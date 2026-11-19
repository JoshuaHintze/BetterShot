using BetterShot.Platform;

namespace BetterShot.Onboarding;

/// <summary>The settings and actions first-run setup needs. MainWindow implements it with the same code paths as Settings.</summary>
internal interface IOnboardingHost
{
    bool ReplaceScreenshotShortcut { get; }
    /// <summary>Saves and applies the Win+Shift+S choice; returns whether the shortcut is now active.</summary>
    bool SetReplaceScreenshotShortcut(bool enabled);
    bool CanConfigureStartup { get; }
    bool StartWithWindows { get; }
    /// <summary>Saves and applies the startup choice; returns the resulting saved state.</summary>
    bool SetStartWithWindows(bool enabled);
    HotkeyBinding HotkeyFor(HotkeyAction action);
    bool IsHotkeyUnavailable(HotkeyAction action);
    /// <summary>Validates, registers, and saves a shortcut. Returns null on success, otherwise a user-facing reason.</summary>
    string? ApplyHotkey(HotkeyAction action, HotkeyBinding binding);
    void BeginHotkeyRecording();
    void EndHotkeyRecording();
    /// <summary>Remembers that setup was finished or skipped so it is not shown again.</summary>
    void CompleteOnboarding();
    void TakeFirstScreenshot();
    /// <summary>Shared styles (toggle switch, quiet button) from the main window resources, or null if unavailable.</summary>
    Style? FindStyle(string key);
}
