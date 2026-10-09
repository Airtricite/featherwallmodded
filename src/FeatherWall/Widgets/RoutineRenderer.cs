using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using FeatherWall.Config;

namespace FeatherWall.Widgets;

/// <summary>Draws every block of the routine preset. One static method per block, all of them
/// pure functions of (box size, config, now) so the layout edit mode writes back is exactly the
/// layout that renders — the same rule the clock preview follows.
///
/// Text goes through GraphicsPath rather than DrawString because a gradient fill needs a path to
/// fill; a flat colour simply fills the same path with a solid brush, so both cases share one
/// centring routine.
///
/// Rotation is not handled here. The overlay applies it to the Graphics before calling in, so
/// every method below can work in the block's own un-rotated frame.</summary>
public static class RoutineRenderer
{
    private const float TextHeightFraction = 0.86f;

    // ---- digits & captions -----------------------------------------------------------------

    public static Font DigitFont(RoutineTextStyle style, float boxHeight)
    {
        float size = Math.Max(boxHeight * TextHeightFraction, 6f);
        return CreateFont(style.FontFamily, size);
    }

    /// <summary>The hour block, styled by HourDigits and centred in whatever box edit mode left.</summary>
    public static void PaintHour(Graphics g, RoutineConfig config, DateTime now, Size box) =>
        PaintText(g, config.HourDigits, HourText(config, now), box);

    /// <summary>The minute block, styled by MinuteDigits — deliberately a separate style.</summary>
    public static void PaintMinute(Graphics g, RoutineConfig config, DateTime now, Size box) =>
        PaintText(g, config.MinuteDigits, MinuteText(now), box);

    /// <summary>What the hour block is currently showing. Public because the overlay only repaints
    /// that block when the text changes, so the two have to agree on what counts as a change.</summary>
    public static string HourText(RoutineConfig config, DateTime now)
    {
        int hour = config.TwentyFourHour ? now.Hour : (now.Hour % 12 == 0 ? 12 : now.Hour % 12);
        return hour.ToString(config.TwentyFourHour ? "00" : "0");
    }

    /// <summary>What the minute block is currently showing — see <see cref="HourText"/>.</summary>
    public static string MinuteText(DateTime now) => now.Minute.ToString("00");

    /// <summary>A small caption under a digit block. The text lives on the part so edit mode can
    /// drag and scale it like any other block.</summary>
    public static void PaintLabel(Graphics g, RoutineConfig config, RoutinePart part, Size box)
    {
        string text = string.IsNullOrWhiteSpace(part.Text) ? "" : part.Text;
        if (text.Length == 0) return;
        PaintText(g, config.LabelStyle, text, box);
    }

    /// <summary>Fills <paramref name="text"/> centred in the box, flat or with a two-stop gradient
    /// at the configured angle, scaled by the style's single opacity.</summary>
    private static void PaintText(Graphics g, RoutineTextStyle style, string text, Size box)
    {
        if (box.Width <= 0 || box.Height <= 0) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        using var font = DigitFont(style, box.Height);
        using var path = new GraphicsPath();
        try
        {
            path.AddString(text, font.FontFamily, (int)font.Style, font.Size, new PointF(0, 0),
                StringFormat.GenericTypographic);
        }
        catch (ArgumentException)
        {
            return; // no glyphs for this run — nothing to draw
        }

        var ink = path.GetBounds();
        if (ink.Width <= 0f || ink.Height <= 0f) return;

        using (var move = new Matrix())
        {
            move.Translate(box.Width / 2f - (ink.Left + ink.Width / 2f),
                           box.Height / 2f - (ink.Top + ink.Height / 2f));
            path.Transform(move);
        }

        using var brush = TextBrush(style, new RectangleF(0, 0, box.Width, box.Height));
        g.FillPath(brush, path);
    }

    private static Brush TextBrush(RoutineTextStyle style, RectangleF rect)
    {
        int alpha = Alpha(style.Opacity);
        var first = WithAlpha(ClockRenderer.ParseColor(style.Color), alpha);
        if (!style.Gradient) return new SolidBrush(first);

        var second = WithAlpha(ClockRenderer.ParseColor(style.Color2), alpha);
        try { return new LinearGradientBrush(rect, first, second, (float)style.Angle); }
        catch (ArgumentException) { return new SolidBrush(first); }
    }

    // ---- ring ------------------------------------------------------------------------------

    /// <summary>The ring is an arc segment rather than a closed circle: an ellipse inscribed in the
    /// block, drawn from the style's StartAngle through its SweepAngle, tinted from Color to Color2
    /// along the arc so both ends can carry different colours. The block's own Rotation tilts it,
    /// which is how a wide box with a 180° sweep becomes a slanted half ellipse.
    ///
    /// The progress sweep is the current fraction of the minute — the current second over sixty.</summary>
    public static void PaintRing(Graphics g, RoutineConfig config, DateTime now, Size box)
    {
        if (box.Width <= 0 || box.Height <= 0) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        var style = config.RingStyle;
        int alpha = Alpha(style.Opacity);

        float unit = Math.Min(box.Width, box.Height);
        float thickness = ThicknessFor(style.Style, Math.Max(unit * (float)style.Thickness, 2f));
        float inset = thickness * GlowReach(style.Style) + 1f;
        var rect = new RectangleF(inset, inset,
            Math.Max(box.Width - inset * 2f, 1f), Math.Max(box.Height - inset * 2f, 1f));

        float start = (float)style.StartAngle;
        float sweepTotal = (float)Math.Clamp(Math.Abs(style.SweepAngle), 1.0, 360.0);
        // A negative sweep runs anticlockwise; GDI+ takes that as a negative angle.
        if (style.SweepAngle < 0) sweepTotal = -sweepTotal;

        // The track is the empty ring: it does not move with the second, so it never fades out. It
        // wears the same two end feathers as the stroke, so a full lap lands on exactly the shape
        // the empty ring already had and nothing shifts at the seam.
        var track = WithAlpha(ClockRenderer.ParseColor(style.TrackColor), Alpha(style.TrackOpacity));
        if (style.HeadFade == 0 && style.TailFade == 0)
        {
            // Nothing to feather, so one stroke is both identical output and a fraction of the
            // per-segment pen churn the ranged path costs.
            using (var pen = new Pen(track, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, rect, start, sweepTotal);
        }
        else
        {
            var rail = new Arc(rect, style, Math.Abs(sweepTotal), Math.Abs(sweepTotal), thickness);
            DrawPattern(g, rail, start, sweepTotal, track, track, LineCap.Round, 0f, 0f);
        }

        float fraction = (now.Second * 1000f + now.Millisecond) / 60000f;
        float sweep = sweepTotal * Math.Clamp(fraction, 0f, 1f);

        var arc = new Arc(rect, style, Math.Abs(sweep), Math.Abs(sweepTotal), thickness);
        var c1 = WithAlpha(ClockRenderer.ParseColor(style.Color), alpha);
        var c2 = WithAlpha(ClockRenderer.ParseColor(style.Color2), alpha);

        // The lap that just ended, unwinding instead of evaporating: its head stays on the point it
        // reached and its tail runs forward after it, so the stroke retracts into its own endpoint
        // and the new one grows out of the start behind it.
        //
        // Nothing jumps at the seam. On the first frame of the new minute the ghost is still the
        // whole lap at full opacity — pixel for pixel the last frame of the old one — and by the
        // time it has shrunk to nothing there is nothing left to lose. Crossfading it would have
        // been cheaper, but a lap that dims in place is still a lap that vanishes; carrying it on
        // to its endpoint makes the seam the motion the ring was already making.
        float retract = Math.Clamp(style.LoopRetract, 0, 45) / 100f;
        if (retract > 0f && fraction < retract)
        {
            float keep = sweepTotal * (1f - Smooth(fraction / retract));
            if (Math.Abs(keep) > 0.05f)
            {
                var ghost = new Arc(rect, style, Math.Abs(keep), Math.Abs(sweepTotal), thickness);
                PaintArc(g, ghost, start + sweepTotal - keep, keep, c1, c2, alpha);
            }
        }

        if (Math.Abs(sweep) <= 0.05f) return;
        PaintArc(g, arc, start, sweep, c1, c2, alpha);
    }

    /// <summary>Everything one frame's stroke shares, so the four call levels below do not each
    /// carry a dozen parameters. <see cref="Run"/> is the arc actually being drawn this second and
    /// <see cref="Full"/> is the whole configured sweep, which is what the end feathers are
    /// measured against — one anchors the colours to what is visible, the other keeps a feather a
    /// constant angular size.</summary>
    private sealed record Arc(RectangleF Rect, RoutineProgressStyle Style, float Run, float Full, float Thickness);

    private static void PaintArc(Graphics g, Arc arc, float start, float sweep, Color c1, Color c2, int alpha)
    {
        float thickness = arc.Thickness;
        float sign = Math.Sign(sweep);
        float run = Math.Abs(sweep);

        switch (arc.Style.Style)
        {
            case ProgressStyle.Solid:
                DrawPattern(g, arc, start, sweep, c1, c1, LineCap.Round, 0f, 0f);
                break;

            case ProgressStyle.Glow:
                using (var glow = new Pen(Glow(c1, alpha), thickness * 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(glow, arc.Rect, start, sweep);
                DrawPattern(g, arc, start, sweep, c1, c2, LineCap.Round, 0f, 0f);
                break;

            case ProgressStyle.Dashed:
                DrawPattern(g, arc, start, sweep, c1, c2, LineCap.Flat, 7f, 5f);
                break;

            case ProgressStyle.Dots:
                DrawPattern(g, arc, start, sweep, c1, c2, LineCap.Round, 0.6f, 5f);
                break;

            case ProgressStyle.Segmented:
            {
                const int segments = 12;
                float span = run / segments;
                DrawPattern(g, arc, start, sweep, c1, c2, LineCap.Flat, span * 0.7f, span * 0.3f);
                break;
            }

            case ProgressStyle.Comet:
                DrawCometArc(g, arc, start, sweep, c1, c1, alpha, glow: false);
                break;

            case ProgressStyle.CometGlow:
            case ProgressStyle.CometGlowGradient:
            {
                // Two passes: the trail with its own halo baked into every step, then a hot cap where
                // the head is — which is what makes this read as a burning head rather than as a
                // stroke that happens to be bright.
                //
                // There is deliberately no separate halo band behind the trail. There used to be —
                // one constant-width stroke over the whole sweep — and it was the one part of this
                // style that ignored the trail: full brightness past both end feathers, and its
                // widest at the tail, so a stroke that had faded to nothing still wore a hard ring
                // of light. The per-step halo already has the right shape, because it takes its
                // width from the step and its level from the same feather the trail uses, so letting
                // it be the only glow is what makes the light follow the stroke.
                //
                // The gradient variant differs only in where the second colour goes: its trail runs
                // Color to Color2 like every other style, and its head and bloom take Color2, so it
                // is the same shape wearing the pair instead of a single tone.
                var head = arc.Style.Style == ProgressStyle.CometGlowGradient ? c2 : c1;
                DrawCometArc(g, arc, start, sweep, c1, head, alpha, glow: true);
                PaintCometHead(g, arc, start, sweep, head, alpha);
                break;
            }

            case ProgressStyle.Duo:
            {
                float half = sweep * 0.5f;
                DrawPattern(g, arc, start, half, c1, c1, LineCap.Flat, 0f, 0f);
                DrawPattern(g, arc, start + half, sweep - half, c2, c2, LineCap.Flat, 0f, 0f);
                // Round only the true ends, so the two halves read as one stroke.
                using (var cap = new Pen(c1, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
                    g.DrawArc(cap, arc.Rect, start, 0.6f * sign);
                using (var cap = new Pen(c2, thickness) { StartCap = LineCap.Flat, EndCap = LineCap.Round })
                    g.DrawArc(cap, arc.Rect, start + sweep - 0.6f * sign, 0.6f * sign);
                break;
            }

            case ProgressStyle.Hairline:
            case ProgressStyle.Chunky:
            case ProgressStyle.Gradient:
            default:
                DrawPattern(g, arc, start, sweep, c1, c2, LineCap.Round, 0f, 0f);
                break;
        }
    }

    /// <summary>Draws an arc as a run/gap pattern, tinting every sub-arc by its position along the
    /// stroke so the colours read as c1 at the tail and c2 at the head.</summary>
    private static void DrawPattern(Graphics g, Arc arc, float startAngle, float sweep,
        Color c1, Color c2, LineCap cap, float onDeg, float offDeg)
    {
        float magnitude = Math.Abs(sweep);
        if (magnitude <= 0.02f || arc.Thickness <= 0f) return;
        float sign = Math.Sign(sweep);

        if (offDeg <= 0.01f || onDeg <= 0.01f)
        {
            DrawRun(g, arc, startAngle, sweep, 0f, c1, c2, cap);
            return;
        }

        float period = onDeg + offDeg;
        for (float pos = 0f; pos < magnitude; pos += period)
        {
            float len = Math.Min(onDeg, magnitude - pos);
            if (len <= 0.02f) continue;
            DrawRun(g, arc, startAngle + pos * sign, len * sign, pos, c1, c2, cap);
        }
    }

    /// <summary>One uninterrupted length of stroke, chopped into sub-arcs because GDI+ has no
    /// per-arc alpha and the two ends have to feather.
    ///
    /// The colours are anchored to the arc actually drawn rather than to the whole ring, so the
    /// head carries the second colour from the moment the lap starts instead of only once the
    /// sweep is nearly complete — which is what makes the two read as the ends of one stroke.</summary>
    private static void DrawRun(Graphics g, Arc arc, float startAngle, float sweep,
        float runOffset, Color c1, Color c2, LineCap cap)
    {
        float magnitude = Math.Abs(sweep);
        if (magnitude <= 0.02f) return;
        float sign = Math.Sign(sweep);

        int steps = Math.Max((int)Math.Ceiling(magnitude / 4f), 1);
        float step = magnitude / steps;

        // One pen for the whole run, its colour and cap set per sub-arc. A GDI+ Pen owns a native
        // handle that costs far more to create than to retune, and a closed ring is ninety of these
        // every frame.
        using var pen = new Pen(Color.Black, arc.Thickness);

        for (int i = 0; i < steps; i++)
        {
            float position = runOffset + step * (i + 0.5f);
            float t = arc.Run > 0.01f ? Math.Clamp(position / arc.Run, 0f, 1f) : 0f;
            var colour = Scale(Lerp(c1, c2, t), Feather(arc, position));
            if (colour.A == 0) continue;

            pen.Color = colour;
            pen.StartCap = i == 0 ? cap : LineCap.Flat;
            pen.EndCap = i == steps - 1 ? cap : LineCap.Flat;
            // A hair of overlap so the sub-arcs meet without a seam.
            g.DrawArc(pen, arc.Rect, startAngle + step * i * sign, (step + 0.06f) * sign);
        }
    }

    /// <summary>A trail whose head is "now" and whose tail fades back along the sweep.
    ///
    /// <paramref name="glow"/> gives every step its own halo, which is what separates the flat Comet
    /// from Comet Glow: the light is not one band laid behind the trail, it is a property of it.
    /// <paramref name="c2"/> is the colour at the head, so the gradient variant tints along the
    /// trail while the plain ones pass the same colour twice and come out flat.
    ///
    /// One pen and one halo brush last the whole loop. A GDI+ Pen wraps a native handle, and this
    /// runs thirty times into every frame at sixty frames a second — allocating one per step was
    /// most of what the frame cost, and setting the colour on an existing pen is all that was ever
    /// needed.</summary>
    private static void DrawCometArc(Graphics g, Arc arc, float start, float sweep, Color c1, Color c2, int alpha, bool glow)
    {
        const int steps = 30;
        float magnitude = Math.Abs(sweep);
        float sign = Math.Sign(sweep);
        float span = magnitude / steps;

        using var pen = new Pen(Color.Black, arc.Thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var halo = new Pen(Color.Black, arc.Thickness * 3.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        for (int i = 0; i < steps; i++)
        {
            float reach = (i + 1) / (float)steps;
            float at = span * (i + 0.5f);
            // The floor of 6 the old version applied is gone: it kept a full-opacity tail alive
            // through the lap retract, which left a stub of ring visible after the seam.
            int level = (int)Math.Round(alpha * (0.20f + 3.0f * reach) * Feather(arc, at));
            //                                   ↑ 尾端下限    ↑ 到头部的增长幅度
            if (level <= 0) continue;

            float t = arc.Run > 0.01f ? Math.Clamp(at / arc.Run, 0f, 1f) : 1f;
            var tint = Lerp(c1, c2, t);
            var colour = Color.FromArgb(Math.Clamp(level, 0, 255), tint.R, tint.G, tint.B);

            // The trail thickens into its head, so the light gathers where the time is.
            float width = arc.Thickness * (0.35f + 0.65f * reach);
            if (glow)
            {
                halo.Color = Glow(colour, level);
                halo.Width = width * 3.4f;
                g.DrawArc(halo, arc.Rect, start + span * i * sign, (span + 0.08f) * sign);
            }
            pen.Color = colour;
            pen.Width = width;
            g.DrawArc(pen, arc.Rect, start + span * i * sign, (span + 0.08f) * sign);
        }
    }

    /// <summary>The burning cap on a glowing comet's head.
    ///
    /// Drawn as a very short round-capped arc rather than as an ellipse: a point on an ellipse arc
    /// is a parametric solve, whereas a round cap lands exactly on the tip for free this way and
    /// follows the block's rotation like everything else.</summary>
    private static void PaintCometHead(Graphics g, Arc arc, float start, float sweep, Color c, int alpha)
    {
        float sign = Math.Sign(sweep);
        int level = (int)Math.Round(alpha * Feather(arc, arc.Run));
        if (level <= 0) return;

        float width = arc.Thickness * 1.15f;
        using (var halo = new Pen(Glow(c, level), width * 3.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(halo, arc.Rect, start + sweep - 0.3f * sign, 0.3f * sign);
        using (var core = new Pen(Color.FromArgb((int)c.A, c.R, c.G, c.B), width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        })
            g.DrawArc(core, arc.Rect, start + sweep - 0.3f * sign, 0.3f * sign);
    }

    // ---- day bar ---------------------------------------------------------------------------

    /// <summary>Where "now" sits between StartHour and EndHour, both inclusive: 8:00–23:00 runs
    /// through 23:59:59, so the span is sixteen hours, not fifteen.</summary>
    public static float DayFraction(RoutineConfig config, DateTime now)
    {
        int first = Math.Min(config.StartHour, config.EndHour);
        int last = Math.Max(config.StartHour, config.EndHour);
        float span = Math.Max((last - first + 1) * 3600f, 1f);
        // TotalSeconds carries the milliseconds, so the pointer creeps rather than jumping.
        float elapsed = (float)(now.TimeOfDay.TotalSeconds - first * 3600.0);
        return Math.Clamp(elapsed / span, 0f, 1f);
    }

    /// <summary>The day bar, as a timestamp rail rather than a filled track: hour ticks along the
    /// line and a pointer at the current time. Nothing is "filled in", so the reading survives
    /// being laid over a picture.</summary>
    public static void PaintBar(Graphics g, RoutineConfig config, DateTime now, Size box)
    {
        if (box.Width <= 0 || box.Height <= 0) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        var style = config.BarStyle;
        int alpha = Alpha(style.Opacity);
        var accent = WithAlpha(ClockRenderer.ParseColor(style.Color), alpha);
        var accent2 = WithAlpha(ClockRenderer.ParseColor(style.Color2), alpha);
        var track = WithAlpha(ClockRenderer.ParseColor(style.TrackColor), Alpha(style.TrackOpacity));

        float mid = box.Height / 2f;
        float t = Math.Clamp((float)style.Thickness * box.Height, 1f, box.Height * 0.6f);
        float left = t / 2f + 1f;
        float right = box.Width - left;
        if (right <= left) return;

        int first = Math.Min(config.StartHour, config.EndHour);
        int last = Math.Max(config.StartHour, config.EndHour);
        int slots = last - first + 1;
        float cell = (right - left) / slots;

        PaintRail(g, style.Style, left, right, mid, t, accent, accent2, track, slots, cell);

        // Hour ticks: a short stroke across the rail at every boundary, longer at the ends.
        using (var tick = new Pen(track, Math.Max(t * 0.22f, 1f)))
        {
            for (int i = 0; i <= slots; i++)
            {
                float x = left + cell * i;
                float h = (i == 0 || i == slots) ? box.Height * 0.36f : box.Height * 0.20f;
                g.DrawLine(tick, x, mid - h, x, mid + h);
            }
        }

        float pointer = left + (right - left) * DayFraction(config, now);
        PaintPointer(g, style.Style, pointer, mid, box.Height, t, accent, accent2, alpha);
    }

    private static void PaintRail(Graphics g, ProgressStyle style, float left, float right, float mid, float t,
        Color accent, Color accent2, Color track, int slots, float cell)
    {
        switch (style)
        {
            case ProgressStyle.Dashed:
                using (var p = new Pen(accent, t) { DashStyle = DashStyle.Dash })
                    g.DrawLine(p, left, mid, right, mid);
                break;

            case ProgressStyle.Dots:
                using (var p = new Pen(accent, t) { DashStyle = DashStyle.Dot, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(p, left, mid, right, mid);
                break;

            case ProgressStyle.Segmented:
                float gap = cell * 0.16f;
                using (var p = new Pen(track, t) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    for (int i = 0; i < slots; i++)
                    {
                        float x0 = left + cell * i + gap * 0.5f;
                        float x1 = left + cell * (i + 1) - gap * 0.5f;
                        if (x1 <= x0) continue;
                        g.DrawLine(p, x0, mid, x1, mid);
                    }
                }
                break;

            case ProgressStyle.CometGlow:
            case ProgressStyle.CometGlowGradient:
                // The rail itself carries the glow, so the needles below read as something the
                // light is coming off rather than as strokes floating over a line. The gradient
                // pair goes into the halo, which is the only part of a rail wide enough to show it.
                using (var halo = new Pen(Glow(style == ProgressStyle.CometGlowGradient ? Lerp(accent, accent2, 0.5f) : accent, accent.A), t * 3.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(halo, left, mid, right, mid);
                using (var p = new Pen(track, t) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(p, left, mid, right, mid);
                break;

            case ProgressStyle.Duo:
                using (var a = new Pen(accent, t) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
                    g.DrawLine(a, left, mid, (left + right) / 2f, mid);
                using (var b = new Pen(accent2, t) { StartCap = LineCap.Flat, EndCap = LineCap.Round })
                    g.DrawLine(b, (left + right) / 2f, mid, right, mid);
                break;

            default:
                using (var p = new Pen(track, t) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(p, left, mid, right, mid);
                break;
        }
    }

    private static void PaintPointer(Graphics g, ProgressStyle style, float x, float mid, float h, float t,
        Color accent, Color accent2, int alpha)
    {
        float half = h * 0.42f;
        float width = Math.Max(t * 0.50f, 1.5f);

        switch (style)
        {
            case ProgressStyle.Comet:
                PaintNeedles(g, x, mid, half, width, accent, alpha);
                break;

            case ProgressStyle.CometGlow:
            case ProgressStyle.CometGlowGradient:
                // The same needles with a halo on the head, so the two differ in light rather than
                // in shape — which is the only way the pair stays readable at small sizes. The
                // gradient variant lets the halo fall between the two colours and puts Color2 on
                // the knob, so the pair shows up on the bar as well as on the ring.
                using (var glow = new Pen(Glow(style == ProgressStyle.CometGlowGradient ? Lerp(accent, accent2, 0.5f) : accent, alpha), width * 5.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(glow, x, mid - half, x, mid + half);
                PaintNeedles(g, x, mid, half, width, accent, alpha);
                break;

            case ProgressStyle.Glow:
                using (var glow = new Pen(Glow(accent, alpha), width * 4.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(glow, x, mid - half, x, mid + half);
                using (var core = new Pen(accent, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(core, x, mid - half, x, mid + half);
                break;

            case ProgressStyle.Gradient:
                using (var br = new LinearGradientBrush(new RectangleF(x - 1f, mid - half, 2f, half * 2f), accent, accent2, 90f))
                using (var p = new Pen(br, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(p, x, mid - half, x, mid + half);
                break;

            case ProgressStyle.Duo:
                using (var halo = new Pen(accent, width * 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(halo, x, mid - half, x, mid + half);
                using (var core = new Pen(accent2, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(core, x, mid - half, x, mid + half);
                break;

            default:
                using (var p = new Pen(accent, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(p, x, mid - half, x, mid + half);
                break;
        }

        // A knob on the rail itself, so the pointer reads as a mark on the line.
        if (style is ProgressStyle.CometGlow or ProgressStyle.CometGlowGradient)
        {
            var lit = style == ProgressStyle.CometGlowGradient ? Lerp(accent, accent2, 0.5f) : accent;
            using var bloom = new SolidBrush(Glow(lit, alpha));
            g.FillEllipse(bloom, x - t * 1.1f, mid - t * 1.1f, t * 2.2f, t * 2.2f);
        }

        using var knob = new SolidBrush(style == ProgressStyle.CometGlowGradient ? accent2 : accent);
        g.FillEllipse(knob, x - t * 0.6f, mid - t * 0.6f, t * 1.2f, t * 1.2f);
    }

    /// <summary>Fading needles trailing the head, so the elapsed side reads without a fill.</summary>
    private static void PaintNeedles(Graphics g, float x, float mid, float half, float width, Color accent, int alpha)
    {
        using var ghost = new Pen(accent, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 6; i >= 0; i--)
        {
            float back = x - i * width * 1.6f;
            if (back < 0) continue;
            int a = (int)Math.Round(alpha * (1f - i * 0.14f));
            if (a <= 0) continue;
            ghost.Color = Color.FromArgb(Math.Clamp(a, 1, 255), accent.R, accent.G, accent.B);
            float reach = half * (1f - i * 0.09f);
            g.DrawLine(ghost, back, mid - reach, back, mid + reach);
        }
    }

    // ---- shared helpers --------------------------------------------------------------------

    private static float ThicknessFor(ProgressStyle style, float baseThickness) => style switch
    {
        ProgressStyle.Hairline => Math.Max(baseThickness * 0.40f, 1f),
        ProgressStyle.Chunky => baseThickness * 1.80f,
        _ => baseThickness,
    };

    /// <summary>How far outside the stroke its light reaches, in multiples of the thickness. The
    /// ellipse is inset by this rather than by half the stroke, because a glow is drawn outside the
    /// stroke and the block's bitmap ends at the box — a halo laid down at half-thickness inset is
    /// sliced off square at the outer edge, which is a hard line exactly where the effect is meant
    /// to be soft.</summary>
    private static float GlowReach(ProgressStyle style) => style switch
    {
        ProgressStyle.Glow => 1.30f,
        ProgressStyle.CometGlow or ProgressStyle.CometGlowGradient => 1.75f,
        _ => 0.50f,
    };

    private static Color Glow(Color c, int alpha) =>
        Color.FromArgb(Math.Clamp(25, 12, 255), c.R, c.G, c.B);
//                            ↑ 光晕只有本体亮度的 30%   ↑ 下限 12
    /// <summary>How much of the stroke survives at <paramref name="position"/> degrees along the
    /// drawn arc — the two end feathers multiplied together.</summary>
    private static float Feather(Arc arc, float position)
    {
        var style = arc.Style;
        return Ramp(style.TailFade, style.TailOpacity, position, arc.Full)
             * Ramp(style.HeadFade, style.HeadOpacity, arc.Run - position, arc.Full);
    }

    /// <summary>One feather: 1 everywhere outside it, easing to <paramref name="tipOpacity"/> at the
    /// tip <paramref name="distance"/> degrees away. Its length is a percentage of the whole sweep,
    /// so it keeps a fixed angular size as the head advances instead of stretching with the arc.</summary>
    private static float Ramp(int lengthPercent, int tipOpacity, float distance, float fullSweep)
    {
        float length = Math.Clamp(lengthPercent, 0, 100) / 100f * Math.Abs(fullSweep);
        if (length <= 0.01f) return 1f;
        float tip = Math.Clamp(tipOpacity, 0, 100) / 100f;
        return tip + (1f - tip) * Smooth(distance / length);
    }

    private static float Smooth(float t) => (t = Math.Clamp(t, 0f, 1f)) * t * (3f - 2f * t);

    private static Color Scale(Color c, float factor) =>
        factor >= 0.999f ? c : Color.FromArgb((int)Math.Round(c.A * Math.Clamp(factor, 0f, 1f)), c.R, c.G, c.B);

    private static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(a.A + (b.A - a.A) * t),
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static int Alpha(int opacity) => Math.Clamp(opacity, 0, 100) * 255 / 100;

    private static Color WithAlpha(Color c, int alpha) =>
        Color.FromArgb((int)Math.Round(c.A * (alpha / 255.0)), c.R, c.G, c.B);

    private static Font CreateFont(string family, float size)
    {
        string name = string.IsNullOrWhiteSpace(family) ? "Segoe UI Light" : family;
        try { return new Font(name, size, FontStyle.Regular, GraphicsUnit.Pixel); }
        catch { return new Font(FontFamily.GenericSansSerif, size, FontStyle.Regular, GraphicsUnit.Pixel); }
    }
}
