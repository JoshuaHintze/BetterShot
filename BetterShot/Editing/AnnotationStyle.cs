using System.Windows.Media;

namespace BetterShot.Editing;

internal enum EditorTool { Select, Rectangle, Ellipse, Arrow, Line, Pen, Highlighter, Text, Callout, Step, Redact, Spotlight, Magnify, Crop }

internal enum RedactionStyle { Solid, Pixelate, Blur }

/// <summary>Per-tool style defaults. Annotations copy the values they support when created or restyled.</summary>
internal sealed record ToolStyle
{
    internal static readonly Color DefaultStroke = Color.FromRgb(0xFF, 0x42, 0x45);
    internal static readonly double[] WidthPresets = [2, 4, 8, 14];

    public Color Stroke { get; init; } = DefaultStroke;
    public double Width { get; init; } = 4;
    public Color? Fill { get; init; }
    public double CornerRadius { get; init; } = 3;
    public double Opacity { get; init; } = 1;
    public bool Dashed { get; init; }
    public bool Shadow { get; init; }
    public double FontSize { get; init; } = 24;
    public bool TextBackground { get; init; }
    public RedactionStyle Redaction { get; init; } = RedactionStyle.Solid;
    public double MagnifyZoom { get; init; } = 2;
    public double MagnifyDiameter { get; init; } = 160;

    internal static ToolStyle DefaultFor(EditorTool tool) => tool switch
    {
        EditorTool.Highlighter => new ToolStyle { Stroke = Color.FromRgb(255, 212, 0), Width = 8 },
        EditorTool.Callout => new ToolStyle { Stroke = DefaultStroke, FontSize = 22 },
        EditorTool.Redact => new ToolStyle { Stroke = Colors.Black },
        EditorTool.Spotlight => new ToolStyle { CornerRadius = 8 },
        EditorTool.Magnify => new ToolStyle { Stroke = Colors.White, Width = 4, Shadow = true },
        _ => new ToolStyle()
    };

    internal ToolStyle Normalized() => this with
    {
        Stroke = Color.FromArgb(255, Stroke.R, Stroke.G, Stroke.B),
        Width = Clamp(Width, 1, 64, 4),
        CornerRadius = Clamp(CornerRadius, 0, 200, 3),
        Opacity = Clamp(Opacity, .1, 1, 1),
        FontSize = Clamp(FontSize, 6, 512, 24),
        Redaction = Enum.IsDefined(Redaction) ? Redaction : RedactionStyle.Solid,
        MagnifyZoom = Clamp(MagnifyZoom, 1.25, 8, 2),
        MagnifyDiameter = Clamp(MagnifyDiameter, 24, 2000, 160)
    };

    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

/// <summary>Background composition applied around the (optionally cropped) screenshot on export.</summary>
internal sealed record BackgroundStyle(string Preset, double Padding = 48, double CornerRadius = 18, bool Shadow = true)
{
    internal const string NoneId = "none";
    internal static readonly BackgroundStyle None = new(NoneId);
    internal bool IsEnabled => BackgroundPresets.Find(Preset) is not null;
    internal int PaddingPixels => IsEnabled ? (int)Math.Round(Math.Clamp(double.IsFinite(Padding) ? Padding : 48, 0, 400)) : 0;
    internal double EffectiveCornerRadius => Math.Clamp(double.IsFinite(CornerRadius) ? CornerRadius : 18, 0, 200);

    internal BackgroundStyle Normalized() => this with
    {
        Preset = BackgroundPresets.Find(Preset)?.Id ?? NoneId,
        Padding = Math.Clamp(double.IsFinite(Padding) ? Math.Round(Padding) : 48, 0, 400),
        CornerRadius = EffectiveCornerRadius
    };
}

internal sealed record BackgroundPreset(string Id, string Name, Color Start, Color End)
{
    internal Brush CreateBrush()
    {
        Brush brush = Start == End
            ? new SolidColorBrush(Start)
            : new LinearGradientBrush(Start, End, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }
}

internal static class BackgroundPresets
{
    internal static readonly BackgroundPreset[] All =
    [
        new("purple", "Purple", Color.FromRgb(0x7F, 0x5A, 0xF0), Color.FromRgb(0xD9, 0x6C, 0xD6)),
        new("ocean", "Cyan and blue", Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x3B, 0x6E, 0xF6)),
        new("peach", "Peach and pink", Color.FromRgb(0xFD, 0xC0, 0x8E), Color.FromRgb(0xF4, 0x72, 0xB6)),
        new("mint", "Mint", Color.FromRgb(0xB4, 0xF5, 0xD6), Color.FromRgb(0x34, 0xD3, 0x99)),
        new("orange", "Orange", Color.FromRgb(0xFD, 0xBA, 0x74), Color.FromRgb(0xF9, 0x5C, 0x16)),
        new("midnight", "Dark blue", Color.FromRgb(0x1E, 0x3A, 0x8A), Color.FromRgb(0x0B, 0x12, 0x2B)),
        new("white", "White", Colors.White, Colors.White),
        new("lavender", "Light lavender", Color.FromRgb(0xF1, 0xEE, 0xFB), Color.FromRgb(0xDD, 0xDB, 0xF2))
    ];

    internal static BackgroundPreset? Find(string? id) =>
        All.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase));
}
