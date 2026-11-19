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

internal sealed class HistorySettingsDto
{
    [JsonRequired] public int RetentionDays { get; set; } = 7;
    public bool ReplaceScreenshotShortcut { get; set; }
    public bool StartWithWindows { get; set; }
    public bool DragAsFile { get; set; }
}