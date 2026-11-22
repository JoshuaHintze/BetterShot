using BetterShot.Export;
using BetterShot.Persistence;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterShot;

/// <summary>Isolated, silent checks for sound settings, imports, bundled media, and Settings layout.</summary>
internal static class SoundEffectsDiagnostics
{
    internal static async Task RunAsync(string? output)
    {
        string directory = string.IsNullOrWhiteSpace(output) ? AppContext.BaseDirectory
            : Path.HasExtension(output) ? Path.GetDirectoryName(Path.GetFullPath(output))! : output;
        Directory.CreateDirectory(directory);
        string root = Path.Combine(directory, "sound-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        VerifySettings(Path.Combine(root, "settings"));
        await VerifyImportsAsync(Path.Combine(root, "imports"));
        VerifyColdStartPriming(Path.Combine(root, "cold-start"));
        await VerifyPlaybackLifecycleAsync();
        await VerifyUiAsync(Path.Combine(root, "ui"), directory);
        App.WriteTestResult(Path.Combine(directory, "sound-test.txt"),
            "PASS: legacy sound defaults/off mapping, three sound selections and custom paths, unknown-field preservation, isolated WAV/MP3 imports and rejection, bundled Giggity decoding, block-aligned cold-start silence with smooth generated cue edges, preemptive single-cue playback cleanup, and Settings selector layout");
    }

    private static void VerifySettings(string root)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, "{\"RetentionDays\":7,\"PlayCaptureSound\":false,\"FutureSound\":{\"version\":9}}");
        var legacyOff = new CaptureHistoryStore(root);
        Check(!legacyOff.Settings.PlayCaptureSound, "Legacy capture-sound off was not preserved.");
        Check(legacyOff.Settings.ScreenshotSound == SoundEffects.Shutter && legacyOff.Settings.RecordingStartSound == SoundEffects.None && legacyOff.Settings.RecordingStopSound == SoundEffects.None,
            "Missing sound selections did not receive compatible defaults.");

        string screenshot = Path.Combine(root, "screen.wav"), start = Path.Combine(root, "start.mp3"), stop = Path.Combine(root, "stop.wav");
        legacyOff.UpdateSettings(settings => settings with
        {
            PlayCaptureSound = true,
            ScreenshotSound = SoundEffects.Custom,
            ScreenshotCustomSoundPath = screenshot,
            RecordingStartSound = SoundEffects.Up,
            RecordingStartCustomSoundPath = start,
            RecordingStopSound = SoundEffects.Custom,
            RecordingStopCustomSoundPath = stop
        });
        CaptureSettings saved = new CaptureHistoryStore(root).Settings;
        Check(saved.PlayCaptureSound && saved.ScreenshotSound == SoundEffects.Custom && saved.ScreenshotCustomSoundPath == screenshot,
            "Screenshot sound selection or custom path did not round-trip.");
        Check(saved.RecordingStartSound == SoundEffects.Up && saved.RecordingStartCustomSoundPath == start && saved.RecordingStopSound == SoundEffects.Custom && saved.RecordingStopCustomSoundPath == stop,
            "Recording sound selections or custom paths did not round-trip.");
        JsonNode json = JsonNode.Parse(File.ReadAllText(path))!;
        Check(json["FutureSound"]?["version"]?.GetValue<int>() == 9, "A sound settings write dropped an unknown field.");

        string freshRoot = Path.Combine(root, "fresh");
        CaptureSettings fresh = new CaptureHistoryStore(freshRoot).Settings;
        Check(fresh.PlayCaptureSound && fresh.ScreenshotSound == SoundEffects.Shutter && fresh.RecordingStartSound == SoundEffects.None && fresh.RecordingStopSound == SoundEffects.None,
            "Fresh sound defaults changed.");
    }

    private static async Task VerifyImportsAsync(string root)
    {
        string source = Path.Combine(root, "source"), managed = Path.Combine(root, "managed");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "valid.wav"), CaptureSound.CreateToneWave([(660, 0, .18)]));
        string wav = await SoundEffects.ImportAsync(Path.Combine(source, "valid.wav"), managed);
        Check(File.Exists(wav) && Path.GetFullPath(wav).StartsWith(Path.GetFullPath(managed), StringComparison.OrdinalIgnoreCase), "WAV import escaped its isolated managed folder.");

        string bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "giggity.mp3");
        Check(File.Exists(bundled), "Bundled Giggity MP3 is missing from the build output.");
        string mp3 = await SoundEffects.ImportAsync(bundled, managed);
        Check(File.Exists(mp3) && mp3.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase), "Bundled Giggity MP3 was not decoded and imported.");

        await RejectAsync(Path.Combine(source, "missing.wav"), managed, "Missing import was accepted.");
        string corrupt = Path.Combine(source, "corrupt.wav"); File.WriteAllText(corrupt, "not audio");
        await RejectAsync(corrupt, managed, "Corrupt import was accepted.");
        string longWave = Path.Combine(source, "long.wav"); File.WriteAllBytes(longWave, LongSilentWave(TimeSpan.FromSeconds(16)));
        await RejectAsync(longWave, managed, "Overlong import was accepted.");
        string large = Path.Combine(source, "large.wav");
        using (FileStream stream = File.Create(large)) stream.SetLength(SoundEffects.MaxCustomBytes + 1);
        await RejectAsync(large, managed, "Oversized import was accepted.");
    }

    private static async Task RejectAsync(string path, string root, string message)
    {
        try { await SoundEffects.ImportAsync(path, root); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException(message);
    }

    private static async Task VerifyPlaybackLifecycleAsync()
    {
        // Do not let the diagnostic make noise: each graph is superseded immediately, before
        // asynchronous output normally reaches Play. This still exercises the regression
        // path for MP3 -> generated WAV -> None and proves every displaced waiter completes.
        Task mp3 = SoundEffects.PlayAsync(SoundEffects.Giggity, "", waitForFinish: true);
        Task wave = SoundEffects.PlayAsync(SoundEffects.Up, "", waitForFinish: true);
        Task none = SoundEffects.PlayAsync(SoundEffects.None, "", waitForFinish: true);
        await Task.WhenAll(mp3, wave, none).WaitAsync(TimeSpan.FromSeconds(2));
        Check(!SoundEffects.IsPlayingForTest, "A preempted sound left a decoder/output graph active.");

        Task final = SoundEffects.PlayAsync(SoundEffects.Down, "", waitForFinish: true);
        SoundEffects.Shutdown();
        await final.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!SoundEffects.IsPlayingForTest, "Sound shutdown left a decoder/output graph active.");
    }

    private static void VerifyColdStartPriming(string root)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "up.wav");
        File.WriteAllBytes(path, CaptureSound.CreateToneWave([(520, .00, .13), (780, .11, .20)]));
        (byte[] bytes, NAudio.Wave.WaveFormat format, int silenceBytes, int trailingSilenceBytes) = SoundEffects.DecodeForTest(path);
        Check(silenceBytes > 0 && silenceBytes % format.BlockAlign == 0, "Cold-start silence is missing or not sample aligned.");
        Check(trailingSilenceBytes > 0 && trailingSilenceBytes % format.BlockAlign == 0, "Endpoint-drain silence is missing or not sample aligned.");
        Check(bytes.AsSpan(0, silenceBytes).IndexOfAnyExcept((byte)0) < 0, "Cold-start lead-in contains audible data.");
        Check(bytes.AsSpan(bytes.Length - trailingSilenceBytes).IndexOfAnyExcept((byte)0) < 0, "Endpoint-drain tail contains audible data.");
        Check(TimeSpan.FromSeconds((double)silenceBytes / format.AverageBytesPerSecond) >= TimeSpan.FromMilliseconds(35), "Cold-start silence is too short to settle the endpoint.");
        Check(TimeSpan.FromSeconds((double)trailingSilenceBytes / format.AverageBytesPerSecond) >= TimeSpan.FromMilliseconds(95), "Endpoint-drain tail is too short to flush shared-mode buffering.");
        int firstSample = silenceBytes;
        Check(bytes.AsSpan(firstSample, Math.Min(format.BlockAlign, bytes.Length - firstSample)).IndexOfAnyExcept((byte)0) < 0,
            "Generated cue's first sample is not silent; its edge could click.");
        int cueEnd = bytes.Length - trailingSilenceBytes;
        Check(bytes.AsSpan(Math.Max(firstSample, cueEnd - format.BlockAlign * 4), Math.Min(format.BlockAlign * 4, cueEnd - firstSample)).IndexOfAnyExcept((byte)0) < 0,
            "Generated cue does not end at silence; its edge could click.");
    }

    private static byte[] LongSilentWave(TimeSpan duration)
    {
        const int sampleRate = 8000;
        int dataLength = checked((int)(duration.TotalSeconds * sampleRate * 2));
        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true);
        writer.Write("RIFF"u8.ToArray()); writer.Write(36 + dataLength); writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray()); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8.ToArray()); writer.Write(dataLength); writer.Write(new byte[dataLength]);
        return stream.ToArray();
    }

    private static async Task VerifyUiAsync(string root, string output)
    {
        Directory.CreateDirectory(root);
        var store = new CaptureHistoryStore(root);
        store.UpdateSettings(settings => settings with
        {
            PlayCaptureSound = true,
            ScreenshotSound = SoundEffects.SoftChime,
            ScreenshotCustomSoundPath = Path.Combine(root, "friendly-tone.wav"),
            RecordingStartSound = SoundEffects.Up,
            RecordingStopSound = SoundEffects.Down
        });
        MainWindow? shell = null;
        try
        {
            shell = new MainWindow(enableHistory: true, historyRoot: root) { Width = 1100, Height = 820 };
            Application.Current.MainWindow = shell;
            shell.Show(); shell.OpenSettingsForTest(); await SettleAsync(shell);
            Check(Tag(shell.ScreenshotSoundComboBox) == SoundEffects.SoftChime && Tag(shell.RecordingStartSoundComboBox) == SoundEffects.Up && Tag(shell.RecordingStopSoundComboBox) == SoundEffects.Down,
                "Settings selectors did not load all three saved sound choices.");
            ComboBoxItem custom = shell.ScreenshotSoundComboBox.Items.Cast<ComboBoxItem>().Single(item => item.Tag?.ToString() == SoundEffects.Custom);
            Check(custom.Content?.ToString() == "Custom: friendly-tone.wav", "Imported custom sound label is not readable.");
            shell.PlaySoundCheckBox.IsChecked = false; await SettleAsync(shell);
            shell.ScreenshotSoundComboBox.SelectedItem = shell.ScreenshotSoundComboBox.Items.Cast<ComboBoxItem>().Single(item => item.Tag?.ToString() == SoundEffects.Giggity);
            await SettleAsync(shell);
            CaptureSettings selected = new CaptureHistoryStore(root).Settings;
            Check(selected.PlayCaptureSound && selected.ScreenshotSound == SoundEffects.Giggity && Tag(shell.ScreenshotSoundComboBox) == SoundEffects.Giggity,
                "Choosing a screenshot sound while sound was off reloaded the old choice or failed to enable playback.");
            shell.RecordingStartSoundComboBox.SelectedItem = shell.RecordingStartSoundComboBox.Items.Cast<ComboBoxItem>().Single(item => item.Tag?.ToString() == SoundEffects.Shutter);
            shell.RecordingStopSoundComboBox.SelectedItem = shell.RecordingStopSoundComboBox.Items.Cast<ComboBoxItem>().Single(item => item.Tag?.ToString() == SoundEffects.SoftChime);
            await SettleAsync(shell);
            selected = new CaptureHistoryStore(root).Settings;
            Check(selected.RecordingStartSound == SoundEffects.Shutter && selected.RecordingStopSound == SoundEffects.SoftChime,
                "Separate recording start/stop selector changes were not saved.");
            BringIntoView(shell.ScreenshotSoundComboBox); await SettleAsync(shell);
            Check(Fits(shell.ScreenshotSoundComboBox), "Screenshot sound selector does not fit at normal Settings width.");
            Render(shell, Path.Combine(output, "sound-settings-capture.png"));

            shell.Width = shell.MinWidth; shell.Height = shell.MinHeight; await SettleAsync(shell);
            BringIntoView(shell.RecordingStopSoundComboBox); await SettleAsync(shell);
            Check(Fits(shell.RecordingStartSoundComboBox) && Fits(shell.RecordingStopSoundComboBox), "Recording sound selectors do not fit at minimum Settings width.");
            Render(shell, Path.Combine(output, "sound-settings-recording.png"));
        }
        finally { shell?.CloseForTest(); }
    }

    private static string? Tag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
    private static void BringIntoView(FrameworkElement element) => element.BringIntoView(new Rect(new Size(Math.Max(1, element.ActualWidth), Math.Max(1, element.ActualHeight))));
    private static bool Fits(FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0 || !element.IsVisible) return false;
        DependencyObject? ancestor = VisualTreeHelper.GetParent(element);
        while (ancestor is not null && ancestor is not System.Windows.Controls.ScrollContentPresenter)
            ancestor = VisualTreeHelper.GetParent(ancestor);
        if (ancestor is not FrameworkElement viewport || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) return false;
        Rect bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        var visible = new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight);
        const double tolerance = 0.5;
        return bounds.Left >= visible.Left - tolerance && bounds.Top >= visible.Top - tolerance
            && bounds.Right <= visible.Right + tolerance && bounds.Bottom <= visible.Bottom + tolerance;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static async Task SettleAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    private static void Render(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        DpiScale dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path); encoder.Save(stream);
    }
}
