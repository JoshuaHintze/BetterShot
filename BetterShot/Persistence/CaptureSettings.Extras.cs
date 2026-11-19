using BetterShot.Platform;

namespace BetterShot.Persistence;

/// <summary>Scrolling capture shortcut, first-run setup, and update-check preferences.</summary>
internal sealed partial record CaptureSettings
{
    public HotkeyBinding ScrollingHotkey { get; init; }
    /// <summary>False only for a brand-new install; settings files written before this field existed count as set up.</summary>
    public bool OnboardingCompleted { get; init; }
    public bool AutoCheckForUpdates { get; init; } = true;
    public DateTime? LastUpdateCheckUtc { get; init; }
    /// <summary>Release version the user chose to skip; empty when none.</summary>
    public string SkippedUpdateVersion { get; init; } = string.Empty;
}

internal sealed partial class HistorySettingsDto
{
    // Phase 5 options. All optional so older settings files load unchanged and older builds ignore them.
    public HotkeyDto? ScrollingHotkey { get; set; }
    public bool? OnboardingCompleted { get; set; }
    public bool? AutoCheckForUpdates { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    public string? SkippedUpdateVersion { get; set; }
}

internal static partial class CaptureSettingsMapper
{
    internal static CaptureSettings WithExtrasFrom(this CaptureSettings settings, HistorySettingsDto dto) => settings with
    {
        ScrollingHotkey = FromDto(dto.ScrollingHotkey),
        // A settings file without this field was written by an earlier build, so the user is not new.
        OnboardingCompleted = dto.OnboardingCompleted ?? true,
        AutoCheckForUpdates = dto.AutoCheckForUpdates ?? CaptureSettings.Default.AutoCheckForUpdates,
        LastUpdateCheckUtc = dto.LastUpdateCheckUtc?.ToUniversalTime(),
        SkippedUpdateVersion = dto.SkippedUpdateVersion ?? string.Empty
    };

    internal static HistorySettingsDto WithExtrasFrom(this HistorySettingsDto dto, CaptureSettings settings)
    {
        dto.ScrollingHotkey = ToDto(settings.ScrollingHotkey);
        dto.OnboardingCompleted = settings.OnboardingCompleted;
        dto.AutoCheckForUpdates = settings.AutoCheckForUpdates;
        dto.LastUpdateCheckUtc = settings.LastUpdateCheckUtc;
        dto.SkippedUpdateVersion = settings.SkippedUpdateVersion;
        return dto;
    }
}
