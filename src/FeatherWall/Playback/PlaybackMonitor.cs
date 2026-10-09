using System.Text;
using Timer = System.Threading.Timer;
using FeatherWall.Common;
using FeatherWall.Desktop;
using FeatherWall.Interop;
using static FeatherWall.Interop.Win32Constants;

namespace FeatherWall.Playback;

/// <summary>Polls (500 ms) the foreground/system state and reports per-monitor pause
/// transitions. Polling beats WinEvent location hooks here (too chatty/unreliable).
///
/// The poll stops while nobody can see the desktop — see <see cref="SetSuspended"/> — which is the
/// state an unattended machine spends the night in.</summary>
public sealed class PlaybackMonitor : IDisposable
{
    private const int PollMs = 500;

    private readonly Timer _timer;
    private readonly Func<Config.PauseConfig> _config;
    private readonly Dictionary<string, PauseReason> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Serialises the timer with the one other caller: <see cref="SetSuspended"/> takes a
    /// final reading on the thread that is suspending, and without this that reading could mutate
    /// <see cref="_state"/> at the same moment a queued tick is doing the same.</summary>
    private readonly Lock _gate = new();

    private bool _suspended;
    private bool _disposed;

    /// <summary>Set from session-change notifications on the message window.</summary>
    public volatile bool SessionLocked;

    /// <summary>Set from GUID_CONSOLE_DISPLAY_STATE, pushed to the message window. Nothing here
    /// polls for it — the poll only reads the flag someone else was told about.</summary>
    public volatile bool DisplayOff;

    private volatile bool _invalidated;

    /// <summary>Forget cached per-monitor state so the next poll re-fires transitions —
    /// call after creating a renderer while a pause condition may already hold.</summary>
    public void Invalidate() => _invalidated = true;

    /// <summary>(monitorDevice, reason) — reason None means resume. Fires on the timer thread, or
    /// on whichever thread called <see cref="SetSuspended"/> for the final reading.</summary>
    public event Action<string, PauseReason>? PauseStateChanged;

    public PlaybackMonitor(Func<Config.PauseConfig> config)
    {
        _config = config;
        _timer = new Timer(_ => Poll(), null, 1000, PollMs);
    }

    /// <summary>Stops the poll while the desktop is not being looked at — the panel has gone dark,
    /// or the session is locked.
    ///
    /// Everything this poll exists to notice is a foreground window, and there is none to speak of
    /// while nobody is looking: the state it would compute is already known and every renderer is
    /// already meant to be stopped. Left running, it is the only thing in the process that keeps
    /// waking the machine on a timer while it is otherwise doing nothing at all, which is exactly
    /// what "idle CPU near zero" means here.
    ///
    /// One last reading is taken before the timer stops, because the event that triggered the
    /// suspension — the lock arriving, the panel going dark — is itself the reason every renderer
    /// should pause, and this poll is what applies that. The reverse happens on the way back: the
    /// cached per-monitor state is forgotten so the next poll re-fires the resume transitions it
    /// swallowed. Resuming is not the poll's job to discover — display, session and power changes
    /// all arrive as pushed notifications, which is also why stopping it cannot strand anything.</summary>
    public void SetSuspended(bool suspended)
    {
        lock (_gate)
        {
            if (_disposed || _suspended == suspended) return;
            if (!suspended)
            {
                _suspended = false;
                _invalidated = true;
            }
        }

        if (suspended)
        {
            Poll();
            lock (_gate)
            {
                if (_disposed) return;
                _suspended = true;
                try { _timer.Change(Timeout.Infinite, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }
        }
        else
        {
            try { _timer.Change(0, PollMs); }
            catch (ObjectDisposedException) { }
        }
    }

    private void Poll()
    {
        lock (_gate)
        {
            if (_disposed || _suspended) return;
            try
            {
                if (_invalidated)
                {
                    _invalidated = false;
                    _state.Clear();
                }
                var flags = new SystemFlags(
                    SessionLocked,
                    User32.GetSystemMetrics(SM_REMOTESESSION) != 0,
                    IsBatterySaverOn(),
                    IsD3DFullscreen(),
                    DisplayOff);

                var foreground = CaptureForeground();
                var config = _config();

                foreach (var monitor in MonitorTracker.Enumerate())
                {
                    var handle = MonitorHandle(monitor.Bounds);
                    var reason = PauseDecision.Evaluate(foreground, monitor.Bounds, monitor.WorkArea, handle, flags, config);
                    if (!_state.TryGetValue(monitor.Device, out var previous) || previous != reason)
                    {
                        _state[monitor.Device] = reason;
                        PauseStateChanged?.Invoke(monitor.Device, reason);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Playback poll failed", ex);
            }
        }
    }

    private static ForegroundInfo? CaptureForeground()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !User32.IsWindowVisible(hwnd)) return null;
        var sb = new StringBuilder(256);
        User32.GetClassNameW(hwnd, sb, sb.Capacity);
        User32.GetWindowRect(hwnd, out var rect);
        return new ForegroundInfo(sb.ToString(), rect, User32.IsZoomed(hwnd), User32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
    }

    private static IntPtr MonitorHandle(in RECT bounds)
    {
        // MonitorFromWindow needs a window; identify the monitor by a representative point instead.
        var point = new POINT { X = bounds.Left + 1, Y = bounds.Top + 1 };
        return MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    private static bool IsBatterySaverOn() =>
        Kernel32.GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1;

    private static bool IsD3DFullscreen() =>
        Shell32.SHQueryUserNotificationState(out int state) == 0 && state == Shell32.QUNS_RUNNING_D3D_FULL_SCREEN;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer.Dispose();
    }
}
