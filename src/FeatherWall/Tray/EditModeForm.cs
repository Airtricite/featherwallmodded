using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FeatherWall.Common;
using FeatherWall.Interop;

namespace FeatherWall.Tray;

/// <summary>One draggable block on the edit-mode canvas. <see cref="Apply"/> receives the new
/// screen rectangle and is the only thing that knows how that maps back to config — the clock
/// stores a placement fraction, a routine block stores four of them.
///
/// <see cref="Rotation"/> is the block's current tilt in degrees and
/// <see cref="ApplyRotation"/> is non-null only for blocks that can be rotated; the canvas grows a
/// rotation handle for those and leaves the others alone.</summary>
public sealed class EditTarget
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required Rectangle Bounds { get; init; }
    public required bool Resizable { get; init; }
    public required Action<Rectangle> Apply { get; init; }
    public double Rotation { get; init; }
    public Action<double>? ApplyRotation { get; init; }
}

/// <summary>The edit-mode canvas: one borderless, topmost window across the whole virtual screen
/// that outlines every widget and turns mouse drags into config changes.
///
/// It has to be a real window. The widgets themselves are click-through by construction — they are
/// DirectComposition visuals on the desktop layer, which never sees input — so there is nothing on
/// the wallpaper to grab. This window sits above the desktop only while edit mode is open.
///
/// It is a per-pixel-alpha layered window, not an ordinary form. An ordinary form is opaque, and
/// painting a translucent wash onto an opaque surface just yields black: the wallpaper being
/// arranged would be invisible. UpdateLayeredWindow is the only way to dim without hiding, and it
/// also keeps an alpha-70 wash hit-testable, so a drag anywhere in a widget's box lands on us
/// rather than falling through to the desktop icons underneath.
///
/// The frame is pushed explicitly with <see cref="Present"/> rather than from OnPaint. FeatherWall
/// runs its own GetMessage/DispatchMessage pump instead of Application.Run, and a layered window
/// under that pump cannot be relied on to receive WM_PAINT: without an explicit push the canvas
/// stays fully transparent while still swallowing every click, which is how edit mode used to
/// wedge the desktop.</summary>
public sealed class EditModeForm : Form
{
    private const int HandleSize = 10;
    private const int MinWidth = 24;
    private const int MinHeight = 16;
    private const int RotationHandleRadius = 7;
    /// <summary>Distance from the block's edge out to the middle of the rotation handle.</summary>
    private const int RotationHandleGap = 26;
    private const byte DimAlpha = 70;
    private const int ToolbarHeight = 48;

    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    /// <summary>The eight gizmo handles, clockwise from the top-left corner.</summary>
    private enum Grip { None, NW, N, NE, E, SE, S, SW, W }

    private readonly Func<IReadOnlyList<EditTarget>> _targets;
    private readonly Action _changed;
    private readonly Action _pickBackground;

    private Bitmap? _buffer;
    private Rectangle _doneButton;
    private Rectangle _pickButton;

    /// <summary>Coalesces repaints. A drag delivers a move message per pixel, and composing plus
    /// pushing a virtual-screen-sized frame on each one is what made the canvas feel wedged; the
    /// timer turns a burst of moves into one frame per tick.
    ///
    /// A WinForms timer rather than an idle-time post because it arrives as WM_TIMER through
    /// FeatherWall's hand-written GetMessage loop, which is the one message source the layered
    /// window already proves it receives.</summary>
    private readonly System.Windows.Forms.Timer _frame = new() { Interval = 16 };
    private bool _dirty;
    private bool _moved;

    /// <summary>One memory DC and one DIB section, kept for the life of the canvas. The previous
    /// version built a screen-compatible bitmap of the whole virtual screen — tens of megabytes —
    /// and threw it away on every frame.</summary>
    private IntPtr _surfaceDc;
    private IntPtr _surfaceDib;
    private IntPtr _surfaceBits;
    private IntPtr _surfacePrevious;
    private SIZE _surfaceSize;

    /// <summary>The target list from the last frame. Hit-testing reuses it instead of asking the
    /// engine to re-walk the monitors on every mouse move.</summary>
    private IReadOnlyList<EditTarget> _cache = [];

    private EditTarget? _active;
    private Rectangle _preview;
    private Point _grab;
    private Grip _grip;
    private bool _rotating;

    public EditModeForm(Func<IReadOnlyList<EditTarget>> targets, Action changed, Action pickBackground)
    {
        _targets = targets;
        _changed = changed;
        _pickBackground = pickBackground;

        Text = "FeatherWall 编辑模式";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _frame.Tick += (_, _) =>
        {
            _frame.Stop();
            if (!_dirty) return;
            _dirty = false;
            // The widgets follow the boxes here rather than inside the mouse handler: a mouse
            // delivers a move per pixel, and re-syncing four widgets plus the schedule blocks on
            // every one of them is what left the canvas unable to keep up with the cursor.
            if (_moved)
            {
                _moved = false;
                _changed();
            }
            PresentFrame();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        Focus();
        PresentFrame();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Redraws and pushes the frame. The engine calls this when something outside the
    /// canvas changed a widget — picking a background, for instance.</summary>
    public void Repaint() => PresentFrame();

    /// <summary>Nothing is drawn through the normal paint path — the whole frame is composed into
    /// a bitmap and pushed to the window with UpdateLayeredWindow.</summary>
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) => PresentFrame();

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _buffer?.Dispose();
        _buffer = null;
        ReleaseSurface();
    }

    private void PresentFrame()
    {
        if (!IsHandleCreated || !Visible) return;
        Present(Compose());
    }

    /// <summary>Asks for a repaint on the next frame instead of painting inside the mouse handler.
    ///
    /// Coalescing is the difference between a canvas that follows the cursor and one that spends
    /// the whole drag inside UpdateLayeredWindow: mouse moves arrive faster than a virtual-screen
    /// frame can be composed and pushed, so each new move lands while the previous frame is being
    /// drawn and the queue never drains. <paramref name="moved"/> also defers the widget sync to the
    /// same tick.</summary>
    private void RequestFrame(bool moved = false)
    {
        _dirty = true;
        _moved |= moved;
        if (!_frame.Enabled) _frame.Start();
    }

    /// <summary>The frame, drawn into a reused buffer so a drag costs no allocation beyond the
    /// alpha blend itself.</summary>
    private Bitmap Compose()
    {
        if (_buffer is null || _buffer.Width != Width || _buffer.Height != Height)
        {
            _buffer?.Dispose();
            // Premultiplied: UpdateLayeredWindow expects premultiplied alpha, and GDI+ premultiplies
            // as it composites onto a PArgb surface.
            _buffer = new Bitmap(Math.Max(Width, 1), Math.Max(Height, 1), PixelFormat.Format32bppPArgb);
        }

        var g = Graphics.FromImage(_buffer);
        try
        {
            g.Clear(Color.Transparent);

            using (var dim = new SolidBrush(Color.FromArgb(DimAlpha, 0, 0, 0)))
                g.FillRectangle(dim, 0, 0, _buffer.Width, _buffer.Height);

            _cache = _targets();
            DrawTargets(g, _cache);
            DrawToolbar(g, _cache.Count == 0);
        }
        finally
        {
            g.Dispose();
        }
        return _buffer;
    }

    private void DrawTargets(Graphics g, IReadOnlyList<EditTarget> targets)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (targets.Count == 0)
        {
            using var empty = new SolidBrush(Color.FromArgb(230, 235, 240, 250));
            using var font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            g.DrawString("没有可编辑的组件：请先在设置里打开时钟 / 挂件 / 日程预设。", font, empty, 20, ToolbarHeight + 18);
            return;
        }

        using var labelFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        foreach (var target in targets)
        {
            bool active = _active is not null && target.Key == _active.Key;
            var rect = active ? _preview : target.Bounds;
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            var border = active ? Color.FromArgb(255, 120, 190, 255) : Color.FromArgb(200, 205, 215, 230);

            // The block's own frame is tilted, so the outline, the handles and the rotation stem
            // are all drawn through the same transform the renderer uses. The label stays upright.
            var state = g.Save();
            Rotate(g, rect, target.Rotation);
            try
            {
                using (var pen = new Pen(border, active ? 2f : 1f) { DashStyle = active ? DashStyle.Solid : DashStyle.Dash })
                    g.DrawRectangle(pen, rect);

                if (target.Resizable)
                {
                    var handles = HandleRects(rect);
                    using var fill = new SolidBrush(Color.FromArgb(255, 120, 190, 255));
                    using var edge = new Pen(Color.FromArgb(255, 20, 24, 32), 1f);
                    for (int i = 0; i < handles.Length; i++)
                    {
                        g.FillRectangle(fill, handles[i]);
                        g.DrawRectangle(edge, handles[i]);
                    }
                }

                if (target.ApplyRotation is not null)
                {
                    var hub = RotationHub(rect);
                    using (var stem = new Pen(border, 1.5f))
                        g.DrawLine(stem, rect.Left + rect.Width / 2f, rect.Top, hub.X, hub.Y + RotationHandleRadius);
                    using var knob = new SolidBrush(Color.FromArgb(255, 255, 214, 120));
                    using var rim = new Pen(Color.FromArgb(255, 20, 24, 32), 1f);
                    g.FillEllipse(knob, hub.X - RotationHandleRadius, hub.Y - RotationHandleRadius,
                        RotationHandleRadius * 2, RotationHandleRadius * 2);
                    g.DrawEllipse(rim, hub.X - RotationHandleRadius, hub.Y - RotationHandleRadius,
                        RotationHandleRadius * 2, RotationHandleRadius * 2);
                }
            }
            finally
            {
                g.Restore(state);
            }

            using (var text = new SolidBrush(border))
                g.DrawString(target.Label, labelFont, text, rect.Left, Math.Max(rect.Top - 18, ToolbarHeight + 4));
        }
    }

    private void DrawToolbar(Graphics g, bool empty)
    {
        g.SmoothingMode = SmoothingMode.None;
        using (var bar = new SolidBrush(Color.FromArgb(238, 18, 20, 26)))
            g.FillRectangle(bar, 0, 0, Width, ToolbarHeight);

        using var font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var text = new SolidBrush(Color.FromArgb(235, 235, 240, 250));
        var hint = empty
            ? "没有可编辑的组件 · Esc 完成"
            : "拖拽移动 · 手柄缩放（8 向）· 顶部圆点旋转 · Esc 完成";
        g.DrawString(hint, font, text, 18, ToolbarHeight / 2f - 10);

        _doneButton = DrawButton(g, "完成 (Esc)", 0);
        _pickButton = DrawButton(g, "选择底图…", 1);
    }

    /// <summary>Buttons are laid out from the right edge, so the strip still reads correctly on a
    /// virtual screen whose origin is not at 0,0.</summary>
    private Rectangle DrawButton(Graphics g, string caption, int indexFromRight)
    {
        const int width = 116;
        const int margin = 8;
        var rect = new Rectangle(
            Width - margin - width * (indexFromRight + 1) - margin * indexFromRight,
            margin, width, ToolbarHeight - margin * 2);

        using (var fill = new SolidBrush(indexFromRight == 1 ? Color.FromArgb(255, 52, 96, 168) : Color.FromArgb(255, 44, 48, 58)))
            g.FillRectangle(fill, rect);

        using var font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        using var brush = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(caption, font, brush, rect, format);
        return rect;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (_doneButton.Contains(e.Location)) { Close(); return; }
        if (_pickButton.Contains(e.Location)) { _pickBackground(); PresentFrame(); return; }

        var targets = _cache;

        // Rotation first: its handle sits outside the block, but on a tilted block it can land
        // over a neighbour, so it takes precedence over everything else.
        for (int i = targets.Count - 1; i >= 0; i--)
        {
            var target = targets[i];
            if (target.ApplyRotation is null) continue;
            if (!HitsRotationHandle(target.Bounds, target.Rotation, e.Location)) continue;
            _active = target;
            _preview = target.Bounds;
            _rotating = true;
            _grip = Grip.None;
            _grab = e.Location;
            PresentFrame();
            return;
        }

        // Handles next: a handle overlaps the body, so testing the body first would make the
        // edges and corners impossible to grab.
        for (int i = targets.Count - 1; i >= 0; i--)
        {
            var target = targets[i];
            if (!target.Resizable) continue;
            var grip = GripAt(target.Bounds, target.Rotation, e.Location);
            if (grip == Grip.None) continue;
            _active = target;
            _preview = target.Bounds;
            _rotating = false;
            _grip = grip;
            _grab = e.Location;
            PresentFrame();
            return;
        }

        for (int i = targets.Count - 1; i >= 0; i--)
        {
            var target = targets[i];
            if (!Contains(target.Bounds, target.Rotation, e.Location)) continue;
            _active = target;
            _preview = target.Bounds;
            _rotating = false;
            _grip = Grip.None;
            _grab = e.Location;
            PresentFrame();
            return;
        }

        _active = null;
        PresentFrame();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_active is null)
        {
            Cursor = CursorFor(e.Location);
            return;
        }

        if (_rotating)
        {
            float cx = _preview.Left + _preview.Width / 2f;
            float cy = _preview.Top + _preview.Height / 2f;
            // The handle starts straight up, so "up" is zero and the angle grows clockwise —
            // the block follows the pointer rather than needing a grab offset.
            double angle = Math.Atan2(e.Y - cy, e.X - cx) * 180.0 / Math.PI + 90.0;
            _active.ApplyRotation?.Invoke(Normalise(angle));
            RequestFrame(moved: true);
            return;
        }

        var delta = new PointF(e.X - _grab.X, e.Y - _grab.Y);
        _grab = e.Location;
        double rotation = _active.Rotation;
        bool tilted = Math.Abs(rotation) >= 0.01;

        if (_grip == Grip.None)
        {
            _preview = new Rectangle(
                _preview.Left + (int)Math.Round(delta.X), _preview.Top + (int)Math.Round(delta.Y),
                _preview.Width, _preview.Height);
        }
        else
        {
            // The handles are laid out in the block's own frame, so a drag on a tilted block has to
            // be read there too — otherwise the box grows along the screen axes instead of its own.
            var local = tilted ? RotateVector(delta, -rotation) : delta;
            var resized = Resized(_preview, _grip, local);
            _preview = tilted ? ReCentre(_preview, resized, rotation) : resized;
        }

        // Applied live rather than on release: the widget is visible under the outline, and
        // dragging a box that the real thing does not follow is a preview that lies.
        _active.Apply(_preview);
        RequestFrame(moved: true);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_active is null) return;
        // A rotation drag already wrote its angle on every move; re-applying the old rectangle
        // here would undo nothing but would churn the config for no reason.
        if (!_rotating)
        {
            _active.Apply(_preview);
            _changed();
        }
        _active = null;
        _grip = Grip.None;
        _rotating = false;
        PresentFrame();
    }

    private Cursor CursorFor(Point location)
    {
        if (_doneButton.Contains(location) || _pickButton.Contains(location)) return Cursors.Hand;
        foreach (var target in _cache)
        {
            if (target.ApplyRotation is not null && HitsRotationHandle(target.Bounds, target.Rotation, location))
                return Cursors.Hand;
            if (!target.Resizable) continue;
            switch (GripAt(target.Bounds, target.Rotation, location))
            {
                case Grip.NW or Grip.SE: return Cursors.SizeNWSE;
                case Grip.NE or Grip.SW: return Cursors.SizeNESW;
                case Grip.N or Grip.S: return Cursors.SizeNS;
                case Grip.E or Grip.W: return Cursors.SizeWE;
            }
        }
        return Cursors.Default;
    }

    // ---- rotation-aware geometry -----------------------------------------------------------

    /// <summary>Maps a screen point into the block's un-rotated frame, so hit-testing can compare
    /// it against the plain rectangle and the plain handle positions.</summary>
    private static PointF ToLocal(Rectangle body, double rotation, Point p)
    {
        float cx = body.Left + body.Width / 2f;
        float cy = body.Top + body.Height / 2f;
        if (Math.Abs(rotation) < 0.01) return new PointF(p.X, p.Y);

        double radians = -rotation * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double dx = p.X - cx, dy = p.Y - cy;
        return new PointF(cx + (float)(dx * cos - dy * sin), cy + (float)(dx * sin + dy * cos));
    }

    private static void Rotate(Graphics g, Rectangle body, double rotation)
    {
        if (Math.Abs(rotation) < 0.01) return;
        float cx = body.Left + body.Width / 2f;
        float cy = body.Top + body.Height / 2f;
        g.TranslateTransform(cx, cy);
        g.RotateTransform((float)rotation);
        g.TranslateTransform(-cx, -cy);
    }

    /// <summary>Middle of the rotation knob, straight above the block in its own frame.</summary>
    private static PointF RotationHub(Rectangle body) =>
        new(body.Left + body.Width / 2f, body.Top - RotationHandleGap);

    private static bool HitsRotationHandle(Rectangle body, double rotation, Point p)
    {
        var local = ToLocal(body, rotation, p);
        var hub = RotationHub(body);
        double dx = local.X - hub.X, dy = local.Y - hub.Y;
        double reach = RotationHandleRadius + 3;
        return dx * dx + dy * dy <= reach * reach;
    }

    private static bool Contains(Rectangle body, double rotation, Point p) =>
        body.Contains(Point.Round(ToLocal(body, rotation, p)));

    private static double Normalise(double degrees)
    {
        degrees %= 360.0;
        if (degrees < 0) degrees += 360.0;
        return Math.Round(degrees, 1);
    }

    /// <summary>The eight handles for a box: four corners and four edge midpoints.</summary>
    private static Rectangle[] HandleRects(Rectangle b)
    {
        int h = HandleSize;
        int half = h / 2;
        int cx = b.Left + b.Width / 2 - half;
        int cy = b.Top + b.Height / 2 - half;
        return
        [
            new Rectangle(b.Left - half, b.Top - half, h, h),          // NW
            new Rectangle(cx, b.Top - half, h, h),                      // N
            new Rectangle(b.Right - half, b.Top - half, h, h),          // NE
            new Rectangle(b.Right - half, cy, h, h),                    // E
            new Rectangle(b.Right - half, b.Bottom - half, h, h),       // SE
            new Rectangle(cx, b.Bottom - half, h, h),                   // S
            new Rectangle(b.Left - half, b.Bottom - half, h, h),        // SW
            new Rectangle(b.Left - half, cy, h, h),                     // W
        ];
    }

    private static Grip GripAt(Rectangle body, double rotation, Point p)
    {
        var local = Point.Round(ToLocal(body, rotation, p));
        var handles = HandleRects(body);
        for (int i = 0; i < handles.Length; i++)
            if (handles[i].Contains(local)) return (Grip)(i + 1);
        return Grip.None;
    }

    private static Rectangle Resized(Rectangle start, Grip grip, PointF delta)
    {
        float left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        switch (grip)
        {
            case Grip.NW: left += delta.X; top += delta.Y; break;
            case Grip.N: top += delta.Y; break;
            case Grip.NE: right += delta.X; top += delta.Y; break;
            case Grip.E: right += delta.X; break;
            case Grip.SE: right += delta.X; bottom += delta.Y; break;
            case Grip.S: bottom += delta.Y; break;
            case Grip.SW: left += delta.X; bottom += delta.Y; break;
            case Grip.W: left += delta.X; break;
        }

        int l = (int)Math.Round(left), t = (int)Math.Round(top);
        int r = (int)Math.Round(right), b = (int)Math.Round(bottom);

        // Clamp against the edge being dragged, so the box never inverts.
        if (r - l < MinWidth)
        {
            if (grip is Grip.NW or Grip.SW or Grip.W) l = r - MinWidth;
            else r = l + MinWidth;
        }
        if (b - t < MinHeight)
        {
            if (grip is Grip.NW or Grip.NE or Grip.N) t = b - MinHeight;
            else b = t + MinHeight;
        }
        return Rectangle.FromLTRB(l, t, r, b);
    }

    /// <summary>Rotates an offset into or out of the block's own frame. The inverse of
    /// <see cref="ToLocal"/>, which does the same for absolute points.</summary>
    private static PointF RotateVector(PointF v, double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        return new PointF(
            (float)(v.X * cos - v.Y * sin),
            (float)(v.X * sin + v.Y * cos));
    }

    /// <summary>A resized box's centre, moved where the local drag put it.
    ///
    /// Resizing is computed in both frames at once: the new width and height come from the local
    /// delta, but the renderer and the canvas both rotate about the box's centre. Without this the
    /// centre would stay on the screen axes while the pointer travelled along the block's, and the
    /// handles would slide out from under the cursor.</summary>
    private static Rectangle ReCentre(Rectangle before, Rectangle after, double rotation)
    {
        var offset = new PointF(
            after.Left - before.Left + (after.Width - before.Width) / 2f,
            after.Top - before.Top + (after.Height - before.Height) / 2f);
        var moved = RotateVector(offset, rotation);

        float cx = before.Left + before.Width / 2f + moved.X;
        float cy = before.Top + before.Height / 2f + moved.Y;
        return new Rectangle(
            (int)Math.Round(cx - after.Width / 2f), (int)Math.Round(cy - after.Height / 2f),
            after.Width, after.Height);
    }

    // ---- layered presentation --------------------------------------------------------------

    private void Present(Bitmap frame)
    {
        if (!IsHandleCreated) return;
        EnsureSurface(frame.Width, frame.Height);
        if (_surfaceDc == IntPtr.Zero) return;

        CopyToSurface(frame);

        IntPtr screenDc = GetDC(IntPtr.Zero);
        try
        {
            var size = _surfaceSize;
            var source = new POINT { X = 0, Y = 0 };
            // Screen coordinates: Bounds is the virtual screen, so Left/Top are usually negative
            // on a multi-monitor desk and must be passed through as-is.
            var destination = new POINT { X = Bounds.Left, Y = Bounds.Top };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };

            UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, _surfaceDc, ref source, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Copies the composed frame into the DIB section, row by row from each side's own
    /// stride. The formats are both premultiplied BGRA, so it is a straight byte copy.</summary>
    private void CopyToSurface(Bitmap frame)
    {
        int row = frame.Width * 4;
        var bits = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            unsafe
            {
                byte* src = (byte*)bits.Scan0;
                byte* dst = (byte*)_surfaceBits;
                for (int y = 0; y < frame.Height; y++)
                    Buffer.MemoryCopy(src + (long)y * bits.Stride, dst + (long)y * row, row, row);
            }
        }
        finally
        {
            frame.UnlockBits(bits);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frame.Stop();
            _frame.Dispose();
            _buffer?.Dispose();
            _buffer = null;
            ReleaseSurface();
        }
        base.Dispose(disposing);
    }

    // ---- the surface ------------------------------------------------------------------------

    /// <summary>Size-matched memory DC with a top-down 32bpp DIB section selected, created once
    /// and written into per frame. UpdateLayeredWindow needs a DC, and a DIB section is the only
    /// GDI surface that carries the per-pixel alpha the blend function asks for.</summary>
    private void EnsureSurface(int width, int height)
    {
        if (_surfaceDc != IntPtr.Zero && _surfaceSize.Cx == width && _surfaceSize.Cy == height) return;
        ReleaseSurface();

        var screenDc = GetDC(IntPtr.Zero);
        try { _surfaceDc = CreateCompatibleDC(screenDc); }
        finally { ReleaseDC(IntPtr.Zero, screenDc); }

        var info = new BITMAPINFO
        {
            Header = new BITMAPINFOHEADER
            {
                Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                Width = width,
                // Negative height: a top-down DIB, matching GDI+'s row order so the copy below is a
                // straight memcpy rather than a flip.
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BI_RGB,
            },
        };

        _surfaceDib = CreateDIBSection(_surfaceDc, ref info, DIB_RGB_COLORS, out _surfaceBits, IntPtr.Zero, 0);
        if (_surfaceDib == IntPtr.Zero)
        {
            Log.Error($"Edit canvas could not allocate a {width}×{height} DIB section");
            DeleteDC(_surfaceDc);
            _surfaceDc = IntPtr.Zero;
            return;
        }

        _surfacePrevious = SelectObject(_surfaceDc, _surfaceDib);
        _surfaceSize = new SIZE { Cx = width, Cy = height };
    }

    private void ReleaseSurface()
    {
        if (_surfaceDc != IntPtr.Zero)
        {
            if (_surfaceDib != IntPtr.Zero) SelectObject(_surfaceDc, _surfacePrevious);
            DeleteDC(_surfaceDc);
            _surfaceDc = IntPtr.Zero;
        }
        if (_surfaceDib != IntPtr.Zero)
        {
            DeleteObject(_surfaceDib);
            _surfaceDib = IntPtr.Zero;
        }
        _surfaceBits = IntPtr.Zero;
        _surfaceSize = default;
    }

    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
