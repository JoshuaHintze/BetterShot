using BetterShot.Onboarding;
using BetterShot.Platform;
using System.IO;
using CaptureMode = BetterShot.Capture.CaptureMode;

namespace BetterShot;

/// <summary>First-run setup and the services that only run for the real interactive app (never for diagnostics).</summary>
public partial class MainWindow : IOnboardingHost
{
    private OnboardingWindow? _onboarding;

    /// <summary>Called once by App for the normal interactive launch. Diagnostic runs never call this.</summary>
    internal void StartInteractiveServices(bool showOnboarding)
    {
        StartBackgroundUpdateChecks();
        if (showOnboarding && !CurrentSettings.OnboardingCompleted) ShowOnboarding();
    }

    internal OnboardingWindow ShowOnboarding()
    {
        if (_onboarding is { IsLoaded: true }) { _onboarding.Activate(); return _onboarding; }
        _onboarding = new OnboardingWindow(this);
        if (IsVisible) _onboarding.Owner = this;
        _onboarding.Closed += (_, _) => _onboarding = null;
        _onboarding.Show();
        _onboarding.Activate();
        return _onboarding;
    }

    /// <summary>Concatenates every bundled license notice except ONNX Runtime's very long third-party list, which is referenced by name.</summary>
    private static string ReadLicenseNotices()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "ThirdPartyNotices");
        string[] files = Directory.GetFiles(folder, "*.txt").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        var sections = new List<string>();
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            sections.Add(name.Equals("OnnxRuntime-ThirdPartyNotices.txt", StringComparison.OrdinalIgnoreCase)
                ? $"== {name} ==\nONNX Runtime's third-party notices are included in {file}."
                : $"== {name} ==\n{File.ReadAllText(file).Trim()}");
        }
        if (sections.Count == 0) throw new FileNotFoundException("No license notices were found.", folder);
        return string.Join("\n\n", sections);
    }

    bool IOnboardingHost.ReplaceScreenshotShortcut => _shortcutHook.IsEnabled;

    bool IOnboardingHost.SetReplaceScreenshotShortcut(bool enabled)
    {
        if (ReplaceShortcutCheckBox.IsChecked != enabled) ReplaceShortcutCheckBox.IsChecked = enabled; // Runs the Settings handler: save, then apply.
        return _shortcutHook.IsEnabled;
    }

    bool IOnboardingHost.CanConfigureStartup => _startupRegistration is not null && _historyStore is not null;
    bool IOnboardingHost.StartWithWindows => _historyStore?.StartWithWindows ?? false;

    bool IOnboardingHost.SetStartWithWindows(bool enabled)
    {
        if (StartWithWindowsCheckBox.IsChecked != enabled) StartWithWindowsCheckBox.IsChecked = enabled; // Runs the Settings handler.
        return _historyStore?.StartWithWindows ?? false;
    }

    HotkeyBinding IOnboardingHost.HotkeyFor(HotkeyAction action) => CurrentSettings.HotkeyFor(action);

    bool IOnboardingHost.IsHotkeyUnavailable(HotkeyAction action)
    {
        HotkeyBinding binding = CurrentSettings.HotkeyFor(action);
        return !binding.IsEmpty && !_hotkeys.IsRegistered(action) && _source is not null;
    }

    string? IOnboardingHost.ApplyHotkey(HotkeyAction action, HotkeyBinding binding) => ApplyRecordedHotkey(action, binding);
    void IOnboardingHost.BeginHotkeyRecording() => HotkeyRecorder_RecordingStarted(this, EventArgs.Empty);
    void IOnboardingHost.EndHotkeyRecording() => HotkeyRecorder_RecordingEnded(this, EventArgs.Empty);

    void IOnboardingHost.CompleteOnboarding()
    {
        if (CurrentSettings.OnboardingCompleted) return;
        try { UpdateSettings(settings => settings with { OnboardingCompleted = true }); }
        catch (Exception ex) { SetStatus($"Could not remember that setup is finished: {FriendlyMessage(ex)}", error: true); }
    }

    void IOnboardingHost.TakeFirstScreenshot() => Dispatcher.BeginInvoke(() => _ = CaptureAsync(CaptureMode.Area));

    Style? IOnboardingHost.FindStyle(string key) => TryFindResource(key) as Style;
}
