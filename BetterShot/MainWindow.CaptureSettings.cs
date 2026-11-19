using BetterShot.Controls;
using BetterShot.Export;
using BetterShot.Persistence;
using BetterShot.Platform;
using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using CaptureMode = BetterShot.Capture.CaptureMode;

namespace BetterShot;

/// <summary>Settings panel: capture shortcuts, delay, after-capture options, saving, and export format.</summary>
public partial class MainWindow
{
    private static readonly HotkeyAction[] AllHotkeyActions = Enum.GetValues<HotkeyAction>();

    internal HotkeyRecorderButton RecorderFor(HotkeyAction action) => action switch
    {
        HotkeyAction.Area => AreaHotkeyRecorder,
        HotkeyAction.Window => WindowHotkeyRecorder,
        HotkeyAction.Fullscreen => FullscreenHotkeyRecorder,
        HotkeyAction.Record => RecordHotkeyRecorder,
        _ => LibraryHotkeyRecorder
    };

    private static string ActionLabel(HotkeyAction action) => action switch
    {
        HotkeyAction.Area => "Area capture",
        HotkeyAction.Window => "Window capture",
        HotkeyAction.Fullscreen => "Fullscreen capture",
        HotkeyAction.Record => "Record screen",
        _ => "Open library"
    };

    /// <summary>Routes changes to settings.json (atomic, preserves other fields), or to memory for transient diagnostic shells.</summary>
    private void UpdateSettings(Func<CaptureSettings, CaptureSettings> change)
    {
        if (_historyStore is not null) _historyStore.UpdateSettings(change);
        else _transientSettings = change(_transientSettings).Normalize();
    }

    /// <summary>Saves a change and applies it; on failure restores the controls to the saved values and reports why.</summary>
    private void TrySaveSettings(Func<CaptureSettings, CaptureSettings> change, string description)
    {
        try
        {
            UpdateSettings(change);
            ApplySettingsToPresentation();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save {description}: {FriendlyMessage(ex)}", error: true, persistent: true);
        }
        LoadCaptureSettingsIntoControls();
    }

    private void ApplySettingsToPresentation()
    {
        CaptureSettings settings = CurrentSettings;
        _presentation.Export = settings.ToExportOptions();
        _presentation.FileNameCounter = () => CurrentSettings.FileNameCounter;
        TimeSpan dismissAfter = TimeSpan.FromSeconds(settings.ThumbnailAutoDismissSeconds);
        if (_presentation.AutoDismissAfter != dismissAfter) _presentation.AutoDismissAfter = dismissAfter;
    }

    private void LoadCaptureSettingsIntoControls()
    {
        CaptureSettings settings = CurrentSettings;
        _updatingSettings = true;
        try
        {
            SelectTagged(DelayComboBox, settings.CaptureDelaySeconds.ToString(CultureInfo.InvariantCulture));
            CopyToClipboardCheckBox.IsChecked = settings.CopyToClipboard;
            ShowThumbnailCheckBox.IsChecked = settings.ShowThumbnail;
            OpenEditorCheckBox.IsChecked = settings.OpenEditorAfterCapture;
            PlaySoundCheckBox.IsChecked = settings.PlayCaptureSound;
            SelectTagged(AutoDismissComboBox, settings.ThumbnailAutoDismissSeconds.ToString(CultureInfo.InvariantCulture));
            SaveFolderText.Text = settings.EffectiveSaveFolder;
            SaveFolderText.ToolTip = settings.EffectiveSaveFolder;
            AutoSaveCheckBox.IsChecked = settings.AutoSave;
            if (!FileNamePatternBox.IsKeyboardFocusWithin) FileNamePatternBox.Text = settings.FileNamePattern;
            SelectTagged(FormatComboBox, settings.ImageFormat == CaptureImageFormat.Jpeg ? "jpeg" : "png");
            JpegQualitySlider.Value = settings.JpegQuality;
            SelectTagged(ResizeComboBox, settings.ExportMaxDimension.ToString(CultureInfo.InvariantCulture));
            ResizeClipboardCheckBox.IsChecked = settings.ResizeClipboardCopies;
        }
        finally { _updatingSettings = false; }
        LoadRecordingSettingsIntoControls();
        UpdateSettingsDependentUi();
        RefreshHotkeyIndicators();
    }

    private void UpdateSettingsDependentUi()
    {
        bool jpeg = SelectedTag(FormatComboBox) == "jpeg";
        JpegQualityRow.Visibility = jpeg ? Visibility.Visible : Visibility.Collapsed;
        JpegQualityText.Text = ((int)JpegQualitySlider.Value).ToString(CultureInfo.CurrentCulture);
        AutoSaveCheckBox.IsEnabled = ShowThumbnailCheckBox.IsChecked == true;
        AutoSaveCheckBox.ToolTip = AutoSaveCheckBox.IsEnabled ? null : "Captures are always saved while the floating thumbnail is off.";
        UpdateFileNamePreview();
    }

    private void UpdateFileNamePreview()
    {
        string extension = SelectedTag(FormatComboBox) == "jpeg" ? ".jpg" : ".png";
        string name = FileNameTemplate.BuildFileName(FileNamePatternBox.Text, DateTime.Now, CurrentSettings.FileNameCounter, extension);
        FileNamePreviewText.Text = "Preview: " + name;
        FileNamePreviewText.ToolTip = name;
    }

    private void CommitPendingSettingsEdits()
    {
        CommitFileNamePattern();
        CommitJpegQuality();
        CommitRecordingFileNamePattern();
    }

    private void AfterCaptureToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _updatingSettings) return;
        bool copy = CopyToClipboardCheckBox.IsChecked == true, thumbnail = ShowThumbnailCheckBox.IsChecked == true;
        bool editor = OpenEditorCheckBox.IsChecked == true, sound = PlaySoundCheckBox.IsChecked == true;
        bool autoSave = AutoSaveCheckBox.IsChecked == true, resizeClipboard = ResizeClipboardCheckBox.IsChecked == true;
        TrySaveSettings(settings => settings with
        {
            CopyToClipboard = copy,
            ShowThumbnail = thumbnail,
            OpenEditorAfterCapture = editor,
            PlayCaptureSound = sound,
            AutoSave = autoSave,
            ResizeClipboardCopies = resizeClipboard
        }, "the capture options");
    }

    private void DelayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _updatingSettings || !TryParseSelectedTag(DelayComboBox, out int seconds)) return;
        TrySaveSettings(settings => settings with { CaptureDelaySeconds = seconds }, "the capture delay");
    }

    private void AutoDismissCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _updatingSettings || !TryParseSelectedTag(AutoDismissComboBox, out int seconds)) return;
        TrySaveSettings(settings => settings with { ThumbnailAutoDismissSeconds = seconds }, "the thumbnail timeout");
    }

    private void FormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _updatingSettings) return;
        CaptureImageFormat format = SelectedTag(FormatComboBox) == "jpeg" ? CaptureImageFormat.Jpeg : CaptureImageFormat.Png;
        TrySaveSettings(settings => settings with { ImageFormat = format }, "the image format");
    }

    private void ResizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _updatingSettings || !TryParseSelectedTag(ResizeComboBox, out int maxDimension)) return;
        TrySaveSettings(settings => settings with { ExportMaxDimension = maxDimension }, "the export size");
    }

    private void JpegQuality_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        JpegQualityText.Text = ((int)JpegQualitySlider.Value).ToString(CultureInfo.CurrentCulture);
    }

    private void JpegQuality_Commit(object sender, RoutedEventArgs e) => CommitJpegQuality();

    private void CommitJpegQuality()
    {
        if (!IsInitialized || _updatingSettings) return;
        int quality = (int)Math.Round(JpegQualitySlider.Value);
        if (quality == CurrentSettings.JpegQuality) return;
        TrySaveSettings(settings => settings with { JpegQuality = quality }, "the JPEG quality");
    }

    private void FileNamePattern_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) UpdateFileNamePreview();
    }

    private void FileNamePattern_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitFileNamePattern();

    private void FileNamePattern_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitFileNamePattern();
    }

    private void CommitFileNamePattern()
    {
        if (!IsInitialized || _updatingSettings) return;
        string pattern = FileNamePatternBox.Text.Trim();
        if (pattern.Length == 0) pattern = FileNameTemplate.DefaultPattern;
        if (pattern == CurrentSettings.FileNamePattern) { FileNamePatternBox.Text = pattern; return; }
        TrySaveSettings(settings => settings with { FileNamePattern = pattern }, "the file name pattern");
        FileNamePatternBox.Text = CurrentSettings.FileNamePattern;
    }

    private void ChooseSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        string current = CurrentSettings.EffectiveSaveFolder;
        var dialog = new OpenFolderDialog { Title = "Choose where BetterShot saves captures", Multiselect = false };
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) != true) return;
        string folder = dialog.FolderName;
        TrySaveSettings(settings => settings with { SaveFolder = folder }, "the save folder");
    }

    private void OpenSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = CurrentSettings.EffectiveSaveFolder;
        try
        {
            Directory.CreateDirectory(folder);
            using Process? explorer = Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus($"Could not open the save folder: {FriendlyMessage(ex)}", error: true, persistent: true); }
    }

    private void RegisterSavedHotkeys()
    {
        var failures = new List<string>();
        foreach (HotkeyAction action in AllHotkeyActions)
            if (_hotkeys.Register(action, CurrentSettings.HotkeyFor(action)) is string error) failures.Add($"{ActionLabel(action)}: {error}");
        RefreshHotkeyIndicators();
        if (failures.Count == 0) return;
        ShowHotkeyProblem(string.Join(" ", failures));
        SetStatus("Some shortcuts are unavailable. See Settings > Capture.", error: true, persistent: true);
    }

    private void RunHotkeyAction(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.Area: _ = CaptureAsync(CaptureMode.Area); break;
            case HotkeyAction.Window: _ = CaptureAsync(CaptureMode.Window); break;
            case HotkeyAction.Fullscreen: _ = CaptureAsync(CaptureMode.Fullscreen); break;
            case HotkeyAction.Library: ShowAndActivate(); break;
            case HotkeyAction.Record: ToggleRecordingFromShortcut(); break;
        }
    }

    private void HotkeyRecorder_RecordingStarted(object? sender, EventArgs e)
    {
        _hotkeys.Suspend();
        if (_source is null || _fallbackSuspended) return;
        GlobalHotKey.Unregister(_source.Handle);
        _fallbackSuspended = true;
    }

    private void HotkeyRecorder_RecordingEnded(object? sender, EventArgs e)
    {
        if (_fallbackSuspended && _source is not null)
        {
            _fallbackSuspended = false;
            if (!GlobalHotKey.Register(_source.Handle)) SetStatus("Ctrl+Shift+F9 is unavailable; use Capture or the tray menu.", true);
        }
        IReadOnlyDictionary<HotkeyAction, string> failures = _hotkeys.Resume();
        if (failures.Count > 0) ShowHotkeyProblem(string.Join(" ", failures.Select(failure => $"{ActionLabel(failure.Key)}: {failure.Value}")));
        RefreshHotkeyIndicators();
    }

    private void HotkeyRecorder_Recorded(object? sender, HotkeyRecordedEventArgs e)
    {
        if (sender is not HotkeyRecorderButton recorder || !Enum.TryParse(recorder.Tag?.ToString(), out HotkeyAction action)) return;
        ApplyRecordedHotkey(action, e.Binding);
    }

    /// <summary>Validates, checks conflicts, registers with Windows, and only then persists. Failures keep the previous shortcut active.</summary>
    internal string? ApplyRecordedHotkey(HotkeyAction action, HotkeyBinding binding)
    {
        HotkeyBinding previous = CurrentSettings.HotkeyFor(action);
        string? problem = HotkeyBinding.Validate(binding) ?? FindHotkeyConflict(action, binding);
        if (problem is null && (binding != previous || !_hotkeys.IsRegistered(action)))
        {
            problem = _hotkeys.Register(action, binding);
            if (problem is not null) _hotkeys.Register(action, previous);
        }
        if (problem is null && binding != previous)
        {
            try { UpdateSettings(settings => settings.WithHotkey(action, binding)); }
            catch (Exception ex) { problem = $"{binding} works for now but could not be saved: {FriendlyMessage(ex)}."; }
        }
        ShowHotkeyProblem(problem);
        if (problem is null) SetStatus(binding.IsEmpty ? $"{ActionLabel(action)} shortcut cleared." : $"{ActionLabel(action)}: {binding}.");
        RefreshHotkeyIndicators();
        return problem;
    }

    private string? FindHotkeyConflict(HotkeyAction action, HotkeyBinding binding)
    {
        if (binding.IsEmpty) return null;
        if (binding == HotkeyBinding.AreaFallback) return $"{binding} is already BetterShot's built-in area capture shortcut.";
        foreach (HotkeyAction other in AllHotkeyActions)
            if (other != action && CurrentSettings.HotkeyFor(other) == binding) return $"{binding} is already used for {ActionLabel(other)}.";
        return null;
    }

    private void RefreshHotkeyIndicators()
    {
        foreach (HotkeyAction action in AllHotkeyActions)
        {
            HotkeyRecorderButton recorder = RecorderFor(action);
            HotkeyBinding binding = CurrentSettings.HotkeyFor(action);
            recorder.Binding = binding;
            recorder.IsUnavailable = !binding.IsEmpty && !_hotkeys.IsRegistered(action) && _source is not null && !recorder.IsRecording;
        }
        WindowButton.ToolTip = WithShortcut("Click a window to capture it", CurrentSettings.WindowHotkey);
        DisplayButton.ToolTip = "Click a display to capture it";
        FullscreenButton.ToolTip = WithShortcut("Capture the whole display under the pointer immediately", CurrentSettings.FullscreenHotkey);
        RefreshRecordButtonToolTip();
    }

    private static string WithShortcut(string text, HotkeyBinding binding) => binding.IsEmpty ? text : $"{text} ({binding})";

    private void ShowHotkeyProblem(string? problem)
    {
        HotkeyProblemText.Text = problem ?? string.Empty;
        HotkeyProblemText.Visibility = string.IsNullOrEmpty(problem) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string? SelectedTag(ComboBox comboBox) => (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static bool TryParseSelectedTag(ComboBox comboBox, out int value) =>
        int.TryParse(SelectedTag(comboBox), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static void SelectTagged(ComboBox comboBox, string tag) =>
        comboBox.SelectedItem = comboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == tag) ?? comboBox.Items[0];
}
