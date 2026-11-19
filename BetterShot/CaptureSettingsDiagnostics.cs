using BetterShot.Editing;
using BetterShot.Export;
using BetterShot.Persistence;
using BetterShot.Platform;
using BetterShot.Presentation;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterShot;

/// <summary>
/// Phase 2 capture-option checks: file-name templating, sanitizing, collisions, resize math, export
/// encoding, settings migration/preservation, hotkey validation, shutter sound, and the Settings UI wiring.
/// Uses isolated fixture folders only; never touches real history, settings, or the user's Pictures folder.
/// </summary>
internal static class CaptureSettingsDiagnostics
{
    private static readonly DateTime SampleTime = new(2026, 8, 5, 10, 22, 31, DateTimeKind.Local);

    internal static async Task RunAsync(string? output)
    {
        string directory = string.IsNullOrWhiteSpace(output) ? AppContext.BaseDirectory : Path.HasExtension(output) ? Path.GetDirectoryName(Path.GetFullPath(output))! : output;
        Directory.CreateDirectory(directory);
        string runRoot = Path.Combine(directory, "capture-settings-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        VerifyTemplating();
        VerifySanitizing();
        VerifyCollisions(Path.Combine(runRoot, "collisions"));
        VerifyResize();
        VerifyExportEncoding(Path.Combine(runRoot, "export"));
        VerifySettingsMigration(Path.Combine(runRoot, "settings"));
        HotkeyBinding.RunSelfTest();
        VerifyShutterSound();
        await VerifyUiAsync(Path.Combine(runRoot, "ui"), directory);
        App.WriteTestResult(Path.Combine(directory, "capture-settings-test.txt"), "PASS: file-name tokens, sanitizing, reserved names, collision suffixes, no-overwrite saves, resize dimensions, PNG/JPEG export, settings migration and field preservation, keep-forever retention, hotkey validation/conflicts, shutter sound format, thumbnail-off auto-save, auto-dismiss, and Settings rendering");
    }

    private static void VerifyTemplating()
    {
        Check(FileNameTemplate.Expand("%date %time", SampleTime, 1) == "2026-08-05 10.22.31", "%date/%time format changed.");
        Check(FileNameTemplate.Expand("%year-%month-%day %hour.%minute.%second #%counter", SampleTime, 7) == "2026-08-05 10.22.31 #7", "Individual tokens expanded incorrectly.");
        Check(FileNameTemplate.Expand("%DATE %Minute %month", SampleTime, 1) == "2026-08-05 22 08", "Tokens are not case-insensitive or %minute/%month were confused.");
        Check(FileNameTemplate.Expand("%foo 100% %", SampleTime, 1) == "%foo 100% %", "Unknown % sequences were not kept literally.");
        Check(FileNameTemplate.Expand("", SampleTime, 1) == "BetterShot 2026-08-05 at 10.22.31", "Empty pattern did not fall back to the default.");
        Check(FileNameTemplate.Expand("%counter", SampleTime, 0) == "1", "Counter below one was not clamped.");
        Check(FileNameTemplate.UsesCounter("Shot %COUNTER") && !FileNameTemplate.UsesCounter(FileNameTemplate.DefaultPattern), "Counter detection failed.");
        Check(FileNameTemplate.BuildFileName(FileNameTemplate.DefaultPattern, SampleTime, 1, ".png") == "BetterShot 2026-08-05 at 10.22.31.png", "Default file name changed.");
    }

    private static void VerifySanitizing()
    {
        Check(FileNameTemplate.Sanitize("a<b>c:d\"e/f\\g|h?i*j") == "a-b-c-d-e-f-g-h-i-j", "Invalid Windows characters were not replaced.");
        Check(FileNameTemplate.Sanitize("tab\there") == "tab-here", "Control characters were not replaced.");
        Check(FileNameTemplate.Sanitize("..\\..\\escape") == "..-..-escape", "Path separators survived sanitizing.");
        Check(FileNameTemplate.Sanitize("  trailing dots... ") == "trailing dots", "Trailing dots/spaces were not trimmed.");
        Check(FileNameTemplate.Sanitize("") == "BetterShot" && FileNameTemplate.Sanitize("...") == "BetterShot" && FileNameTemplate.Sanitize("/:*") == "BetterShot", "Empty results did not fall back.");
        Check(FileNameTemplate.Sanitize("CON") == "_CON" && FileNameTemplate.Sanitize("lpt1.backup") == "_lpt1.backup" && FileNameTemplate.Sanitize("CONSOLE") == "CONSOLE", "Reserved device names were mishandled.");
        Check(FileNameTemplate.Sanitize(new string('x', 400)).Length == FileNameTemplate.MaximumStemLength, "Overlong names were not truncated.");
        Check(FileNameTemplate.BuildFileName("%hour:%minute", SampleTime, 1, ".jpg") == "10-22.jpg", "Colons in patterns were not sanitized.");
    }

    private static void VerifyCollisions(string root)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(root, "x.png"), Path.Combine(root, "x (2).png") };
        Check(FileNameTemplate.ResolveUniquePath(root, "x.png", taken.Contains) == Path.Combine(root, "x (3).png"), "Collision suffix did not skip taken names.");
        Check(FileNameTemplate.ResolveUniquePath(root, "free.jpg", taken.Contains) == Path.Combine(root, "free.jpg"), "A free name was suffixed.");

        Directory.CreateDirectory(root);
        var options = ExportOptions.Default with { SaveFolder = root, FileNamePattern = "Fixed name" };
        string userFile = Path.Combine(root, "Fixed name.png");
        File.WriteAllText(userFile, "user-owned file sentinel");
        string userHash = Hash(userFile);
        BitmapSource image = SampleBitmap(64, 40);
        string first = ImageExporter.SaveToFolder(image, options, SampleTime, 1);
        string second = ImageExporter.SaveToFolder(image, options, SampleTime, 1);
        Check(Path.GetFileName(first) == "Fixed name (2).png" && Path.GetFileName(second) == "Fixed name (3).png", "Folder saves did not pick the next free name.");
        Check(Hash(userFile) == userHash, "Saving overwrote an existing user file.");
        Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Atomic save left a temporary file.");
    }

    private static void VerifyResize()
    {
        Check(ExportResize.FitWithin(3840, 2160, 1920) == (1920, 1080), "4K landscape did not fit 1920.");
        Check(ExportResize.FitWithin(1080, 1920, 1280) == (720, 1280), "Portrait did not fit 1280.");
        Check(ExportResize.FitWithin(1000, 500, 1920) == (1000, 500), "Small image was upscaled or changed.");
        Check(ExportResize.FitWithin(2560, 1440, 0) == (2560, 1440), "Full size changed dimensions.");
        Check(ExportResize.FitWithin(5000, 1, 1280) == (1280, 1), "Extreme aspect lost its minimum dimension.");
        Check(ExportResize.FitWithin(1921, 1921, 1920) == (1920, 1920), "Square boundary resize failed.");
        Check(ExportResize.FitWithin(2001, 1333, 1280) == (1280, 853), "Rounding changed unexpectedly.");
        BitmapSource resized = ImageExporter.Resize(SampleBitmap(400, 200), 100);
        Check(resized.PixelWidth == 100 && resized.PixelHeight == 50, "Bitmap resize produced the wrong pixel size.");
        BitmapSource untouched = SampleBitmap(80, 60);
        Check(ReferenceEquals(ImageExporter.Resize(untouched, 1920), untouched), "A no-op resize copied the bitmap.");
        var clipboardOptions = ExportOptions.Default with { MaxDimension = 50 };
        Check(ReferenceEquals(ImageExporter.PrepareForClipboard(untouched, clipboardOptions), untouched), "Clipboard copies were resized without opting in.");
        Check(ImageExporter.PrepareForClipboard(untouched, clipboardOptions with { ResizeClipboard = true }).PixelWidth == 50, "Opted-in clipboard resize was ignored.");
    }

    private static void VerifyExportEncoding(string root)
    {
        Directory.CreateDirectory(root);
        BitmapSource image = SampleBitmap(300, 150);
        var jpeg = ExportOptions.Default with { SaveFolder = root, Format = CaptureImageFormat.Jpeg, JpegQuality = 70, MaxDimension = 1280, FileNamePattern = "%date shot" };
        string path = ImageExporter.SaveToFolder(image, jpeg, SampleTime, 1);
        Check(Path.GetFileName(path) == "2026-08-05 shot.jpg", "JPEG export used the wrong name or extension.");
        byte[] header = File.ReadAllBytes(path).Take(2).ToArray();
        Check(header.Length == 2 && header[0] == 0xFF && header[1] == 0xD8, "JPEG export is not a JPEG file.");
        BitmapFrame decoded = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Check(decoded.PixelWidth == 300 && decoded.PixelHeight == 150, "JPEG export changed dimensions.");
        var png = ExportOptions.Default with { SaveFolder = root, MaxDimension = 100, FileNamePattern = "small" };
        string pngPath = ImageExporter.SaveToFolder(image, png, SampleTime, 1);
        BitmapFrame pngFrame = BitmapDecoder.Create(new Uri(pngPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Check(pngFrame.PixelWidth == 100 && pngFrame.PixelHeight == 50 && pngPath.EndsWith(".png", StringComparison.Ordinal), "PNG export did not apply resize.");

        CaptureDocument document = EditorDiagnostics.CreateSampleDocument();
        var dragOptions = ExportOptions.Default with { Format = CaptureImageFormat.Jpeg, MaxDimension = 100 };
        DataObject data = CaptureDragData.CreateData(document, asFile: true, options: dragOptions);
        string dragged = DroppedPath(data);
        BitmapFrame dragFrame = BitmapDecoder.Create(new Uri(dragged), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Check(dragged.EndsWith(".jpg", StringComparison.Ordinal) && Math.Max(dragFrame.PixelWidth, dragFrame.PixelHeight) == 100, "Drag export ignored format or resize.");
        DataObject imageData = CaptureDragData.CreateData(document, asFile: false, options: dragOptions);
        Check(imageData.GetDataPresent("PNG") && imageData.GetDataPresent(DataFormats.Bitmap) && imageData.GetDataPresent(DataFormats.FileDrop), "Image drag lost its Slack-compatible formats.");
        string defaultDrag = DroppedPath(CaptureDragData.CreateData(document));
        Check(defaultDrag.EndsWith(".png", StringComparison.Ordinal), "Default drag export is no longer PNG.");
    }

    private static void VerifySettingsMigration(string root)
    {
        var fresh = new CaptureHistoryStore(Path.Combine(root, "fresh"));
        CaptureSettings defaults = fresh.Settings;
        Check(defaults.CopyToClipboard && defaults.ShowThumbnail && defaults.PlayCaptureSound && !defaults.OpenEditorAfterCapture && !defaults.AutoSave, "After-capture defaults changed.");
        Check(defaults.CaptureDelaySeconds == 0 && defaults.ThumbnailAutoDismissSeconds == 0 && defaults.ExportMaxDimension == 0 && defaults.ImageFormat == CaptureImageFormat.Png, "Capture defaults changed.");
        Check(defaults.FileNamePattern == FileNameTemplate.DefaultPattern && defaults.SaveFolder.Length == 0 && defaults.EffectiveSaveFolder.EndsWith("BetterShot", StringComparison.OrdinalIgnoreCase), "Saving defaults changed.");
        Check(AllHotkeysEmpty(defaults), "New shortcuts should start unassigned.");

        string legacyRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        string legacyPath = Path.Combine(legacyRoot, "settings.json");
        File.WriteAllText(legacyPath, "{\"RetentionDays\":30,\"ReplaceScreenshotShortcut\":true,\"StartWithWindows\":true,\"DragAsFile\":true,\"EditorTheme\":\"midnight\",\"FutureBlock\":{\"Nested\":[1,2]}}");
        var legacy = new CaptureHistoryStore(legacyRoot);
        Check(legacy.RetentionDays == 30 && legacy.ReplaceScreenshotShortcut && legacy.StartWithWindows && legacy.DragAsFile, "Legacy fields did not load.");
        Check(legacy.Settings with { RetentionDays = 7, ReplaceScreenshotShortcut = false, StartWithWindows = false, DragAsFile = false } == CaptureSettings.Default, "Missing new fields did not default.");

        var areaKey = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x41);
        legacy.UpdateSettings(settings => settings with { CaptureDelaySeconds = 5, ImageFormat = CaptureImageFormat.Jpeg, JpegQuality = 75, AreaHotkey = areaKey, FileNamePattern = "Shot %counter", FileNameCounter = 4 });
        JsonNode saved = JsonNode.Parse(File.ReadAllText(legacyPath))!;
        Check(saved["EditorTheme"]?.GetValue<string>() == "midnight" && saved["FutureBlock"]?["Nested"]?.AsArray().Count == 2, "Unknown settings fields were dropped on write.");
        Check(saved["RetentionDays"]!.GetValue<int>() == 30 && saved["ReplaceScreenshotShortcut"]!.GetValue<bool>() && saved["DragAsFile"]!.GetValue<bool>(), "A new-option write changed existing fields.");
        legacy.SetDragAsFile(false);
        var reloaded = new CaptureHistoryStore(legacyRoot);
        Check(reloaded.Settings.CaptureDelaySeconds == 5 && reloaded.Settings.ImageFormat == CaptureImageFormat.Jpeg && reloaded.Settings.JpegQuality == 75, "New options did not survive another setter.");
        Check(reloaded.Settings.AreaHotkey == areaKey && reloaded.Settings.FileNameCounter == 4 && reloaded.Settings.FileNamePattern == "Shot %counter", "Hotkey or counter did not round-trip.");
        Check(!reloaded.DragAsFile && reloaded.StartWithWindows, "Legacy setter did not preserve other fields.");

        reloaded.SetRetentionDays(CaptureSettings.NeverExpire);
        JsonNode forever = JsonNode.Parse(File.ReadAllText(legacyPath))!;
        Check(forever["KeepCapturesForever"]!.GetValue<bool>() && forever["RetentionDays"]!.GetValue<int>() == CaptureSettings.ForeverCompatibilityDays, "Keep-forever was not written in a backward-compatible way.");
        var foreverStore = new CaptureHistoryStore(legacyRoot);
        Check(foreverStore.KeepForever && foreverStore.RetentionDays == CaptureSettings.NeverExpire, "Keep-forever did not reload.");
        var ancient = new CaptureDocument(SampleBitmap(20, 20), createdUtc: DateTime.UtcNow.AddDays(-4000));
        foreverStore.Save(ancient);
        Check(foreverStore.PurgeExpired(DateTime.UtcNow).Count == 0 && foreverStore.Load(DateTime.UtcNow).Any(document => document.Id == ancient.Id), "Keep-forever still expired a capture.");
        foreverStore.SetRetentionDays(180);
        Check(new CaptureHistoryStore(legacyRoot).RetentionDays == 180 && foreverStore.PurgeExpired(DateTime.UtcNow).Contains(ancient.Id), "180-day retention did not apply after keep-forever.");

        string clampRoot = Path.Combine(root, "clamp");
        Directory.CreateDirectory(clampRoot);
        File.WriteAllText(Path.Combine(clampRoot, "settings.json"), "{\"RetentionDays\":7,\"CaptureDelaySeconds\":7,\"ThumbnailAutoDismissSeconds\":-3,\"ExportMaxDimension\":999,\"JpegQuality\":500,\"FileNameCounter\":-2,\"FileNamePattern\":\"  \",\"ImageFormat\":\"gif\",\"WindowHotkey\":{\"Modifiers\":0,\"Key\":65},\"LibraryHotkey\":{\"Modifiers\":4096,\"Key\":66}}");
        CaptureSettings clamped = new CaptureHistoryStore(clampRoot).Settings;
        Check(clamped.CaptureDelaySeconds == 0 && clamped.ThumbnailAutoDismissSeconds == 0 && clamped.ExportMaxDimension == 0, "Out-of-range choices were not reset.");
        Check(clamped.JpegQuality == 100 && clamped.FileNameCounter == 1 && clamped.FileNamePattern == FileNameTemplate.DefaultPattern && clamped.ImageFormat == CaptureImageFormat.Png, "Invalid values were not clamped.");
        Check(clamped.WindowHotkey.IsEmpty && clamped.LibraryHotkey.IsEmpty, "Unsafe or malformed saved hotkeys were accepted.");

        string lockedRoot = Path.Combine(root, "locked");
        var lockedStore = new CaptureHistoryStore(lockedRoot);
        lockedStore.UpdateSettings(settings => settings with { PlayCaptureSound = false });
        bool failed = false;
        using (new FileStream(Path.Combine(lockedRoot, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { lockedStore.UpdateSettings(settings => settings with { PlayCaptureSound = true, AutoSave = true }); }
            catch (IOException) { failed = true; }
            catch (UnauthorizedAccessException) { failed = true; }
        }
        Check(failed && !lockedStore.Settings.PlayCaptureSound && !lockedStore.Settings.AutoSave && !new CaptureHistoryStore(lockedRoot).Settings.AutoSave, "A failed settings write changed saved or in-memory state.");
    }

    private static bool AllHotkeysEmpty(CaptureSettings settings) =>
        Enum.GetValues<HotkeyAction>().All(action => settings.HotkeyFor(action).IsEmpty);

    private static void VerifyShutterSound()
    {
        byte[] wave = CaptureSound.CreateShutterWave();
        Check(System.Text.Encoding.ASCII.GetString(wave, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(wave, 8, 4) == "WAVE", "Shutter sound is not a WAV file.");
        Check(BitConverter.ToInt32(wave, 24) == CaptureSound.SampleRate && BitConverter.ToInt16(wave, 34) == 16, "Shutter sound format changed.");
        int dataLength = BitConverter.ToInt32(wave, 40);
        Check(dataLength == wave.Length - 44 && dataLength / 2.0 / CaptureSound.SampleRate is > 0.05 and < 0.3, "Shutter sound length is wrong.");
        int peak = 0;
        for (int offset = 44; offset < wave.Length; offset += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wave, offset)));
        Check(peak is > 3000 and < 30000, "Shutter sound is silent or clipping.");
    }

    private static async Task VerifyUiAsync(string root, string output)
    {
        string saveFolder = Path.Combine(root, "Saved");
        var store = new CaptureHistoryStore(root);
        store.UpdateSettings(settings => settings with { SaveFolder = saveFolder, PlayCaptureSound = false, FileNamePattern = "Capture %counter", FileNameCounter = 3 });
        var old = new CaptureDocument(SampleBitmap(30, 30), createdUtc: DateTime.UtcNow.AddDays(-200));
        store.Save(old);
        store.SetRetentionDays(CaptureSettings.NeverExpire);
        MainWindow? shell = null;
        try
        {
            shell = new MainWindow(enableHistory: true, historyRoot: root);
            Application.Current.MainWindow = shell;
            shell.Show(); await SettleAsync(shell);
            Check(shell.Presentation.Recent.Any(document => document.Id == old.Id), "Keep-forever library lost an old capture on startup.");
            Check(((ComboBoxItem)shell.RetentionComboBox.SelectedItem).Tag?.ToString() == "0", "Never retention was not selected in Settings.");
            Check(shell.SaveFolderText.Text == saveFolder && shell.FileNamePreviewText.Text.Contains("Capture 3.png", StringComparison.Ordinal), "Saving settings were not shown.");
            Check(shell.CopyToClipboardCheckBox.IsChecked == true && shell.ShowThumbnailCheckBox.IsChecked == true && shell.PlaySoundCheckBox.IsChecked == false, "Toggles do not reflect saved settings.");

            shell.ShowThumbnailCheckBox.IsChecked = false;
            Check(!new CaptureHistoryStore(root).Settings.ShowThumbnail && !shell.AutoSaveCheckBox.IsEnabled, "Turning thumbnails off did not save or lock auto-save on.");
            int clipboardWrites = 0;
            CaptureThumbnailWindow? none = await shell.CompleteCaptureAsync(SampleBitmap(120, 80), _ => clipboardWrites++);
            Check(none is null && clipboardWrites == 1 && shell.Presentation.Thumbnails.Count == 0, "Thumbnail-off capture still showed a thumbnail or skipped the clipboard.");
            Check(File.Exists(Path.Combine(saveFolder, "Capture 3.png")) && new CaptureHistoryStore(root).Settings.FileNameCounter == 4, "Thumbnail-off capture was not saved with the counter.");
            Check(shell.Presentation.Recent.Count == 2, "Thumbnail-off capture was not added to the library.");

            shell.ShowThumbnailCheckBox.IsChecked = true;
            shell.CopyToClipboardCheckBox.IsChecked = false;
            clipboardWrites = 0;
            CaptureThumbnailWindow? thumbnail = await shell.CompleteCaptureAsync(SampleBitmap(120, 80), _ => clipboardWrites++);
            Check(thumbnail is not null && clipboardWrites == 0 && Directory.GetFiles(saveFolder).Length == 1, "Copy-off capture copied, or auto-save ran while off.");

            shell.AutoDismissComboBox.SelectedItem = shell.AutoDismissComboBox.Items.Cast<ComboBoxItem>().Single(item => item.Tag?.ToString() == "10");
            await SettleAsync(shell);
            Check(new CaptureHistoryStore(root).Settings.ThumbnailAutoDismissSeconds == 10 && thumbnail!.AutoDismissActiveForTest, "Auto-dismiss was not saved or applied to open thumbnails.");
            thumbnail!.AdvanceAutoDismissForTest(TimeSpan.FromSeconds(4));
            Check(shell.Presentation.Thumbnails.Contains(thumbnail), "Auto-dismiss fired early.");
            thumbnail.AdvanceAutoDismissForTest(TimeSpan.FromSeconds(7));
            await SettleAsync(shell);
            Check(!shell.Presentation.Thumbnails.Contains(thumbnail) && shell.Presentation.Recent.Count == 3, "Auto-dismiss did not close the thumbnail or removed the capture from the library.");

            var conflictKey = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x86);
            Check(shell.ApplyRecordedHotkey(HotkeyAction.Window, HotkeyBinding.AreaFallback) is not null, "The Ctrl+Shift+F9 fallback could be reassigned.");
            Check(shell.ApplyRecordedHotkey(HotkeyAction.Window, new HotkeyBinding(HotkeyModifiers.None, 0x41)) is not null, "A bare letter was accepted as a global shortcut.");
            string? registration = shell.ApplyRecordedHotkey(HotkeyAction.Fullscreen, conflictKey);
            if (registration is null)
            {
                Check(new CaptureHistoryStore(root).Settings.FullscreenHotkey == conflictKey && shell.FullscreenHotkeyRecorder.Binding == conflictKey, "Registered shortcut was not saved or shown.");
                Check(shell.ApplyRecordedHotkey(HotkeyAction.Library, conflictKey) is not null, "Duplicate shortcuts were accepted.");
                Check(shell.ApplyRecordedHotkey(HotkeyAction.Fullscreen, HotkeyBinding.None) is null && new CaptureHistoryStore(root).Settings.FullscreenHotkey.IsEmpty, "Clearing a shortcut failed.");
            }
            else Check(shell.HotkeyProblemText.IsVisible && new CaptureHistoryStore(root).Settings.FullscreenHotkey.IsEmpty, "A refused registration was saved or not reported.");

            shell.OpenSettingsForTest(); await SettleAsync(shell);
            shell.FormatComboBox.SelectedIndex = 1; await SettleAsync(shell);
            Check(shell.JpegQualityRow.IsVisible && new CaptureHistoryStore(root).Settings.ImageFormat == CaptureImageFormat.Jpeg, "JPEG choice did not save or reveal quality.");
            shell.StatusText.Text = string.Empty;
            Render(shell, Path.Combine(output, "capture-settings-panel.png"));
            if (((ScrollViewer)shell.SettingsPanel.Child).Content is FrameworkElement settingsContent) Render(settingsContent, Path.Combine(output, "capture-settings-full.png"));
            shell.FormatComboBox.SelectedIndex = 0;

            var countdown = new CaptureCountdownWindow();
            try
            {
                countdown.SetSeconds(3); countdown.Show(); await SettleAsync(countdown);
                Render(countdown, Path.Combine(output, "capture-countdown.png"));
            }
            finally { countdown.Close(); }
        }
        finally { shell?.CloseForTest(); }
    }

    private static BitmapSource SampleBitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            int pixel = index / 4;
            pixels[index] = (byte)(pixel % width * 255 / Math.Max(1, width - 1));
            pixels[index + 1] = (byte)(pixel / width * 255 / Math.Max(1, height - 1));
            pixels[index + 2] = 140;
            pixels[index + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static string DroppedPath(DataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths ? paths[0] : throw new InvalidOperationException("Drag data has no file.");

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

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
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
