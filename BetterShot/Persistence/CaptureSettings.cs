using BetterShot.Export;
using BetterShot.Platform;
using System.Text.Json;

namespace BetterShot.Persistence;

/// <summary>Immutable snapshot of every persisted preference. Setters build a modified copy, write it, then swap it in.</summary>
internal sealed record CaptureSettings
{
    internal const int NeverExpire = 0;
    /// <summary>Written to RetentionDays when captures are kept forever, so older builds keep them for a year instead of resetting.</summary>
    internal const int ForeverCompatibilityDays = 365;
    internal static readonly int[] RetentionChoices = [1, 7, 14, 30, 90, 180, NeverExpire];
    internal static readonly int[] DelayChoices = [0, 3, 5, 10];
    internal static readonly int[] AutoDismissChoices = [0, 10, 30, 60];

    internal static CaptureSettings Default { get; } = new();

    /// <summary>Days to keep library captures; <see cref="NeverExpire"/> keeps them until deleted.</summary>
    public int RetentionDays { get; init; } = 7;
    public bool ReplaceScreenshotShortcut { get; init; }
    public bool StartWithWindows { get; init; }
    public bool DragAsFile { get; init; }
    public int CaptureDelaySeconds { get; init; }
    public bool CopyToClipboard { get; init; } = true;
    public bool ShowThumbnail { get; init; } = true;
    public bool OpenEditorAfterCapture { get; init; }
    public bool PlayCaptureSound { get; init; } = true;
    public int ThumbnailAutoDismissSeconds { get; init; }
    /// <summary>Empty means the default Pictures\BetterShot folder.</summary>
    public string SaveFolder { get; init; } = string.Empty;
    public bool AutoSave { get; init; }
    public string FileNamePattern { get; init; } = FileNameTemplate.DefaultPattern;
    public int FileNameCounter { get; init; } = 1;
    public CaptureImageFormat ImageFormat { get; init; } = CaptureImageFormat.Png;
    public int JpegQuality { get; init; } = ExportOptions.DefaultJpegQuality;
    public int ExportMaxDimension { get; init; }
    public bool ResizeClipboardCopies { get; init; }
    public HotkeyBinding AreaHotkey { get; init; }
    public HotkeyBinding WindowHotkey { get; init; }
    public HotkeyBinding FullscreenHotkey { get; init; }
    public HotkeyBinding LibraryHotkey { get; init; }
    /// <summary>Reads text in library captures on this PC so the library search can find it.</summary>
    public bool SearchTextInImages { get; init; } = true;

    internal bool KeepForever => RetentionDays == NeverExpire;
    internal bool SavesEveryCapture => AutoSave || !ShowThumbnail;
    internal string EffectiveSaveFolder => string.IsNullOrWhiteSpace(SaveFolder) ? ExportOptions.DefaultSaveFolder : SaveFolder;

    internal ExportOptions ToExportOptions() =>
        new(ImageFormat, JpegQuality, ExportMaxDimension, ResizeClipboardCopies, EffectiveSaveFolder, FileNamePattern);

    internal HotkeyBinding HotkeyFor(HotkeyAction action) => action switch
    {
        HotkeyAction.Area => AreaHotkey,
        HotkeyAction.Window => WindowHotkey,
        HotkeyAction.Fullscreen => FullscreenHotkey,
        HotkeyAction.Library => LibraryHotkey,
        _ => HotkeyBinding.None
    };

    internal CaptureSettings WithHotkey(HotkeyAction action, HotkeyBinding binding) => action switch
    {
        HotkeyAction.Area => this with { AreaHotkey = binding },
        HotkeyAction.Window => this with { WindowHotkey = binding },
        HotkeyAction.Fullscreen => this with { FullscreenHotkey = binding },
        HotkeyAction.Library => this with { LibraryHotkey = binding },
        _ => this
    };

    /// <summary>Clamps every value to a supported choice so hand-edited or future files cannot put the app in an unsupported state.</summary>
    internal CaptureSettings Normalize() => this with
    {
        RetentionDays = RetentionDays is NeverExpire or (>= 1 and <= 365) ? RetentionDays : 7,
        CaptureDelaySeconds = DelayChoices.Contains(CaptureDelaySeconds) ? CaptureDelaySeconds : 0,
        ThumbnailAutoDismissSeconds = AutoDismissChoices.Contains(ThumbnailAutoDismissSeconds) ? ThumbnailAutoDismissSeconds : 0,
        SaveFolder = SaveFolder?.Trim() ?? string.Empty,
        FileNamePattern = string.IsNullOrWhiteSpace(FileNamePattern) ? FileNameTemplate.DefaultPattern : FileNamePattern,
        FileNameCounter = Math.Max(1, FileNameCounter),
        ImageFormat = Enum.IsDefined(ImageFormat) ? ImageFormat : CaptureImageFormat.Png,
        JpegQuality = Math.Clamp(JpegQuality, 10, 100),
        ExportMaxDimension = ExportResize.Choices.Contains(ExportMaxDimension) ? ExportMaxDimension : 0,
        AreaHotkey = SafeHotkey(AreaHotkey),
        WindowHotkey = SafeHotkey(WindowHotkey),
        FullscreenHotkey = SafeHotkey(FullscreenHotkey),
        LibraryHotkey = SafeHotkey(LibraryHotkey)
    };

    private static HotkeyBinding SafeHotkey(HotkeyBinding binding) =>
        HotkeyBinding.Validate(binding) is null ? binding : HotkeyBinding.None;
}

/// <summary>Converts between the on-disk settings.json shape and <see cref="CaptureSettings"/>.</summary>
internal static class CaptureSettingsMapper
{
    internal static CaptureSettings FromDto(HistorySettingsDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        CaptureSettings defaults = CaptureSettings.Default;
        return new CaptureSettings
        {
            RetentionDays = dto.KeepCapturesForever == true ? CaptureSettings.NeverExpire : dto.RetentionDays,
            ReplaceScreenshotShortcut = dto.ReplaceScreenshotShortcut,
            StartWithWindows = dto.StartWithWindows,
            DragAsFile = dto.DragAsFile,
            CaptureDelaySeconds = dto.CaptureDelaySeconds ?? defaults.CaptureDelaySeconds,
            CopyToClipboard = dto.CopyToClipboard ?? defaults.CopyToClipboard,
            ShowThumbnail = dto.ShowThumbnail ?? defaults.ShowThumbnail,
            OpenEditorAfterCapture = dto.OpenEditorAfterCapture ?? defaults.OpenEditorAfterCapture,
            PlayCaptureSound = dto.PlayCaptureSound ?? defaults.PlayCaptureSound,
            ThumbnailAutoDismissSeconds = dto.ThumbnailAutoDismissSeconds ?? defaults.ThumbnailAutoDismissSeconds,
            SaveFolder = dto.SaveFolder ?? defaults.SaveFolder,
            AutoSave = dto.AutoSave ?? defaults.AutoSave,
            FileNamePattern = dto.FileNamePattern ?? defaults.FileNamePattern,
            FileNameCounter = dto.FileNameCounter ?? defaults.FileNameCounter,
            ImageFormat = ParseFormat(dto.ImageFormat),
            JpegQuality = dto.JpegQuality ?? defaults.JpegQuality,
            ExportMaxDimension = dto.ExportMaxDimension ?? defaults.ExportMaxDimension,
            ResizeClipboardCopies = dto.ResizeClipboardCopies ?? defaults.ResizeClipboardCopies,
            AreaHotkey = FromDto(dto.AreaHotkey),
            WindowHotkey = FromDto(dto.WindowHotkey),
            FullscreenHotkey = FromDto(dto.FullscreenHotkey),
            LibraryHotkey = FromDto(dto.LibraryHotkey),
            SearchTextInImages = dto.SearchTextInImages ?? defaults.SearchTextInImages
        }.Normalize();
    }

    internal static HistorySettingsDto ToDto(CaptureSettings settings, Dictionary<string, JsonElement>? additionalFields)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new HistorySettingsDto
        {
            RetentionDays = settings.KeepForever ? CaptureSettings.ForeverCompatibilityDays : settings.RetentionDays,
            KeepCapturesForever = settings.KeepForever,
            ReplaceScreenshotShortcut = settings.ReplaceScreenshotShortcut,
            StartWithWindows = settings.StartWithWindows,
            DragAsFile = settings.DragAsFile,
            CaptureDelaySeconds = settings.CaptureDelaySeconds,
            CopyToClipboard = settings.CopyToClipboard,
            ShowThumbnail = settings.ShowThumbnail,
            OpenEditorAfterCapture = settings.OpenEditorAfterCapture,
            PlayCaptureSound = settings.PlayCaptureSound,
            ThumbnailAutoDismissSeconds = settings.ThumbnailAutoDismissSeconds,
            SaveFolder = settings.SaveFolder,
            AutoSave = settings.AutoSave,
            FileNamePattern = settings.FileNamePattern,
            FileNameCounter = settings.FileNameCounter,
            ImageFormat = settings.ImageFormat == CaptureImageFormat.Jpeg ? "jpeg" : "png",
            JpegQuality = settings.JpegQuality,
            ExportMaxDimension = settings.ExportMaxDimension,
            ResizeClipboardCopies = settings.ResizeClipboardCopies,
            AreaHotkey = ToDto(settings.AreaHotkey),
            WindowHotkey = ToDto(settings.WindowHotkey),
            FullscreenHotkey = ToDto(settings.FullscreenHotkey),
            LibraryHotkey = ToDto(settings.LibraryHotkey),
            SearchTextInImages = settings.SearchTextInImages,
            AdditionalFields = additionalFields
        };
    }

    private static CaptureImageFormat ParseFormat(string? value) =>
        value?.Trim().ToLowerInvariant() is "jpeg" or "jpg" ? CaptureImageFormat.Jpeg : CaptureImageFormat.Png;

    private static HotkeyBinding FromDto(HotkeyDto? dto)
    {
        const uint knownModifiers = (uint)(HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Windows);
        if (dto is null || dto.Key is <= 0 or > 0xFE || (dto.Modifiers & ~knownModifiers) != 0) return HotkeyBinding.None;
        return new HotkeyBinding((HotkeyModifiers)dto.Modifiers, dto.Key);
    }

    private static HotkeyDto ToDto(HotkeyBinding binding) =>
        new() { Modifiers = binding.IsEmpty ? 0 : (uint)binding.Modifiers, Key = binding.VirtualKey };
}
