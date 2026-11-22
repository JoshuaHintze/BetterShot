using BetterShot.Export;
using BetterShot.Persistence;
using Microsoft.Win32;
using System.Windows.Controls;

namespace BetterShot;

public partial class MainWindow
{
    private void LoadSoundSettingsIntoControls()
    {
        CaptureSettings settings = CurrentSettings;
        SelectTagged(ScreenshotSoundComboBox, settings.ScreenshotSound);
        SelectTagged(RecordingStartSoundComboBox, settings.RecordingStartSound);
        SelectTagged(RecordingStopSoundComboBox, settings.RecordingStopSound);
        LabelCustom(ScreenshotSoundComboBox, settings.ScreenshotCustomSoundPath);
        LabelCustom(RecordingStartSoundComboBox, settings.RecordingStartCustomSoundPath);
        LabelCustom(RecordingStopSoundComboBox, settings.RecordingStopCustomSoundPath);
    }

    private void SoundCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _updatingSettings) return;
        string screenshot = SelectedTag(ScreenshotSoundComboBox) ?? SoundEffects.Shutter;
        string start = SelectedTag(RecordingStartSoundComboBox) ?? SoundEffects.None;
        string stop = SelectedTag(RecordingStopSoundComboBox) ?? SoundEffects.None;
        TrySaveSettings(settings => settings with
        {
            PlayCaptureSound = ReferenceEquals(sender, ScreenshotSoundComboBox) && screenshot != SoundEffects.None || settings.PlayCaptureSound,
            ScreenshotSound = screenshot,
            RecordingStartSound = start,
            RecordingStopSound = stop
        }, "the sound effects");
    }

    private async void PreviewSound_Click(object sender, RoutedEventArgs e)
    {
        (string id, string path) = SoundFor((sender as FrameworkElement)?.Tag?.ToString());
        await SoundEffects.PlayAsync(id, path, true);
    }

    private async void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        string target = (sender as FrameworkElement)?.Tag?.ToString() ?? "screenshot";
        var dialog = new OpenFileDialog { Title = "Choose a BetterShot sound", Filter = "Sound files (*.mp3;*.wav)|*.mp3;*.wav" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string imported = await SoundEffects.ImportAsync(dialog.FileName);
            UpdateSettings(settings => target switch
            {
                "start" => settings with { RecordingStartSound = SoundEffects.Custom, RecordingStartCustomSoundPath = imported },
                "stop" => settings with { RecordingStopSound = SoundEffects.Custom, RecordingStopCustomSoundPath = imported },
                _ => settings with { PlayCaptureSound = true, ScreenshotSound = SoundEffects.Custom, ScreenshotCustomSoundPath = imported }
            });
            LoadCaptureSettingsIntoControls();
            SetStatus($"Imported {System.IO.Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) { LoadCaptureSettingsIntoControls(); SetStatus($"Could not import the sound: {FriendlyMessage(ex)}", true, true); }
    }

    private (string Id, string Path) SoundFor(string? target)
    {
        CaptureSettings settings = CurrentSettings;
        return target switch
        {
            "start" => (settings.RecordingStartSound, settings.RecordingStartCustomSoundPath),
            "stop" => (settings.RecordingStopSound, settings.RecordingStopCustomSoundPath),
            _ => (settings.ScreenshotSound, settings.ScreenshotCustomSoundPath)
        };
    }

    private static void LabelCustom(ComboBox combo, string path)
    {
        ComboBoxItem? item = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(candidate => candidate.Tag?.ToString() == SoundEffects.Custom);
        if (item is null) return;
        string fileName = System.IO.Path.GetFileName(path);
        string extension = System.IO.Path.GetExtension(fileName);
        string stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length > 65 && stem[^65] == '-' && stem[^64..].All(Uri.IsHexDigit)) stem = stem[..^65];
        item.Content = string.IsNullOrWhiteSpace(path) ? "Custom sound…" : "Custom: " + stem + extension;
        item.ToolTip = string.IsNullOrWhiteSpace(path) ? "Browse for an MP3 or WAV file" : path;
    }
}
