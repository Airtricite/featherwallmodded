using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using Timer = System.Threading.Timer;
using FeatherWall.Common;
using FeatherWall.Config;
using FeatherWall.Desktop;
using FeatherWall.Interop;
using FeatherWall.Playback;
using FeatherWall.Rendering;

namespace FeatherWall.Widgets;

/// <summary>The routine preset: six independently placed overlays composed over the background
/// wallpaper — the hour, the minute, a caption under each, the second ring and the day bar.
///
/// One overlay per block rather than one bitmap for the whole design, because edit mode has to
/// move and resize each block on its own and a single composed bitmap would have to be redrawn
/// in full for every dragged pixel.
///
/// The timer runs at a fixed frame rate rather than on a clock boundary, because everything here
/// moves continuously: the ring fills with the current second and the day bar creeps with the
/// current time, and both want sub-second stepping to look smooth. The bitmaps are reused across
/// frames — at thirty frames a second, allocating six bitmaps per tick would churn the large
/// object heap continuously.</summary>
public sealed class RoutineOverlay : IDisposable
{
    private const string HourKey = "routine.hour";
    private const string MinuteKey = "routine.minute";
    private const string HourLabelKey = "routine.hourLabel";
    private const string MinuteLabelKey = "routine.minLabel";
    private const string RingKey = "routine.ring";
    private const string BarKey = "routine.bar";

    private static readonly string[] AllKeys =
        [HourKey, MinuteKey, HourLabelKey, MinuteLabelKey, RingKey, BarKey];

    /// <summary>Sixty frames a second. The ring and the pointer advance continuously, and anything
    /// slower reads as steps rather than as motion — which is what the second ring was for.</summary>
    private const int ForegroundFrameMs = 16;

    /// <summary>Fifteen frames a second, while an application has the foreground.
    ///
    /// The wallpaper is behind that window, so a full frame rate buys nothing but heat — but the
    /// preset is not hidden by any means, and a ring that stops moving at an alt-tab reads as
    /// broken. Slow, not stopped. Sixty is also the only rate that needs the fine system timer, so
    /// this is where the largest slice of the idle cost comes back.</summary>
    private const int BackgroundFrameMs = 66;

    private readonly CompositionHost _host;
    private readonly RoutineConfig _config;
    private readonly MonitorInfo _monitor;
    private readonly Timer _timer;
    private readonly Lock _sync = new();

    /// <summary>Frame pacing runs off an absolute deadline, so the schedule cannot be dragged later
    /// by how long a frame took to draw.</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _nextFrame;

    /// <summary>Live overlays wanting a 1 ms system timer. Counted rather than set and cleared
    /// because a rebuild constructs the replacement before disposing the old one, and the old one's
    /// Dispose must not cancel the new one's request.</summary>
    private static readonly Lock PrecisionGate = new();
    private static int _precisionUsers;

    private readonly Dictionary<string, Rectangle> _bounds = [];
    private readonly Dictionary<string, Bitmap> _bitmaps = [];

    /// <summary>What each block was last painted with, for the blocks whose content does not depend
    /// on the clock. The hour changes once an hour and its caption never does, so repainting them
    /// sixty times a second - and re-uploading both bitmaps through Direct2D - was most of what a
    /// frame cost. Cleared whenever the config changes, since that is the only other thing that can
    /// change the picture. The ring and the bar pass no stamp and are repainted every frame.</summary>
    private readonly Dictionary<string, string> _stamp = [];

    /// <summary>The angle each block was last drawn at. A rotation drag has to repaint even when
    /// the padded surface comes out the same size — a few degrees on a small box rounds to the
    /// same rectangle — and without this the block would refuse to turn.</summary>
    private readonly Dictionary<string, double> _tilt = [];

    /// <summary>The window the foreground was last seen on, and whether an application owns it.
    /// Held so the class name — a marshalled string — is only fetched when the foreground actually
    /// changes, rather than on every frame.</summary>
    private IntPtr _foreground;
    private bool _background;

    /// <summary>Whether this overlay is currently holding one of the 1 ms leases — see
    /// <see cref="HoldPreciseTimers"/>. Instance state, because whether the process should be holding
    /// one is a question about the others, not about this overlay.</summary>
    private bool _precise;

    private bool _disposed;
    private bool _suspended;
    private bool _paused;

    public RoutineOverlay(CompositionHost host, RoutineConfig config, MonitorInfo monitor)
    {
        _host = host;
        _config = config;
        _monitor = monitor;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        RetunePrecision();
        UpdateFocusProfile();
        Render();
        Arm();
    }

    /// <summary>Asks Windows for a 1 ms system timer, or hands it back.
    ///
    /// The default tick is 15.6 ms, and every wait in the process is quantised to it: a 16 ms
    /// request comes back at 15.6 or 31.2, so the frames land unevenly no matter how exact the
    /// schedule is. That quantisation, not the schedule, is what made the animation look uneven.
    /// Released whenever the animation drops to fifteen frames, while the preset is suspended, and
    /// on Dispose, so a preset that is switched off, one running behind somebody else's window, or
    /// one on a machine nobody is using costs nothing.
    ///
    /// The lease is counted and this overlay remembers whether it is holding one, because a rebuild
    /// has two overlays alive at once — a blind release in the old one's Dispose would hand back the
    /// resolution the replacement is still using.</summary>
    private void HoldPreciseTimers(bool wanted)
    {
        lock (PrecisionGate)
        {
            if (_precise == wanted) return;
            _precise = wanted;
            if (wanted)
            {
                if (_precisionUsers++ == 0) Winmm.TimeBeginPeriod(1);
            }
            else if (_precisionUsers > 0 && --_precisionUsers == 0)
            {
                Winmm.TimeEndPeriod(1);
            }
        }
    }

    /// <summary>Whether the 1 ms system timer is wanted at all, given how the preset is currently
    /// running: sixty frames while it is actually on screen, and nothing else.
    ///
    /// Fifteen frames does not want it — the default 15.6 ms tick is already finer than a 66 ms
    /// interval. A suspended preset wants it least of all, and that is the case that reaches
    /// outside this process: <c>timeBeginPeriod(1)</c> raises the system tick for everything running
    /// on the machine and is a well-known way to keep it out of its deep idle states, so holding the
    /// request while the panel is dark and not one frame is being drawn spends real battery on an
    /// unattended laptop to buy nothing at all.</summary>
    private void RetunePrecision() => HoldPreciseTimers(!_suspended && !_background);

    /// <summary>Repaints because the config changed: every block is due, whatever its stamp says.</summary>
    public void Refresh()
    {
        lock (_sync) _stamp.Clear();
        Render();
        Arm();
    }

    /// <summary>Stops the tick entirely while the desktop is not being looked at — the panel has
    /// gone dark, or the session is locked. Not a throttle: a suspended preset draws nothing at all,
    /// and it hands back its share of the 1 ms system timer on the way down.</summary>
    public void SetSuspended(bool suspended)
    {
        lock (_sync)
        {
            if (_disposed || _suspended == suspended) return;
            _suspended = suspended;
            if (suspended)
            {
                try { _timer.Change(Timeout.Infinite, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }
        }

        // Outside _sync: the 1 ms request is process-wide state, and any other overlay still
        // running at sixty is holding the same lease.
        RetunePrecision();
        if (!suspended) Refresh();
    }

    /// <summary>Stops the free-running animation without stopping repaints.
    ///
    /// Edit mode wants this: while a block is being dragged the frame rate should follow the
    /// mouse, and a 30 Hz timer repainting six blocks underneath the drag both doubles the work
    /// and keeps the render lock busy against the UI thread that is trying to draw the canvas.
    /// The picture is already on screen, so a paused block is still visible and still movable;
    /// <see cref="Refresh"/> and <see cref="SyncLayout"/> keep working either way.
    ///
    /// There is no matching resume. Edit mode rebuilds the overlay on the way out
    /// (<c>Engine.RecreateWidgets</c>, because the blocks moved), and a fresh one animates from its
    /// constructor — so it is the rebuild, not a flag, that starts the clock again.</summary>
    public void PauseAnimation()
    {
        lock (_sync)
        {
            if (_disposed || _paused) return;
            _paused = true;
            try { _timer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Arms the next frame against an absolute deadline rather than "one interval from
    /// now".
    ///
    /// A relative wait measures from the moment the previous frame finished, so every frame's own
    /// drawing time is added to the interval and the error compounds: the picture drifts later and
    /// later and the gap between frames varies with how much there was to draw. Advancing a deadline
    /// by exactly one interval per tick instead means a slow frame is followed by a shorter wait and
    /// the cadence stays even. A frame that overruns by a whole interval is skipped rather than
    /// fired back to back — a burst of catch-up frames is the stutter this is meant to remove.
    ///
    /// The interval is whatever the current profile asks for, and the deadline is re-derived from
    /// it each time, so switching between sixty and fifteen frames takes effect on the next tick
    /// instead of finishing out the old grid.</summary>
    private void Arm()
    {
        lock (_sync)
        {
            if (_disposed || _suspended || _paused) return;
            int interval = _background ? BackgroundFrameMs : ForegroundFrameMs;
            long now = _clock.ElapsedMilliseconds;
            long due = (_nextFrame <= 0 ? now : _nextFrame) + interval;
            if (due <= now) due = now + interval;
            _nextFrame = due;
            int wait = (int)Math.Clamp(due - now, 1, interval);
            try { _timer.Change(wait, Timeout.Infinite); }
            catch (ObjectDisposedException) { /* raced Dispose */ }
        }
    }

    /// <summary>Re-reads whether an application has the foreground, and retunes the frame rate and
    /// the system timer when that changes.
    ///
    /// GetForegroundWindow is a bare export and cheap enough to call every frame; the class name
    /// behind it is a marshalled string, so it is only asked for when the handle actually moved.
    /// Read before the frame is drawn and before the next wait is armed, so a change takes effect on
    /// the frame that noticed it.</summary>
    private void UpdateFocusProfile()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == _foreground) return;
        _foreground = hwnd;

        bool background = ForegroundIsApplication(hwnd);
        if (background == _background) return;
        _background = background;

        // At fifteen frames a second the default 15.6 ms tick is finer than the interval itself, so
        // the 1 ms request buys nothing and only costs wakeups — released here and taken back on the
        // way to sixty. This is most of what the optimisation is worth.
        RetunePrecision();
    }

    /// <summary>True when the foreground window belongs to somebody else's application.
    ///
    /// The desktop, the taskbar and our own windows all count as "no application": the first two are
    /// what the wallpaper is visible through, and the third is the settings or edit canvas, where
    /// somebody is looking straight at the ring and a coarse frame rate would read as a regression.
    /// The process id covers those — they are ordinary WinForms windows with generated class names —
    /// and the class list covers the shell and our own hidden message windows.</summary>
    private static bool ForegroundIsApplication(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !User32.IsWindowVisible(hwnd)) return false;

        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;

        var name = new StringBuilder(256);
        User32.GetClassNameW(hwnd, name, name.Capacity);
        return !PauseDecision.IsShellOrOwnWindow(name.ToString());
    }

    private void Tick()
    {
        try
        {
            UpdateFocusProfile();
            Render();
        }
        catch (Exception ex) { Log.Error("Routine render failed", ex); }
        finally { Arm(); }
    }

    /// <summary>The screen rectangles of the blocks that are currently drawn, for edit mode, along
    /// with the rotation the canvas needs to draw their gizmos tilted the same way.</summary>
    public IReadOnlyList<(string Key, string Label, Rectangle Bounds, double Rotation)> DescribeParts()
    {
        lock (_sync)
        {
            var parts = new List<(string, string, Rectangle, double)>();
            Add(HourKey, "小时", _config.Hour); Add(MinuteKey, "分钟", _config.Minute);
            Add(HourLabelKey, "小时标签", _config.HourLabel); Add(MinuteLabelKey, "分钟标签", _config.MinuteLabel);
            Add(RingKey, "秒环", _config.Ring); Add(BarKey, "日程条", _config.Bar);
            return parts;

            void Add(string key, string label, RoutinePart part)
            {
                if (_bounds.TryGetValue(key, out var rect) && rect.Width > 0 && rect.Height > 0)
                    parts.Add((key, label, rect, part.Rotation));
            }
        }
    }

    private void Render()
    {
        lock (_sync)
        {
            if (_disposed || _suspended) return;
            PaintBlocks(repaint: true);
        }
    }

    /// <summary>Puts the blocks where the config says they are without repainting their contents.
    ///
    /// A drag is geometry-only: the pointer moves a box and the picture inside it is already
    /// drawn. Rendering six GDI+ bitmaps through Direct2D for every dragged pixel is what made the
    /// canvas stutter, so a drag re-syncs offsets here instead. A block whose surface had to change
    /// shape has nothing correct left to show and is redrawn anyway.</summary>
    public void SyncLayout()
    {
        lock (_sync)
        {
            if (_disposed || _suspended) return;
            PaintBlocks(repaint: false);
        }
    }

    private void PaintBlocks(bool repaint)
    {
        var now = DateTime.Now;

        // The stamp is whatever visible thing about the block can change on its own. The ring and
        // the bar move with the second, so they get none and are repainted every frame; the captions
        // depend on nothing that moves, so a constant says "already correct" until something clears
        // the stamps.
        PaintBlock(HourKey, _config.Hour, repaint, RoutineRenderer.HourText(_config, now),
            g => RoutineRenderer.PaintHour(g, _config, now, BlockSize(_config.Hour)));
        PaintBlock(MinuteKey, _config.Minute, repaint, RoutineRenderer.MinuteText(now),
            g => RoutineRenderer.PaintMinute(g, _config, now, BlockSize(_config.Minute)));
        PaintBlock(HourLabelKey, _config.HourLabel, repaint, "caption",
            g => RoutineRenderer.PaintLabel(g, _config, _config.HourLabel, BlockSize(_config.HourLabel)));
        PaintBlock(MinuteLabelKey, _config.MinuteLabel, repaint, "caption",
            g => RoutineRenderer.PaintLabel(g, _config, _config.MinuteLabel, BlockSize(_config.MinuteLabel)));
        PaintBlock(RingKey, _config.Ring, repaint, null,
            g => RoutineRenderer.PaintRing(g, _config, now, BlockSize(_config.Ring)));
        PaintBlock(BarKey, _config.Bar, repaint, null,
            g => RoutineRenderer.PaintBar(g, _config, now, BlockSize(_config.Bar)));

        Size BlockSize(RoutinePart part)
        {
            var area = _monitor.Bounds;
            return new Size(
                Math.Max((int)Math.Round(part.W * area.Width), 1),
                Math.Max((int)Math.Round(part.H * area.Height), 1));
        }
    }

    /// <summary>Renders one block, creating or resizing its overlay only when the geometry
    /// actually moved — the same discipline the clock uses so a frame over video costs nothing but
    /// the redraw into the block's own cached bitmap. <paramref name="repaint"/> false limits the
    /// work to moving the existing surface. <paramref name="stamp"/> is the block's current content
    /// where that content is not the clock: null means "redraw every frame".</summary>
    private void PaintBlock(string key, RoutinePart part, bool repaint, string? stamp, Action<Graphics> paint)
    {
        var area = _monitor.Bounds;

        if (!part.Enabled)
        {
            _bounds.Remove(key);
            _tilt.Remove(key);
            _stamp.Remove(key);
            ReleaseBitmap(key);
            _host.RemoveOverlay(key);
            return;
        }

        int width = Math.Max((int)Math.Round(part.W * area.Width), 1);
        int height = Math.Max((int)Math.Round(part.H * area.Height), 1);
        int offsetX = (int)Math.Round(part.X * area.Width);
        int offsetY = (int)Math.Round(part.Y * area.Height);

        // The bounds describe the un-rotated box: rotation happens inside the bitmap, so what the
        // edit canvas outlines and what a drag moves agree with each other.
        _bounds[key] = new Rectangle(area.Left + offsetX, area.Top + offsetY, width, height);

        // A tilted block needs a surface big enough to hold its own rotated corners, or the
        // ellipse loses its top and bottom the moment anyone angles it. The surface grows around
        // the box's centre and the renderers still draw into an axis-aligned width×height frame.
        var (surface, padX, padY) = SurfaceFor(width, height, part.Rotation);

        var overlay = _host.GetOverlay(key);
        bool resized = overlay is null || overlay.Width != surface.Width || overlay.Height != surface.Height;
        if (overlay is null || resized)
            overlay = _host.CreateOverlay(key, surface.Width, surface.Height,
                offsetX - padX, offsetY - padY);
        else
            overlay.SetOffset(offsetX - padX, offsetY - padY);

        // A moved block keeps its picture, so a plain offset is all the surface needed. A resized
        // one has nothing correct left to show, and a turned one has to be redrawn through the new
        // transform even when the padded surface happens to come out the same size. Past that, a
        // block showing what it already shows is left alone: the picture is on screen and re-rendering
        // it only to upload the same pixels again is the work this is here to avoid.
        bool turned = !_tilt.TryGetValue(key, out var drawn) || Math.Abs(drawn - part.Rotation) > 0.001;
        if (!resized && !turned)
        {
            if (!repaint) return;
            if (stamp is not null && _stamp.TryGetValue(key, out var painted) && painted == stamp) return;
        }

        _tilt[key] = part.Rotation;
        if (stamp is not null) _stamp[key] = stamp;

        var frame = Buffer(key, surface.Width, surface.Height);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.Transparent);
            ApplyRotation(g, part.Rotation, surface, width, height);
            paint(g);
        }
        overlay.PresentBitmap(frame);
    }

    /// <summary>The surface a block needs, and how far its top-left corner sits above and left of
    /// the block's own box. Unrotated this is exactly the box with a zero inset.</summary>
    private static (Size Surface, int PadX, int PadY) SurfaceFor(int width, int height, double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Abs(Math.Cos(radians));
        double sin = Math.Abs(Math.Sin(radians));
        int padX = Math.Max((int)Math.Ceiling(width * cos + height * sin) - width, 0);
        int padY = Math.Max((int)Math.Ceiling(width * sin + height * cos) - height, 0);
        return (new Size(width + padX, height + padY), padX / 2, padY / 2);
    }

    /// <summary>Tilts the block's own frame about its centre, so every renderer downstream can
    /// draw as if nothing were rotated. The box is centred in the surface first, which makes this
    /// the same rotation either way round.</summary>
    private static void ApplyRotation(Graphics g, double degrees, Size surface, int width, int height)
    {
        if (Math.Abs(degrees) < 0.01) return;
        g.TranslateTransform(surface.Width / 2f, surface.Height / 2f);
        g.RotateTransform((float)degrees);
        g.TranslateTransform(-width / 2f, -height / 2f);
    }

    private Bitmap Buffer(string key, int width, int height)
    {
        if (_bitmaps.TryGetValue(key, out var frame) && frame.Width == width && frame.Height == height)
            return frame;

        frame?.Dispose();
        var created = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        _bitmaps[key] = created;
        return created;
    }

    private void ReleaseBitmap(string key)
    {
        if (_bitmaps.Remove(key, out var frame)) frame.Dispose();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            foreach (var key in AllKeys)
            {
                try { _host.RemoveOverlay(key); } catch { /* host may already be gone */ }
                ReleaseBitmap(key);
            }
            _bounds.Clear();
            _tilt.Clear();
            _stamp.Clear();
        }
        HoldPreciseTimers(false);
    }
}
