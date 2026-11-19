using BetterShot.Capture;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using BetterShot.Editing;

namespace BetterShot;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\BetterShot.SingleInstance";
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? testOutput = GetArgumentValue(e.Args, "--test-output");
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            bool passed;
            string result;
            try
            {
                passed = Platform.ShortcutClassifier.RunSelfTests();
                Capture.ScreenGeometry.RunSelfTest();
                EditorDiagnostics.RunSelfTest();
                result = passed ? "PASS: shortcut classification, suppression, screen geometry, document editing, rendering, and PNG file-drop snapshots" : "FAIL: shortcut classification or suppression";
            }
            catch (Exception ex)
            {
                passed = false;
                result = $"FAIL: self-test: {ex.Message}";
            }
            WriteTestResult(testOutput, result);
            Shutdown(passed ? 0 : 1);
            return;
        }

        if (e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            RunSmokeTest(testOutput);
            return;
        }

        if (e.Args.Contains("--overlay-test", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunOverlayTestAsync(testOutput);
            return;
        }

        if (e.Args.Contains("--ui-test", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunUiTestAsync(testOutput);
            return;
        }

        if (e.Args.Contains("--workflow-test", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunWorkflowTestAsync(testOutput);
            return;
        }
        if (e.Args.Contains("--shell-test", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunShellTestAsync(testOutput);
            return;
        }

        if (e.Args.Contains("--history-test", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunHistoryTestAsync(testOutput);
            return;
        }

        if (e.Args.Contains("--startup-test", StringComparer.OrdinalIgnoreCase))
        {
            try { StartupDiagnostics.Run(testOutput); Shutdown(0); }
            catch (Exception ex) { WriteTestResult(testOutput, $"FAIL: startup: {ex}", "startup-test.txt"); Shutdown(1); }
            return;
        }

        if (e.Args.Contains("--sharing-test", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunSharingTestAsync(testOutput);
            return;
        }

        bool background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        _mutex = new Mutex(true, MutexName, out bool ownsMutex);
        if (!ownsMutex)
        {
            if (!background) System.Windows.MessageBox.Show("BetterShot is already running. Look for it in the notification area.", "BetterShot",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        var window = new MainWindow(enableHistory: true);
        MainWindow = window;
        if (background) window.InitializeInTray(); else window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private async Task RunSharingTestAsync(string? output)
    {
        try { await CaptureSharingDiagnostics.RunAsync(output); Shutdown(0); }
        catch (Exception ex) { WriteTestResult(output, $"FAIL: capture sharing: {ex}", "sharing-test.txt"); Shutdown(1); }
    }

    private void RunSmokeTest(string? output)
    {
        try
        {
            BitmapSource bitmap = CaptureService.CaptureDesktopForSmokeTest();
            string path = string.IsNullOrWhiteSpace(output)
                ? Path.Combine(AppContext.BaseDirectory, "smoke-test.png")
                : Directory.Exists(output) || !Path.HasExtension(output)
                    ? Path.Combine(output, "smoke-test.png")
                    : output;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            WriteTestResult(output, $"FAIL: smoke capture: {ex.Message}");
            Shutdown(1);
        }
    }

    private async Task RunOverlayTestAsync(string? output)
    {
        try
        {
            await CaptureService.RunOverlaySelfTestAsync();
            WriteTestResult(output, "PASS: capture overlay startup, cancellation, and cleanup");
            Shutdown(0);
        }
        catch (Exception ex)
        {
            WriteTestResult(output, $"FAIL: overlay test: {ex.Message}");
            Shutdown(1);
        }
    }

    private async Task RunUiTestAsync(string? output)
    {
        MainWindow? window = null;
        try
        {
            window = new MainWindow();
            MainWindow = window;
            window.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            double scaleX = 1;
            double scaleY = 1;
            if (PresentationSource.FromVisual(window)?.CompositionTarget is { } target)
            {
                scaleX = target.TransformToDevice.M11;
                scaleY = target.TransformToDevice.M22;
            }
            int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * scaleX));
            int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * scaleY));
            var bitmap = new RenderTargetBitmap(width, height, 96 * scaleX, 96 * scaleY, PixelFormats.Pbgra32);
            bitmap.Render(window);

            string path = ResolveTestPath(output, "main-window.png");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            window.CloseForTest();
            window = null;
            Shutdown(0);
        }
        catch (Exception ex)
        {
            window?.CloseForTest();
            WriteTestResult(output, $"FAIL: UI render test: {ex.Message}");
            Shutdown(1);
        }
    }

    private async Task RunWorkflowTestAsync(string? output)
    {
        MainWindow? shell = null;
        try
        {
            string directory = string.IsNullOrWhiteSpace(output) ? AppContext.BaseDirectory : Path.HasExtension(output) ? Path.GetDirectoryName(Path.GetFullPath(output))! : output;
            Directory.CreateDirectory(directory);
            shell = new MainWindow(); MainWindow = shell; shell.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            shell.Hide();
            CaptureDocument document = EditorDiagnostics.CreateSampleDocument();
            var thumbnail = shell.PresentDocument(document);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (shell.IsVisible) throw new InvalidOperationException("The shell reopened after capture.");
            if (!thumbnail.IsVisible || shell.Presentation.OpenEditorCount != 0) throw new InvalidOperationException("Thumbnail presentation auto-opened the editor or failed to remain visible.");
            if (shell.Presentation.Recent.Count != 1 || !ReferenceEquals(shell.Presentation.Recent[0], document)) throw new InvalidOperationException("The captured document was not retained in Recent.");
            RenderWindow(thumbnail, Path.Combine(directory, "workflow-thumbnail.png"));
            if (!thumbnail.Topmost) throw new InvalidOperationException("Capture thumbnails must stay on top.");
            thumbnail.ShowActionsForTest(true);
            await Task.Delay(160);
            thumbnail.UpdateLayout();
            if (!thumbnail.ActionsFitForTest() || !shell.Presentation.IsInsideWorkingArea(thumbnail)) throw new InvalidOperationException("Thumbnail actions or native bounds do not fit the monitor working area.");
            RenderWindow(thumbnail, Path.Combine(directory, "workflow-thumbnail-hover.png"));
            SaveBitmap(CaptureService.RenderLoupePreviewForTest(), Path.Combine(directory, "workflow-loupe.png"));
            SaveBitmap(CaptureService.RenderSelectionPreviewForTest(true), Path.Combine(directory, "workflow-window-hover.png"));
            SaveBitmap(CaptureService.RenderSelectionPreviewForTest(false), Path.Combine(directory, "workflow-area-drag.png"));
            var preview = new Presentation.DragPreviewWindow(document.Render());
            IntPtr previewHandle = IntPtr.Zero;
            try
            {
                preview.Show(); preview.UpdatePosition();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Presentation.DragPreviewDiagnostics.VerifyVisible(preview);
                previewHandle = new System.Windows.Interop.WindowInteropHelper(preview).Handle;
                if (Presentation.DragPreviewWindow.CalculatePreviewPosition(-200, -100) != new Point(-184, -84))
                    throw new InvalidOperationException("Drag preview lost negative monitor coordinates.");
                RenderWindow(preview, Path.Combine(directory, "workflow-drag-preview.png"));
            }
            finally { preview.Close(); }
            Presentation.DragPreviewDiagnostics.VerifyClosed(previewHandle);


            CaptureDocument newerDocument = EditorDiagnostics.CreateSampleDocument();
            var newerThumbnail = shell.PresentDocument(newerDocument);
            newerThumbnail.CloseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (newerThumbnail.IsVisible) throw new InvalidOperationException("The thumbnail close button did not dismiss its card.");
            if (shell.Presentation.Recent.Count != 2 || !ReferenceEquals(shell.Presentation.Recent[1], document)) throw new InvalidOperationException("Dismissing a newer thumbnail lost an older capture.");

            shell.Presentation.OpenEditor(document);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            EditorWindow editor = shell.Presentation.EditorFor(document) ?? throw new InvalidOperationException("Thumbnail did not open the editor.");
            RenderWindow(editor, Path.Combine(directory, "workflow-editor.png"));
            foreach (double testWidth in new[] { 1180d, 780d })
            {
                editor.Width = testWidth;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                editor.UpdateLayout();
                var root = (System.Windows.Controls.Grid)editor.Content;
                var toolbar = (System.Windows.Controls.Border)root.Children[0];
                var bar = (System.Windows.Controls.Grid)toolbar.Child;
                Rect? previous = null;
                foreach (FrameworkElement group in bar.Children)
                {
                    Point origin = group.TranslatePoint(new Point(), bar);
                    var bounds = new Rect(origin, group.RenderSize);
                    if (bounds.Left < -0.5 || bounds.Right > bar.ActualWidth + 0.5 ||
                        bounds.Top < -0.5 || bounds.Bottom > bar.ActualHeight + 0.5 ||
                        (previous is Rect prior && prior.Right > bounds.Left + 0.5))
                        throw new InvalidOperationException($"Editor toolbar overlaps or clips at {testWidth} DIP.");
                    previous = bounds;
                }
                RenderWindow(editor, Path.Combine(directory, $"workflow-editor-{testWidth:0}.png"));
            }

            InlineTextDiagnostics.Verify(editor, document);
            editor.Width = 1180;
            editor.SetZoomForTest(0.85);
            editor.BeginTextForTest(new Point(60, 45));
            editor.SetTextForTest("Hello gyp\nEdit text right on the image");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            RenderWindow(editor, Path.Combine(directory, "workflow-inline-text.png"));
            editor.CommitTextForTest();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            RenderWindow(editor, Path.Combine(directory, "workflow-text-handles.png"));
            editor.Close();
            shell.Presentation.OpenEditor(document);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            EditorWindow reopened = shell.Presentation.EditorFor(document) ?? throw new InvalidOperationException("The shared document could not be reopened.");
            if (!ReferenceEquals(shell.Presentation.Recent[1], document)) throw new InvalidOperationException("Reopening replaced the shared document.");
            reopened.Close(); shell.Presentation.Dismiss(thumbnail); shell.CloseForTest(); shell = null;
            WriteTestResult(Path.Combine(directory, "workflow-test.txt"), "PASS: thumbnail-first capture, no auto-editor, multi-capture retention, dismiss behavior, shared-document reopen, thumbnail render, editor layout, inline text editing, text handle undo/cancel, pointer-anchored zoom, and Space-drag canvas navigation");
            Shutdown(0);
        }
        catch (Exception ex)
        {
            shell?.CloseForTest();
            WriteTestResult(output, $"FAIL: workflow test: {ex.Message}");
            Shutdown(1);
        }
    }

    private async Task RunHistoryTestAsync(string? output)
    {
        try { await HistoryDiagnostics.RunAsync(output); Shutdown(0); }
        catch (Exception ex) { WriteTestResult(output, $"FAIL: history test: {ex}", "history-test.txt"); Shutdown(1); }
    }
    private async Task RunShellTestAsync(string? output)
    {
        try { await ShellDiagnostics.RunAsync(output); Shutdown(0); }
        catch (Exception ex) { WriteTestResult(output, $"FAIL: shell test: {ex.Message}", "shell-test.txt"); Shutdown(1); }
    }
    private static void RenderWindow(Window window, string path)
    {
        window.UpdateLayout();
        DpiScale dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static string? GetArgumentValue(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    internal static void WriteTestResult(string? output, string result, string defaultName = "self-test.txt")
    {
        if (string.IsNullOrWhiteSpace(output)) return;
        string path = Directory.Exists(output) || !Path.HasExtension(output)
            ? Path.Combine(output, defaultName)
            : output;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, result + Environment.NewLine);
    }

    private static string ResolveTestPath(string? output, string defaultName) =>
        string.IsNullOrWhiteSpace(output)
            ? Path.Combine(AppContext.BaseDirectory, defaultName)
            : Directory.Exists(output) || !Path.HasExtension(output)
                ? Path.Combine(output, defaultName)
                : output;
}
