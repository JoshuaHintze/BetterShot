using BetterShot.Editing;
using BetterShot.Persistence;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterShot;

internal static class HistoryDiagnostics
{
    internal static async Task RunAsync(string? output)
    {
        string directory = string.IsNullOrWhiteSpace(output) ? AppContext.BaseDirectory : Path.HasExtension(output) ? Path.GetDirectoryName(Path.GetFullPath(output))! : output;
        Directory.CreateDirectory(directory);
        string runRoot = Path.Combine(directory, "history-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        VerifyStore(Path.Combine(runRoot, "store"));
        await VerifyUiAsync(Path.Combine(runRoot, "ui"), directory);
        App.WriteTestResult(Path.Combine(directory, "history-test.txt"), "PASS: editable history round-trip, retention settings, expiry boundary, atomic overwrite failure, corrupt-file isolation, immediate deletion, restart restoration, and saved shortcut on/off hook activation");
    }

    private static void VerifyStore(string root)
    {
        DateTime now = DateTime.UtcNow;
        var store = new CaptureHistoryStore(root);
        Check(store.RetentionDays == 7, "Default retention is not seven days.");
        Check(!store.DragAsFile, "Image drag was not the default.");
        store.SetDragAsFile(true);
        store.SetRetentionDays(14);
        Check(new CaptureHistoryStore(root).RetentionDays == 14, "Retention setting did not survive a new store instance.");
        store.SetReplaceScreenshotShortcut(true);
        Check(new CaptureHistoryStore(root).ReplaceScreenshotShortcut, "Shortcut preference did not persist.");
        Check(new CaptureHistoryStore(root).RetentionDays == 14, "Saving shortcut preference changed retention.");
        store.SetRetentionDays(7);
        Check(new CaptureHistoryStore(root).ReplaceScreenshotShortcut, "Saving retention reset shortcut preference.");
        store.SetReplaceScreenshotShortcut(false);
        Check(!new CaptureHistoryStore(root).ReplaceScreenshotShortcut, "Disabling shortcut preference did not persist.");
        bool settingWriteFailed = false;
        using (var lockedSettings = new FileStream(Path.Combine(root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { store.SetReplaceScreenshotShortcut(true); }
            catch (IOException) { settingWriteFailed = true; }
            catch (UnauthorizedAccessException) { settingWriteFailed = true; }
        }
        Check(settingWriteFailed && !store.ReplaceScreenshotShortcut && !new CaptureHistoryStore(root).ReplaceScreenshotShortcut, "Failed preference write changed saved or in-memory state.");
        string legacyRoot = Path.Combine(root, "legacy-settings");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "settings.json"), "{\"RetentionDays\":30}");
        var legacy = new CaptureHistoryStore(legacyRoot);
        Check(legacy.RetentionDays == 30 && !legacy.ReplaceScreenshotShortcut, "Existing settings did not migrate safely.");
        Check(new CaptureHistoryStore(root).DragAsFile, "Other preference changes reset the saved file-drag preference.");
        store.SetDragAsFile(false);
        Check(!new CaptureHistoryStore(root).DragAsFile && !legacy.DragAsFile, "Image preference or legacy default did not restore.");
        var sample = EditorDiagnostics.CreateSampleDocument();
        sample.Add(new PenAnnotation(new[] { new Point(25, 25), new Point(50, 90), new Point(120, 40) }, Colors.LimeGreen, 3));
        sample.Add(new TextAnnotation(new Point(14, 230), "Editable history\nTwo lines", Colors.White, 25));
        var kept = new CaptureDocument(sample.Original, createdUtc: now.AddDays(-6));
        kept.RestoreAnnotations(sample.Items);
        var expired = new CaptureDocument(sample.Original, createdUtc: now.AddDays(-8));
        var boundary = new CaptureDocument(sample.Original, createdUtc: now.AddDays(-7));
        store.Save(kept); store.Save(expired); store.Save(boundary);
        string manual = Path.Combine(root, "manual-export.png");
        File.WriteAllText(manual, "User-owned export sentinel");
        string foreign = Path.Combine(root, "notes.capture");
        File.WriteAllText(foreign, "Foreign file sentinel");
        var purged = store.PurgeExpired(now);
        Check(purged.Contains(expired.Id) && purged.Contains(boundary.Id), "Expiry did not honor the seven-day boundary.");
        var loaded = store.Load(now).Single();
        Check(loaded.Id == kept.Id && loaded.CreatedUtc == kept.CreatedUtc, "History changed identity or creation time.");
        Check(loaded.Items.Count == kept.Items.Count && PixelHash(loaded.Render()) == PixelHash(kept.Render()), "Annotations or rendered pixels changed after history round-trip.");
        loaded.Add(new RectangleAnnotation(new Rect(10, 10, 45, 28), Colors.Black, 1, true));
        store.Save(loaded);
        Check(store.Load(now).Single().CreatedUtc == kept.CreatedUtc, "Editing restarted the retention clock.");
        string archive = Path.Combine(root, kept.Id.ToString("N") + ".capture");
        string originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
        bool overwriteFailed = false;
        using (var locked = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { store.Save(loaded); }
            catch (IOException) { overwriteFailed = true; }
            catch (UnauthorizedAccessException) { overwriteFailed = true; }
        }
        Check(overwriteFailed, "Locked-history overwrite unexpectedly succeeded.");
        Check(originalHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))), "A failed overwrite damaged the stored capture.");
        File.WriteAllText(Path.Combine(root, Guid.NewGuid().ToString("N") + ".capture"), "corrupt history fixture");
        Check(store.Load(now).Count == 1 && store.LastLoadErrors.Count > 0, "A corrupt capture was not isolated and reported.");
        var lockedExpired = new CaptureDocument(sample.Original, createdUtc: now.AddDays(-9));
        store.Save(lockedExpired);
        string lockedPath = Path.Combine(root, lockedExpired.Id.ToString("N") + ".capture");
        using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Check(!store.Load(now).Any(d => d.Id == lockedExpired.Id) && store.LastLoadErrors.Count > 0, "Locked expired history was restored or failed silently.");
        }
        Check(store.PurgeExpired(now).Contains(lockedExpired.Id), "Expiry did not retry after the file was unlocked.");
        var malformed = new CaptureDocument(sample.Original);
        malformed.RestoreAnnotations(sample.Items);
        store.Save(malformed);
        string malformedPath = Path.Combine(root, malformed.Id.ToString("N") + ".capture");
        using (var zip = System.IO.Compression.ZipFile.Open(malformedPath, System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("document.json")!;
            string json;
            using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            foreach (var annotation in node["Annotations"]!.AsArray()) annotation!.AsObject().Remove("Redaction");
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("document.json").Open());
            writer.Write(node.ToJsonString());
        }
        Check(!store.Load(now).Any(d => d.Id == malformed.Id), "A damaged annotation package loaded without its required redaction metadata.");
        store.Delete(kept.Id);
        Check(!File.Exists(archive) && File.Exists(manual) && File.Exists(foreign), "Delete removed unrelated files or left the target archive.");
    }

    private static string PixelHash(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task SettleAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    private static async Task VerifyUiAsync(string root, string output)
    {
        var store = new CaptureHistoryStore(root);
        var source = ShellPreviewSamples.Board();
        var old = new CaptureDocument(source.Original, createdUtc: DateTime.UtcNow.AddDays(-2));
        store.Save(old);
        MainWindow? shell = null;
        try
        {
            shell = new MainWindow(enableHistory: true, historyRoot: root);
            Application.Current.MainWindow = shell;
            shell.Show(); await SettleAsync(shell);
            Check(shell.Presentation.Recent.Count == 1 && shell.Presentation.Thumbnails.Count == 0, "Restored history did not load quietly into Recent.");
            var restored = shell.Presentation.Recent.Single();
            Check(restored.Id == old.Id, "Restart changed the capture identity.");
            var fresh = EditorDiagnostics.CreateSampleDocument();
            var thumbnail = shell.PresentDocument(fresh);
            await SettleAsync(shell);
            string freshPath = Path.Combine(root, fresh.Id.ToString("N") + ".capture");
            Check(File.Exists(freshPath), "A new capture was not persisted.");
            thumbnail.ShowActionsForTest(true);
            await Task.Delay(170); await SettleAsync(shell);
            Render(thumbnail, Path.Combine(output, "history-thumbnail-trash.png"));
            Check(thumbnail.ActionsFitForTest(), "The thumbnail trash action does not fit.");
            using (var locked = new FileStream(freshPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                thumbnail.DeleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await SettleAsync(shell);
                Check(shell.Presentation.Recent.Any(d => d.Id == fresh.Id) && File.Exists(freshPath), "Failed disk deletion silently removed the visible capture.");
            }
            shell.Presentation.OpenEditor(fresh); await SettleAsync(shell);
            var deletingEditor = shell.Presentation.EditorFor(fresh)!;
            deletingEditor.BeginTextForTest(new Point(35, 80));
            deletingEditor.SetTextForTest("Pending text must not resurrect a deleted capture");
            fresh.Add(new RectangleAnnotation(new Rect(35, 20, 110, 25), Colors.Red, 3));
            thumbnail.DeleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync(shell);
            Check(!File.Exists(freshPath) && shell.Presentation.Recent.All(d => d.Id != fresh.Id), "Trash did not delete the archive and Recent entry.");
            Check(shell.Presentation.EditorFor(fresh) is null && !thumbnail.IsVisible, "Trash left an editor or floating thumbnail open.");
            Check(new CaptureHistoryStore(root).Load(DateTime.UtcNow).All(d => d.Id != fresh.Id), "A queued edit resurrected the deleted capture.");

            shell.Presentation.OpenEditor(restored); await SettleAsync(shell);
            var editing = shell.Presentation.EditorFor(restored)!;
            editing.BeginTextForTest(new Point(45, 45));
            editing.SetTextForTest("Retained across restart");
            shell.CloseForTest(); shell = null;
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var persisted = new CaptureHistoryStore(root).Load(DateTime.UtcNow).Single();
            Check(persisted.Items.OfType<TextAnnotation>().Any(a => a.Text == "Retained across restart"), "Closing the app lost pending text from stored history.");

            shell = new MainWindow(enableHistory: true, historyRoot: root);
            Application.Current.MainWindow = shell;
            shell.Show(); await SettleAsync(shell);
            shell.OpenSettingsForTest(); await SettleAsync(shell);
            shell.StatusText.Text = string.Empty;
            Render(shell, Path.Combine(output, "history-settings.png"));
            shell.RetentionComboBox.IsDropDownOpen = true;
            await SettleAsync(shell);
            var popup = (System.Windows.Controls.Primitives.Popup)shell.RetentionComboBox.Template.FindName("PART_Popup", shell.RetentionComboBox);
            if (popup?.Child is FrameworkElement popupChild) RenderElement(popupChild, Path.Combine(output, "history-retention-options.png"));
            shell.RetentionComboBox.IsDropDownOpen = false;
            var oneDay = shell.RetentionComboBox.Items.OfType<ComboBoxItem>().Single(i => Convert.ToInt32(i.Tag) == 1);
            shell.RetentionComboBox.SelectedItem = oneDay;
            await SettleAsync(shell);
            Check(new CaptureHistoryStore(root).RetentionDays == 1, "Settings did not persist the selected retention period.");
            Check(shell.Presentation.Recent.Count == 0 && shell.EmptyState.IsVisible, "Shortening retention did not remove the expired capture and reset the library.");
            Check(new CaptureHistoryStore(root).Load(DateTime.UtcNow).Count == 0, "Expired capture remained on disk after changing retention.");
            shell.ReplaceShortcutCheckBox.IsChecked = true;
            Check(shell.ShortcutEnabledForTest && new CaptureHistoryStore(root).ReplaceScreenshotShortcut, "Enabling shortcut did not activate and save it.");
            shell.CloseForTest(); shell = null;
            shell = new MainWindow(enableHistory: true, historyRoot: root);
            Application.Current.MainWindow = shell;
            shell.Show(); await SettleAsync(shell);
            Check(shell.ReplaceShortcutCheckBox.IsChecked == true && shell.ShortcutEnabledForTest, "Restart did not restore the enabled shortcut hook.");
            Check(shell.CaptureShortcutText.Text == "Win + Shift + S" && shell.AreaButton.ToolTip.ToString()!.Contains("Win + Shift + S"), "Restored shortcut was not reflected on the main screen.");
            Render(shell, Path.Combine(output, "shortcut-main-enabled.png"));
            shell.OpenSettingsForTest(); await SettleAsync(shell);
            shell.StatusText.Text = string.Empty;
            Render(shell, Path.Combine(output, "shortcut-restored-settings.png"));
            shell.ReplaceShortcutCheckBox.IsChecked = false;
            Check(!shell.ShortcutEnabledForTest && !new CaptureHistoryStore(root).ReplaceScreenshotShortcut, "Disabling shortcut did not release and save it.");
            Check(shell.CaptureShortcutText.Text == "Ctrl + Shift + F9" && shell.AreaButton.ToolTip.ToString()!.Contains("Ctrl + Shift + F9"), "Disabling override did not immediately restore the fallback hint.");
            shell.CloseForTest(); shell = null;
            shell = new MainWindow(enableHistory: true, historyRoot: root);
            Application.Current.MainWindow = shell;
            shell.Show(); await SettleAsync(shell);
            Check(shell.ReplaceShortcutCheckBox.IsChecked != true && !shell.ShortcutEnabledForTest, "Restart reenabled a disabled shortcut.");
            shell.DragModeComboBox.SelectedIndex = 1;
            Check(shell.Presentation.DragAsFile && new CaptureHistoryStore(root).DragAsFile, "Drag mode selection did not apply and persist.");
            shell.CloseForTest(); shell = null;
            shell = new MainWindow(enableHistory: true, historyRoot: root);
            Application.Current.MainWindow = shell;
            shell.Show(); await SettleAsync(shell);
            Check(shell.Presentation.DragAsFile && shell.DragModeComboBox.SelectedIndex == 1, "File drag mode did not restore after restart.");
            shell.DragModeComboBox.SelectedIndex = 0;
            Check(!shell.Presentation.DragAsFile && !new CaptureHistoryStore(root).DragAsFile, "Image mode was not restored by Settings.");
        }
        finally { shell?.CloseForTest(); }
    }

    private static void Render(Window window, string path) => RenderElement(window, path);
    private static void RenderElement(FrameworkElement window, string path)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}