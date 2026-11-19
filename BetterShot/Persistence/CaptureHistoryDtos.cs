using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterShot.Persistence;

internal sealed class CapturePackageDto
{
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public Guid Id { get; set; }
    [JsonRequired] public DateTime CreatedUtc { get; set; }
    [JsonRequired] public List<AnnotationDto> Annotations { get; set; } = [];
    // Optional since phase 1; packages written earlier omit them.
    public CropDto? Crop { get; set; }
    public BackgroundDto? Background { get; set; }
}

internal sealed class CropDto
{
    [JsonRequired] public double X { get; set; }
    [JsonRequired] public double Y { get; set; }
    [JsonRequired] public double Width { get; set; }
    [JsonRequired] public double Height { get; set; }
}

internal sealed class BackgroundDto
{
    [JsonRequired] public string Preset { get; set; } = "none";
    public double Padding { get; set; } = 48;
    public double CornerRadius { get; set; } = 18;
    public bool Shadow { get; set; } = true;
}

internal sealed class ColorDto
{
    [JsonRequired] public byte A { get; set; }
    [JsonRequired] public byte R { get; set; }
    [JsonRequired] public byte G { get; set; }
    [JsonRequired] public byte B { get; set; }
}

internal sealed class AnnotationDto
{
    [JsonRequired] public string Kind { get; set; } = string.Empty;
    [JsonRequired] public byte A { get; set; }
    [JsonRequired] public byte R { get; set; }
    [JsonRequired] public byte G { get; set; }
    [JsonRequired] public byte B { get; set; }
    [JsonRequired] public double Width { get; set; }
    [JsonRequired] public double X1 { get; set; }
    [JsonRequired] public double Y1 { get; set; }
    [JsonRequired] public double X2 { get; set; }
    [JsonRequired] public double Y2 { get; set; }
    [JsonRequired] public double Radius { get; set; }
    [JsonRequired] public int Number { get; set; }
    [JsonRequired] public bool Redaction { get; set; }
    [JsonRequired] public string? Text { get; set; }
    [JsonRequired] public List<PointDto>? Points { get; set; }
    // Optional style fields added in phase 1. Absent values keep the original appearance.
    public double? Opacity { get; set; }
    public bool? Dashed { get; set; }
    public bool? Shadow { get; set; }
    public ColorDto? Fill { get; set; }
    public double? CornerRadius { get; set; }
    public string? RedactionStyle { get; set; }
    public double? Zoom { get; set; }
    public PointDto? Tail { get; set; }
    public bool? TextBackground { get; set; }
}

internal sealed class PointDto
{
    [JsonRequired] public double X { get; set; }
    [JsonRequired] public double Y { get; set; }
}

internal sealed partial class HistorySettingsDto
{
    [JsonRequired] public int RetentionDays { get; set; } = 7;
    public bool ReplaceScreenshotShortcut { get; set; }
    public bool StartWithWindows { get; set; }
    public bool DragAsFile { get; set; }

    // Phase 2 capture options. All optional: a missing value means "use the default", so
    // older settings.json files load unchanged and older builds ignore these fields.
    public bool? KeepCapturesForever { get; set; }
    public int? CaptureDelaySeconds { get; set; }
    public bool? CopyToClipboard { get; set; }
    public bool? ShowThumbnail { get; set; }
    public bool? OpenEditorAfterCapture { get; set; }
    public bool? PlayCaptureSound { get; set; }
    public int? ThumbnailAutoDismissSeconds { get; set; }
    public string? SaveFolder { get; set; }
    public bool? AutoSave { get; set; }
    public string? FileNamePattern { get; set; }
    public int? FileNameCounter { get; set; }
    public string? ImageFormat { get; set; }
    public int? JpegQuality { get; set; }
    public int? ExportMaxDimension { get; set; }
    public bool? ResizeClipboardCopies { get; set; }
    public HotkeyDto? AreaHotkey { get; set; }
    public HotkeyDto? WindowHotkey { get; set; }
    public HotkeyDto? FullscreenHotkey { get; set; }
    public HotkeyDto? LibraryHotkey { get; set; }

    /// <summary>Fields written by newer builds or other features are carried through every rewrite.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalFields { get; set; }
}

internal sealed class HotkeyDto
{
    public uint Modifiers { get; set; }
    public int Key { get; set; }
}