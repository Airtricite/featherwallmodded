using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeatherWall.Config;

public enum FitMode { Fill, Fit, Stretch }

public enum ClockAnchor
{
    TopLeft, TopCenter, TopRight,
    CenterLeft, Center, CenterRight,
    BottomLeft, BottomCenter, BottomRight,
}

public sealed class WallpaperAssignment
{
    /// <summary>Monitor device name (e.g. \\.\DISPLAY1); "*" applies to all monitors.</summary>
    public string Monitor { get; set; } = "*";
    public string Path { get; set; } = "";
}

/// <summary>Free placement written by edit mode: the widget's top-left corner as a fraction of
/// the monitor's work area. Null (the default) means the widget is still positioned by its
/// anchor and margins, so an untouched config renders exactly as it did before edit mode
/// existed. Fractions rather than pixels so the placement survives a resolution change.</summary>
public sealed class WidgetPlacement
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class ClockConfig
{
    public bool Enabled { get; set; } = true;
    public string Monitor { get; set; } = "*";
    public ClockAnchor Anchor { get; set; } = ClockAnchor.TopCenter;

    /// <summary>Set by edit mode once the clock has been dragged. Overrides Anchor + margins.</summary>
    public WidgetPlacement? Placement { get; set; }
    public int MarginX { get; set; } = 48;
    public int MarginY { get; set; } = 96;

    /// <summary>Per-edge overrides. Null inherits MarginX (left/right) or MarginY (top/bottom),
    /// so an untouched config behaves exactly as it did before these existed. Only the edges the
    /// anchor actually touches are consulted.</summary>
    public int? MarginLeft { get; set; }
    public int? MarginRight { get; set; }
    public int? MarginTop { get; set; }
    public int? MarginBottom { get; set; }
    public bool TwentyFourHour { get; set; } = false;
    public bool ShowSeconds { get; set; } = false;
    public bool ShowDate { get; set; } = true;
    /// <summary>Thin horizontal rule between time and date (Mond style).</summary>
    public bool Separator { get; set; } = true;
    public float FontSize { get; set; } = 110f;
    /// <summary>Use a light-weight face for the big time ("Segoe UI Light").</summary>
    public string FontFamily { get; set; } = "Segoe UI Light";
    /// <summary>#RRGGBB or #AARRGGBB.</summary>
    public string Color { get; set; } = "#F0FFFFFF";
    public bool Shadow { get; set; } = true;

    // ---- date styling ---------------------------------------------------------------------
    // Requested on r/coolgithubprojects by a user two days into daily use: the date could not be
    // styled at all, because CreateDateFont hardcoded "Segoe UI" at 16% of the time's size.
    // Every default below reproduces that exact rendering, so upgrading changes nothing until
    // someone actually sets one.

    /// <summary>Face for the date line. Null or empty inherits the time's <see cref="FontFamily"/>.</summary>
    public string? DateFontFamily { get; set; } = "Segoe UI";

    /// <summary>Date size as a fraction of the time's font size.</summary>
    public float DateFontScale { get; set; } = 0.16f;

    /// <summary>Floor in pixels, so the date stays legible under a small clock.</summary>
    public float DateMinFontSize { get; set; } = 11f;

    /// <summary>#RRGGBB or #AARRGGBB for the date. Null inherits the time's colour, dimmed by
    /// <see cref="DateOpacity"/>.</summary>
    public string? DateColor { get; set; }

    /// <summary>Applied to the inherited colour's alpha. Ignored when DateColor is set.</summary>
    public float DateOpacity { get; set; } = 0.80f;
}

public sealed class PauseConfig
{
    public bool OnFullscreen { get; set; } = true;
    public bool OnBatterySaver { get; set; } = true;
    public bool OnRemoteSession { get; set; } = true;
}

/// <summary>The info widget: a stack of lines fed by system sources, anchored independently of
/// the clock. Disabled by default — an upgrade must render exactly as it did before, the same
/// rule the date-styling defaults follow.</summary>
public sealed class InfoConfig
{
    public bool Enabled { get; set; } = false;
    public string Monitor { get; set; } = "*";
    public ClockAnchor Anchor { get; set; } = ClockAnchor.BottomLeft;

    /// <summary>Set by edit mode once the widget has been dragged.</summary>
    public WidgetPlacement? Placement { get; set; }
    public int MarginX { get; set; } = 48;
    public int MarginY { get; set; } = 48;

    /// <summary>Per-edge overrides, null inheriting MarginX/MarginY, exactly as ClockConfig does.</summary>
    public int? MarginLeft { get; set; }
    public int? MarginRight { get; set; }
    public int? MarginTop { get; set; }
    public int? MarginBottom { get; set; }

    public float FontSize { get; set; } = 22f;
    public string FontFamily { get; set; } = "Segoe UI";
    public string Color { get; set; } = "#C0FFFFFF";
    public bool Shadow { get; set; } = true;

    /// <summary>Truncation budget. Scrolling a long title would mean animating, and animating
    /// means waking up.</summary>
    public int MaxCharacters { get; set; } = 48;

    /// <summary>Ordered; the order is the display order. An unknown name is logged and skipped
    /// rather than throwing, so a config written by a later version still starts this one.</summary>
    public List<string> Sources { get; set; } = ["nowPlaying", "battery"];

    public HaloConfig Halo { get; set; } = new();
    public DiscConfig Disc { get; set; } = new();
}

/// <summary>The now-playing record: album art on the label, turning while the music plays.
///
/// Rotate is the only thing in FeatherWall that starts a timer. Setting it false gives the whole
/// design — art, progress, the lot — as a still image, and creates no timer at all.</summary>
public sealed class DiscConfig
{
    public bool Enabled { get; set; } = true;
    public int Size { get; set; } = 112;
    public bool Rotate { get; set; } = true;
    public bool ShowProgress { get; set; } = true;

    /// <summary>Its own anchor, independent of the info text and the halo — the record is its own
    /// block, not a line in a list.</summary>
    public ClockAnchor Anchor { get; set; } = ClockAnchor.BottomLeft;

    /// <summary>Set by edit mode once the record has been dragged.</summary>
    public WidgetPlacement? Placement { get; set; }
    public int MarginX { get; set; } = 48;
    public int MarginY { get; set; } = 48;
    public int? MarginLeft { get; set; }
    public int? MarginRight { get; set; }
    public int? MarginTop { get; set; }
    public int? MarginBottom { get; set; }

    /// <summary>Used for the progress ring, and for the label when a track has no artwork.</summary>
    public string AccentColor { get; set; } = "#8FB4FF";

    // Typography follows the clock's hierarchy rather than the info widget's uniform text: a
    // near-white title, and a smaller dimmed artist set in spaced capitals underneath. That is
    // the same relationship the clock has between the time and the date.
    public float TitleFontSize { get; set; } = 19f;
    public float ArtistFontSize { get; set; } = 14f;
    public bool ArtistUppercase { get; set; } = true;
    public float ArtistLetterSpacing { get; set; } = 1.4f;
    public float ArtistOpacity { get; set; } = 0.62f;
}

public enum HaloPlacement { Left, Right, Above, Below }

/// <summary>The battery halo: a ring whose arc is the charge level and whose colour steps with it.
///
/// On by default, which bends the rule that an upgrade must never silently add something to
/// someone's wallpaper. Deliberate and narrow: the info widget this attaches to shipped in the
/// same release and has no installed base to surprise, so the population that rule protects is
/// empty. Had the widget shipped a release earlier this would default to false.</summary>
public sealed class HaloConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Bigger than it was: the percentage is drawn inside the ring now, and a number in
    /// a 34 px circle is not legible.</summary>
    public int Size { get; set; } = 44;

    /// <summary>False: drawn beside the info lines and moving with them. True: its own overlay,
    /// its own anchor and its own margins, and Placement is ignored.</summary>
    public bool Detached { get; set; } = false;
    public HaloPlacement Placement { get; set; } = HaloPlacement.Left;

    public ClockAnchor Anchor { get; set; } = ClockAnchor.TopRight;

    /// <summary>Set by edit mode once the halo has been dragged. Named apart from
    /// <see cref="Placement"/>, which is the attached-mode side the halo sits on.</summary>
    public WidgetPlacement? FreePlacement { get; set; }
    public int MarginX { get; set; } = 48;
    public int MarginY { get; set; } = 48;
    public int? MarginLeft { get; set; }
    public int? MarginRight { get; set; }
    public int? MarginTop { get; set; }
    public int? MarginBottom { get; set; }

    /// <summary>False uses HighColor at every level, so one fixed colour is a supported choice
    /// rather than something you have to fake by setting all four the same.</summary>
    public bool ColorByLevel { get; set; } = true;

    // Red at the bottom, orange in the middle, green when healthy — the conventional battery
    // reading. Thresholds are inclusive upper bounds. The warm Ember ramp is still available as a
    // preset; it is not the default, because a gold "high" says less than a green one.
    public string LowColor { get; set; } = "#FF4D4D";
    public int LowThreshold { get; set; } = 20;
    public string MidColor { get; set; } = "#FF9A3C";
    public int MidThreshold { get; set; } = 50;
    public string HighColor { get; set; } = "#5FD98A";
    public string ChargedColor { get; set; } = "#7CE8A4";
    public string TrackColor { get; set; } = "#24FFFFFF";
}

/// <summary>One block of the routine preset. Position and size are fractions of the monitor's
/// work area, so the whole layout survives a resolution change and edit mode can write a dragged
/// rectangle straight back without knowing anything about DPI.</summary>
public sealed class RoutinePart
{
    public bool Enabled { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 0.1;
    public double H { get; set; } = 0.1;

    /// <summary>Clockwise rotation in degrees about the block's own centre. Applied inside the
    /// block's bitmap, so the composition surface stays axis-aligned and moving a rotated block
    /// is still a plain offset.</summary>
    public double Rotation { get; set; }

    /// <summary>Caption for the block when it carries text of its own (the Hour/Min labels).
    /// Ignored by the digit and progress blocks.</summary>
    public string Text { get; set; } = "";
}

/// <summary>The accent treatments shared by the ring and the day bar. Every one of them answers the
/// same question — "how much of the cycle is behind us" — with a different mark, so they are a
/// single enum rather than two unrelated sets. The order is load-bearing: the settings page maps a
/// dropdown index straight onto the value, so a new member goes on the end.</summary>
public enum ProgressStyle
{
    Solid,
    Gradient,
    Glow,
    Dashed,
    Segmented,
    Hairline,
    Chunky,
    Comet,
    Dots,
    Duo,
    CometGlow,
    CometGlowGradient,
}

/// <summary>Fill for the big hour/minute digits: a flat colour, or a two-stop gradient with a
/// direction and a single opacity that scales both stops.</summary>
public sealed class RoutineTextStyle
{
    public bool Gradient { get; set; } = false;
    /// <summary>Flat colour, or the first gradient stop.</summary>
    public string Color { get; set; } = "#F0FFFFFF";
    /// <summary>Second gradient stop; unused when <see cref="Gradient"/> is false.</summary>
    public string Color2 { get; set; } = "#8FB4FF";
    /// <summary>Gradient direction in degrees: 0 points right, 90 points down.</summary>
    public double Angle { get; set; } = 90;
    /// <summary>0–100, applied on top of the colours' own alpha.</summary>
    public int Opacity { get; set; } = 100;
    public string FontFamily { get; set; } = "Segoe UI Light";
}

/// <summary>Colour, opacity, thickness and treatment for one progress element — the ring or the
/// bar. Two instances rather than one so the ring and the bar can be styled apart.</summary>
public sealed class RoutineProgressStyle
{
    public ProgressStyle Style { get; set; } = ProgressStyle.Solid;
    public string Color { get; set; } = "#8FB4FF";
    public string Color2 { get; set; } = "#F0FFFFFF";
    /// <summary>0–100, applied on top of the colours' own alpha.</summary>
    public int Opacity { get; set; } = 100;
    public string TrackColor { get; set; } = "#33FFFFFF";
    /// <summary>0–100, applied on top of the track colour's own alpha.</summary>
    public int TrackOpacity { get; set; } = 100;
    /// <summary>Bar/ring width as a fraction of the box's short side.</summary>
    public double Thickness { get; set; } = 0.14;

    // ---- arc geometry (the ring; ignored by the bar) ---------------------------------------
    // The ring is an arc segment, not a full circle: an ellipse inscribed in the block, drawn
    // from StartAngle through SweepAngle, with the block's own Rotation tilting it. A 180° sweep
    // in a wide box is the "slanted half ellipse" case.

    /// <summary>Degrees clockwise from 3 o'clock. -90 is the top.</summary>
    public double StartAngle { get; set; } = -90;
    /// <summary>Degrees of arc to draw. 360 closes the ring.</summary>
    public double SweepAngle { get; set; } = 360;

    // ---- the two ends of the stroke ---------------------------------------------------------
    // Both feathers are measured as a percentage of the WHOLE sweep rather than of what happens to
    // be drawn, so a feather keeps its angular size as the head advances along the arc instead of
    // stretching with it. The two multiply where they overlap, which is what makes a short arc on
    // a long feather meet in the middle rather than double up.
    //
    // The track carries them too, so a full lap lands on exactly the shape the empty ring already
    // had: the stroke grows out of the track's soft tail and arrives at its soft head.

    /// <summary>How far the leading end fades in, in percent of the sweep. 0 draws a hard tip.</summary>
    public int HeadFade { get; set; } = 0;
    /// <summary>Opacity at the very tip of the leading end, 0–100. Only reached when
    /// <see cref="HeadFade"/> is non-zero.</summary>
    public int HeadOpacity { get; set; } = 100;

    /// <summary>How far the trailing end fades up from, in percent of the sweep.</summary>
    public int TailFade { get; set; } = 0;
    /// <summary>Opacity at the very tip of the trailing end, 0–100.</summary>
    public int TailOpacity { get; set; } = 100;

    /// <summary>Percent of the cycle the finished lap spends retracting.
    ///
    /// Without it the arc is deleted and redrawn the instant the minute turns — a full stroke one
    /// frame, a bare track the next — which reads as a dropped frame rather than as a loop. Instead
    /// the finished lap's head stays where it landed and its tail runs forward after it, so the
    /// stroke unwinds into the endpoint it just reached while the new one grows out of the start
    /// behind it; 0 is the old hard cut.
    ///
    /// Crossfading it out would have been the cheaper trick, but a lap that dims in place is still
    /// a lap that vanishes. Retracting it is the same picture carried to its conclusion: the seam
    /// becomes the motion the ring was already making.
    ///
    /// The ring only: the day bar is a rail with a pointer on it, and there is no stroke of its own
    /// to unwind.</summary>
    public int LoopRetract { get; set; } = 8;
}

/// <summary>The routine preset: a user-supplied background image with a schedule built on top of
/// it — the hour and minute beside a character with a label under each, a ring that fills with the
/// current second, and a timestamp bar marking where "now" sits between StartHour and EndHour.
///
/// Disabled by default, like the info widget, so an upgrade changes nothing until it is switched
/// on. Every block is placed by edit mode.</summary>
public sealed class RoutineConfig
{
    public bool Enabled { get; set; } = false;
    public string Monitor { get; set; } = "*";

    /// <summary>The picture the preset is drawn over. Applied as this monitor's wallpaper while
    /// the preset is on; empty means whatever wallpaper is already assigned.</summary>
    public string BackgroundImage { get; set; } = "";

    /// <summary>Inclusive bounds: StartHour 8 means the day starts at 8:00:00, EndHour 23 means
    /// it runs through 23:59:59, so the span is (EndHour - StartHour + 1) hours.</summary>
    public int StartHour { get; set; } = 8;
    public int EndHour { get; set; } = 23;

    public bool TwentyFourHour { get; set; } = true;

    /// <summary>The two digit blocks are styled apart on purpose: the reference design reads the
    /// hour and the minute as different elements, and a single style could not say that.</summary>
    public RoutineTextStyle HourDigits { get; set; } = new();
    public RoutineTextStyle MinuteDigits { get; set; } = new()
    {
        Color = "#F0FFFFFF", Color2 = "#8FB4FF",
    };

    public RoutinePart Hour { get; set; } = new() { X = 0.255, Y = 0.280, W = 0.090, H = 0.110 };
    public RoutinePart Minute { get; set; } = new() { X = 0.655, Y = 0.280, W = 0.090, H = 0.110 };
    public RoutinePart HourLabel { get; set; } = new() { X = 0.245, Y = 0.398, W = 0.110, H = 0.026, Text = "HOUR" };
    public RoutinePart MinuteLabel { get; set; } = new() { X = 0.645, Y = 0.398, W = 0.110, H = 0.026, Text = "MIN" };
    public RoutinePart Ring { get; set; } = new() { X = 0.435, Y = 0.265, W = 0.130, H = 0.190 };
    public RoutinePart Bar { get; set; } = new() { X = 0.200, Y = 0.790, W = 0.600, H = 0.070 };

    public RoutineProgressStyle RingStyle { get; set; } = new()
    {
        Style = ProgressStyle.Comet, Color = "#8FB4FF", Color2 = "#F0FFFFFF", Thickness = 0.14,
    };
    public RoutineProgressStyle BarStyle { get; set; } = new()
    {
        Style = ProgressStyle.Solid, Color = "#8FB4FF", Color2 = "#F0FFFFFF", Thickness = 0.20,
    };

    /// <summary>Face, colour and opacity of the small Hour/Min captions.</summary>
    public RoutineTextStyle LabelStyle { get; set; } = new()
    {
        Color = "#E8E8E8", Color2 = "#8FB4FF", Opacity = 100,
    };
}

public sealed class AppConfig
{
    public List<WallpaperAssignment> Wallpapers { get; set; } = [];
    public FitMode Fit { get; set; } = FitMode.Fill;
    public bool MuteVideo { get; set; } = true;
    public double Volume { get; set; } = 0.3;
    public ClockConfig Clock { get; set; } = new();
    public InfoConfig Info { get; set; } = new();
    public RoutineConfig Routine { get; set; } = new();
    public PauseConfig Pause { get; set; } = new();

    public string? WallpaperFor(string monitorDevice)
    {
        var exact = Wallpapers.FirstOrDefault(w => string.Equals(w.Monitor, monitorDevice, StringComparison.OrdinalIgnoreCase));
        var star = Wallpapers.FirstOrDefault(w => w.Monitor == "*");
        var path = (exact ?? star)?.Path;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public void Assign(string monitorDevice, string path)
    {
        var existing = Wallpapers.FirstOrDefault(w => string.Equals(w.Monitor, monitorDevice, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) existing.Path = path;
        else Wallpapers.Add(new WallpaperAssignment { Monitor = monitorDevice, Path = path });
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(RoutineConfig))]
public sealed partial class ConfigJsonContext : JsonSerializerContext;

public static class ConfigStore
{
    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FeatherWall");

    public static string ConfigPath { get; } = Path.Combine(ConfigDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), ConfigJsonContext.Default.AppConfig) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Common.Log.Warn($"Config unreadable, using defaults ({ex.Message})");
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig));
        File.Move(tmp, ConfigPath, overwrite: true);
    }
}

/// <summary>Named routine layouts on disk, one JSON file each under
/// %AppData%\FeatherWall\routine-presets. The whole <see cref="RoutineConfig"/> is stored, blocks
/// and all, because a preset that only carried colours would not be a layout.</summary>
public static class RoutinePresetStore
{
    public static string Directory { get; } =
        Path.Combine(ConfigStore.ConfigDirectory, "routine-presets");

    public static IReadOnlyList<string> List()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return [];
            return [.. System.IO.Directory.EnumerateFiles(Directory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex)
        {
            Common.Log.Warn($"Routine presets unreadable ({ex.Message})");
            return [];
        }
    }

    public static bool Save(string name, RoutineConfig config)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = PathFor(name) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, ConfigJsonContext.Default.RoutineConfig));
            File.Move(tmp, PathFor(name), overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Common.Log.Warn($"Saving routine preset '{name}' failed ({ex.Message})");
            return false;
        }
    }

    public static RoutineConfig? Load(string name)
    {
        try
        {
            var path = PathFor(name);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJsonContext.Default.RoutineConfig);
        }
        catch (Exception ex)
        {
            Common.Log.Warn($"Loading routine preset '{name}' failed ({ex.Message})");
            return null;
        }
    }

    /// <summary>Template is replaced before this is called, so the name is already known to be a
    /// bare file name with no separators.</summary>
    public static bool Delete(string name)
    {
        try
        {
            var path = PathFor(name);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Common.Log.Warn($"Deleting routine preset '{name}' failed ({ex.Message})");
            return false;
        }
    }

    private static string PathFor(string name) => Path.Combine(Directory, name + ".json");
}
