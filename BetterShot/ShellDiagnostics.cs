using BetterShot.Editing;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterShot;

internal static class ShellDiagnostics
{
    public static async Task RunAsync(string? output)
    {
        string directory = ResolveDirectory(output);
        Directory.CreateDirectory(directory);
        MainWindow? shell = null;
        try
        {
            shell = new MainWindow { Width = 1179, Height = 860, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            Application.Current.MainWindow = shell;
            shell.Show();
            await SettleAsync(shell);
            shell.StatusText.Text = string.Empty; // A separate running instance may own the capture hotkey during this visual diagnostic.
            VerifyVisibleControlsFit(shell, "empty shell");
            Render(shell, Path.Combine(directory, "shell-empty.png"));

            CaptureDocument older = ShellPreviewSamples.Board();
            CaptureDocument selected = ShellPreviewSamples.Board();
            shell.PresentDocumentForTest(older);
            await SettleAsync(shell);
            Render(shell, Path.Combine(directory, "shell-single-user-size.png"));
            shell.PresentDocumentForTest(ShellPreviewSamples.Code());
            shell.PresentDocumentForTest(ShellPreviewSamples.Document());
            shell.PresentDocumentForTest(selected);
            await SettleAsync(shell);
            if (!ReferenceEquals(shell.SelectedDocumentForTest, selected)) throw new InvalidOperationException("The newest recent capture was not selected.");
            if (!shell.SelectedTimeText.Text.StartsWith("Today, ")) throw new InvalidOperationException("The capture timestamp is not readable.");
            VerifyVisibleControlsFit(shell, "populated shell at 1179 x 860");
            Render(shell, Path.Combine(directory, "shell-populated-user-size.png"));

            shell.RecentList.SelectedIndex = 3;
            await SettleAsync(shell);
            if (!ReferenceEquals(shell.SelectedDocumentForTest, older)) throw new InvalidOperationException("Selecting an older card did not select its document.");
            shell.RecentList.SelectedIndex = 0;
            await SettleAsync(shell);
            BitmapSource? previewBeforeEdit = shell.PreviewSourceForTest;
            selected.Add(new RectangleAnnotation(new Rect(24, 24, 90, 54), Colors.DeepSkyBlue, 5));
            await SettleAsync(shell);
            if (ReferenceEquals(previewBeforeEdit, shell.PreviewSourceForTest)) throw new InvalidOperationException("The selected recent capture preview did not refresh after an edit.");
            if (!ReferenceEquals(shell.SelectedDocumentForTest, selected)) throw new InvalidOperationException("Editing a recent capture changed the shell selection.");

            shell.Width = shell.MinWidth;
            shell.Height = shell.MinHeight;
            await SettleAsync(shell);
            VerifyVisibleControlsFit(shell, $"populated shell at minimum size ({shell.MinWidth:0} x {shell.MinHeight:0})");
            Render(shell, Path.Combine(directory, "shell-populated-min.png"));

            shell.Width = 1100;
            shell.Height = 900;
            await SettleAsync(shell);
            VerifyVisibleControlsFit(shell, "default size");
            Render(shell, Path.Combine(directory, "shell-populated-default.png"));
            shell.RecentList.SelectedIndex = 1;
            await SettleAsync(shell);
            Render(shell, Path.Combine(directory, "shell-portrait.png"));
            shell.PresentDocumentForTest(ShellPreviewSamples.Wide());
            await SettleAsync(shell);
            Render(shell, Path.Combine(directory, "shell-wide.png"));
            shell.Width = 1440;
            shell.Height = 1000;
            await SettleAsync(shell);
            VerifyVisibleControlsFit(shell, "large size");
            Render(shell, Path.Combine(directory, "shell-large.png"));
            shell.Width = 1100;
            shell.Height = 900;
            shell.OpenSettingsForTest();
            await SettleAsync(shell);
            if (!shell.SettingsVisibleForTest) throw new InvalidOperationException("The Settings panel did not open.");
            VerifyVisibleControlsFit(shell, "Settings panel");
            Render(shell, Path.Combine(directory, "shell-settings.png"));

            shell.Width = shell.MinWidth;
            shell.Height = shell.MinHeight;
            await SettleAsync(shell);
            VerifyVisibleControlsFit(shell, "Settings at minimum size");
            Render(shell, Path.Combine(directory, "shell-settings-min.png"));

            shell.CloseToTrayForTest();
            await SettleAsync(shell);
            if (shell.IsVisible) throw new InvalidOperationException("Normal Close did not hide the shell.");
            if (!shell.TrayVisibleForTest) throw new InvalidOperationException("The notification-area icon was removed by normal Close.");
            shell.ShowAndActivateForTest();
            await SettleAsync(shell);
            if (!shell.IsVisible || !shell.IsLoaded) throw new InvalidOperationException("The hidden shell could not be reopened from the notification area.");

            shell.CloseForTest();
            shell = null;
            App.WriteTestResult(Path.Combine(directory, "shell-test.txt"), "PASS: empty and populated shell rendering, minimum-size layout, Settings panel, selected-preview edit refresh, and close-to-tray reopen behavior");
        }
        finally { shell?.CloseForTest(); }
    }

    private static async Task SettleAsync(Window shell)
    {
        await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        shell.UpdateLayout();
        await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    private static void VerifyVisibleControlsFit(Window shell, string state)
    {
        if (shell.Content is not Visual root) throw new InvalidOperationException($"The {state} has no visual root.");
        var client = new Rect(new Point(), shell.RenderSize);
        foreach (FrameworkElement element in Descendants(root).OfType<FrameworkElement>())
        {
            if (string.IsNullOrEmpty(element.Name) || !ReferenceEquals(shell.FindName(element.Name), element) || !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
            GeneralTransform transform;
            try { transform = element.TransformToAncestor(shell); }
            catch (InvalidOperationException) { continue; }
            Rect bounds = transform.TransformBounds(new Rect(element.RenderSize));
            if (bounds.Left < client.Left - 1 || bounds.Top < client.Top - 1 || bounds.Right > client.Right + 1 || bounds.Bottom > client.Bottom + 1)
                throw new InvalidOperationException($"{element.GetType().Name} is clipped in the {state}.");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Render(Window window, string path)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static string ResolveDirectory(string? output) => string.IsNullOrWhiteSpace(output) ? AppContext.BaseDirectory : Path.HasExtension(output) ? Path.GetDirectoryName(Path.GetFullPath(output))! : output;
}