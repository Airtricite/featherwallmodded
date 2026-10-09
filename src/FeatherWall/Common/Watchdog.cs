using System.Diagnostics;
using FeatherWall.Interop;
using static FeatherWall.Interop.Win32Constants;

namespace FeatherWall.Common;

/// <summary>The guard behind <c>featherwall.exe --watchdog &lt;pid&gt;</c>.
///
/// When FeatherWall wedges, the window is still on screen but nothing answers: clicks land
/// nowhere and the tray icon can no longer be used to quit. Nothing inside the process can clean
/// that up, because the thread that would do it is the one that is stuck. So the guard is a
/// separate process — it pings the app's message window and, when the ping goes unanswered, kills
/// the app and starts it again.
///
/// Two things it deliberately is not. It is not a second instance: <c>--watchdog</c> returns
/// before the singleton mutex is taken, so it never holds the lock and never fights the app it
/// guards. And it is not an unconditional retry loop: restarts are rate-limited, because a bug
/// that kills the app during start-up would otherwise become a crash loop at reboot speed.</summary>
public static class Watchdog
{
    /// <summary>The app's message window. It answers only while the UI thread is retrieving
    /// messages, which is exactly the condition being watched.</summary>
    private const string HeartbeatClass = "FeatherWallMessage";

    private const int PollMs = 3000;
    private const int PingTimeoutMs = 2000;

    /// <summary>Misses before the app counts as wedged. Three polls is nine seconds of silence —
    /// far longer than any legitimate stall, including the multi-second desktop-layer rebuild a
    /// resume from sleep triggers, so a busy-but-alive app is never mistaken for a frozen one.</summary>
    private const int MissesBeforeKill = 3;

    /// <summary>How long a start-up is given to produce a window before its silence starts
    /// counting. Loading the gallery and attaching the desktop layer can take a few seconds on a
    /// cold start, and killing the app mid-load would be worse than not guarding it.</summary>
    private static readonly TimeSpan StartUpGrace = TimeSpan.FromSeconds(30);

    private const int MaxRestarts = 3;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);

    /// <summary>Runs the guard. <paramref name="watchedPid"/> is the app this process was started
    /// to watch. Returns 0 once there is nothing left to guard.</summary>
    public static int Run(int watchedPid)
    {
        Log.Init();
        // A guard is overhead by definition: it must never be the reason the machine feels busy.
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        var main = Find(watchedPid);
        if (main is null) return 0;

        Log.Info($"Watchdog: guarding FeatherWall pid {main.Id}");

        var restarts = new Queue<DateTime>();
        var launched = DateTime.UtcNow;
        bool answered = false;
        int misses = 0;

        while (true)
        {
            bool alive = !main.WaitForExit(PollMs);

            if (alive && Heartbeat(main.Id))
            {
                answered = true;
                misses = 0;
                continue;
            }

            if (!alive)
            {
                int code = main.ExitCode;
                Log.Info($"Watchdog: FeatherWall pid {main.Id} exited (code {code})");
                main.Dispose();
                // Zero is the tray's Quit, or a second launch losing the singleton mutex. Either
                // way the app meant to go and there is nothing left to guard.
                if (code == 0) return 0;
            }
            else
            {
                // Silence before the window has ever answered is a start-up still in progress; the
                // grace period is what keeps a slow load from being killed. Silence after it has
                // answered is the freeze this process exists for.
                if (!answered && DateTime.UtcNow - launched < StartUpGrace) continue;
                if (++misses < MissesBeforeKill) continue;

                Log.Warn($"Watchdog: FeatherWall pid {main.Id} missed {misses} heartbeats — killing it");
                try { main.Kill(entireProcessTree: true); }
                catch (Exception ex) { Log.Warn($"Watchdog: kill failed: {ex.Message}"); }
                main.WaitForExit(PollMs);
                main.Dispose();
            }

            if (!TryReserveRestart(restarts)) return 1;

            main = Launch();
            if (main is null) return 1;
            Log.Info($"Watchdog: FeatherWall restarted as pid {main.Id}");
            launched = DateTime.UtcNow;
            answered = false;
            misses = 0;
        }
    }

    /// <summary>Starts a guard for this instance. Called once during start-up, after the message
    /// window exists so the guard's first heartbeat has something to reach.</summary>
    public static void EnsureRunning()
    {
        try
        {
            var psi = Command();
            psi.ArgumentList.Add("--watchdog");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            // Handle released at once; the guard outlives this app rather than being tied to it.
            var guard = Process.Start(psi);
            guard?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Watchdog: could not start a guard ({ex.Message}) — running unguarded");
        }
    }

    /// <summary>One round trip to the app's UI thread. SendMessageTimeout is the whole point: it
    /// answers only if the thread is retrieving messages, and it gives up on its own, so an app
    /// that has stopped pumping cannot stall the guard as well.</summary>
    private static bool Heartbeat(int pid)
    {
        var hwnd = User32.FindWindowW(HeartbeatClass, null);
        if (hwnd == IntPtr.Zero) return false;
        // After a restart there is briefly a window from the instance that is going away, and
        // answering on its behalf would mask a start-up that never gets anywhere.
        if (User32.GetWindowThreadProcessId(hwnd, out uint owner) == 0 || owner != pid) return false;

        return User32.SendMessageTimeoutW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero,
            SMTO_NORMAL, PingTimeoutMs, out _) != IntPtr.Zero;
    }

    private static Process? Find(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch (ArgumentException)
        {
            return null; // no such process
        }
    }

    /// <summary>Relaunches the app as this process's child, guarded by this same process.</summary>
    private static Process? Launch()
    {
        try
        {
            var psi = Command();
            // --no-watchdog: a guard is already running, and this app is its child.
            psi.ArgumentList.Add("--no-watchdog");
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Error("Watchdog: relaunch failed", ex);
            return null;
        }
    }

    /// <summary>Spends one restart from the budget. Returns false once the budget is gone, which
    /// is the signal to stand down rather than keep restarting an app that cannot stay up.</summary>
    private static bool TryReserveRestart(Queue<DateTime> restarts)
    {
        var now = DateTime.UtcNow;
        while (restarts.Count > 0 && now - restarts.Peek() > RestartWindow) restarts.Dequeue();

        if (restarts.Count >= MaxRestarts)
        {
            Log.Error($"Watchdog: {MaxRestarts} restarts within {RestartWindow.TotalMinutes:0} minutes — standing down");
            return false;
        }

        restarts.Enqueue(now);
        return true;
    }

    /// <summary>How to start this same app again. Under <c>dotnet featherwall.dll</c> the host
    /// process is dotnet itself, so the assembly has to be named after it.</summary>
    private static ProcessStartInfo Command()
    {
        var host = Environment.ProcessPath
            ?? throw new InvalidOperationException("The executable path is unavailable.");

        var psi = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        return psi;
    }
}
