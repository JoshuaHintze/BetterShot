using BetterShot.Persistence;
using BetterShot.Platform;
using System.IO;
using System.Text.Json.Nodes;

namespace BetterShot.Onboarding;

/// <summary>Persistence checks for the first-run flag and the other phase 5 settings, using isolated fixture folders.</summary>
internal static class OnboardingDiagnostics
{
    internal static string Run(string root)
    {
        VerifyNewInstallShowsSetup(Path.Combine(root, "new"));
        VerifyExistingUserSkipsSetup(Path.Combine(root, "existing"));
        VerifyExtrasRoundTripAndPreserveUnknownFields(Path.Combine(root, "roundtrip"));
        return "first-run flag (new install shows setup, existing settings skip it, completion persists), scrolling hotkey, update preferences, and unknown-field preservation";
    }

    private static void VerifyNewInstallShowsSetup(string root)
    {
        var store = new CaptureHistoryStore(root);
        Check(!store.Settings.OnboardingCompleted, "A brand-new install would not show first-run setup.");
        Check(store.Settings.AutoCheckForUpdates, "Automatic update checks are not on by default.");
        store.UpdateSettings(settings => settings with { OnboardingCompleted = true });
        Check(new CaptureHistoryStore(root).Settings.OnboardingCompleted, "Finishing setup was not remembered.");
    }

    private static void VerifyExistingUserSkipsSetup(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), "{ \"RetentionDays\": 14, \"ReplaceScreenshotShortcut\": true }");
        CaptureSettings settings = new CaptureHistoryStore(root).Settings;
        Check(settings.OnboardingCompleted && settings.RetentionDays == 14, "An existing user from an earlier build would be shown first-run setup.");
    }

    private static void VerifyExtrasRoundTripAndPreserveUnknownFields(string root)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, "{ \"RetentionDays\": 7, \"OnboardingCompleted\": false, \"FutureFeature\": { \"enabled\": true } }");
        var store = new CaptureHistoryStore(root);
        Check(!store.Settings.OnboardingCompleted, "An explicit unfinished setup flag was ignored.");
        var scrolling = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x52);
        DateTime checkedAt = new(2026, 11, 19, 8, 30, 0, DateTimeKind.Utc);
        store.UpdateSettings(settings => settings with
        {
            ScrollingHotkey = scrolling, AutoCheckForUpdates = false, LastUpdateCheckUtc = checkedAt, SkippedUpdateVersion = " 0.3.0 "
        });
        CaptureSettings reloaded = new CaptureHistoryStore(root).Settings;
        Check(reloaded.ScrollingHotkey == scrolling && reloaded.HotkeyFor(HotkeyAction.Scrolling) == scrolling, "The scrolling capture shortcut was not saved.");
        Check(!reloaded.AutoCheckForUpdates && reloaded.LastUpdateCheckUtc == checkedAt && reloaded.SkippedUpdateVersion == "0.3.0", "Update preferences were not saved.");
        JsonNode? json = JsonNode.Parse(File.ReadAllText(path));
        Check(json?["FutureFeature"]?["enabled"]?.GetValue<bool>() == true, "An unknown settings field was lost.");
        File.WriteAllText(path, "{ \"RetentionDays\": 7, \"ScrollingHotkey\": { \"Modifiers\": 0, \"Key\": 65 } }");
        Check(new CaptureHistoryStore(root).Settings.ScrollingHotkey.IsEmpty, "An unsafe scrolling shortcut (bare letter) was accepted from disk.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("First-run setup: " + message);
    }
}
