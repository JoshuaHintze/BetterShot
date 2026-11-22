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
        await VerifyLibraryPreferenceAsync(Path.Combine(directory, "library-preference-fixture"));
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
            if (!shell.GroupNamesForTest.SequenceEqual(["Today"])) throw new InvalidOperationException("Today's captures were not grouped under Today.");
            if (!shell.SaveLocationForTest.Contains(" › ") && !shell.SaveLocationForTest.Contains("to ")) throw new InvalidOperationException("The save location line is missing.");
            if (shell.EmbeddedEditorForTest is not null) throw new InvalidOperationException("Presenting a capture opened the editor.");
            VerifyVisibleControlsFit(shell, "populated library at 1179 x 860");
            Render(shell, Path.Combine(directory, "shell-populated-user-size.png"));
            shell.SetGridModeForTest(true);
            await SettleAsync(shell);
            VerifyVisibleControlsFit(shell, "library grid");
            Render(shell, Path.Combine(directory, "shell-library-grid.png"));
            shell.SetGridModeForTest(false);

            shell.SelectDocumentForTest(older);
            await SettleAsync(shell);
            if (!ReferenceEquals(shell.SelectedDocumentForTest, older)) throw new InvalidOperationException("Selecting an older row did not select its document.");
            shell.SelectDocumentForTest(selected);
            await SettleAsync(shell);
            BitmapSource? previewBeforeEdit = shell.PreviewSourceForTest;
            selected.Add(new RectangleAnnotation(new Rect(24, 24, 90, 54), Colors.DeepSkyBlue, 5));
            await SettleAsync(shell);
            if (ReferenceEquals(previewBeforeEdit, shell.PreviewSourceForTest)) throw new InvalidOperationException("The selected capture's thumbnail did not refresh after an edit.");
            if (!ReferenceEquals(shell.SelectedDocumentForTest, selected)) throw new InvalidOperationException("Editing a recent capture changed the library selection.");

            // Library + editor in one window (KhingShot layout).
            shell.Width = 1500;
            shell.Height = 900;
            shell.OpenInEditorForTest(selected);
            await SettleAsync(shell);
            Editing.EditorSurface editor = shell.EmbeddedEditorForTest ?? throw new InvalidOperationException("The capture did not open in the embedded editor.");
            if (shell.LibraryVisibleForTest) throw new InvalidOperationException("A first edit did not default to a collapsed library.");
            editor.SetInspectorForTest(true, docked: true);
            await SettleAsync(shell);
            editor.HandleShortcutForTest(System.Windows.Input.Key.R);
            editor.VerifyToolIconCentersForTest("Rectangle selected shell render");
            editor.VerifyChromeFitsForTest("default collapsed library at 1500 x 900");
            Render(shell, Path.Combine(directory, "shell-editor-rectangle-selected.png"));
            shell.ToggleLibraryForTest();
            await SettleAsync(shell);
            if (!shell.LibraryVisibleForTest) throw new InvalidOperationException("The library did not open beside the editor at 1500 DIP.");
            await SettleAsync(shell);
            VerifyVisibleControlsFit(shell, "library and editor at 1500 x 900");
            editor.VerifyChromeFitsForTest("library and editor at 1500 x 900");
            Render(shell, Path.Combine(directory, "shell-editor-1500x900.png"));
            editor.SetInspectorForTest(true, docked: false);
            await SettleAsync(shell);
            editor.VerifyChromeFitsForTest("floating inspector at 1500 x 900");
            Render(shell, Path.Combine(directory, "shell-editor-floating-inspector.png"));
            shell.SelectDocumentForTest(older);
            await SettleAsync(shell);
            if (!ReferenceEquals(shell.EmbeddedEditorForTest?.Document, older)) throw new InvalidOperationException("Picking another row did not switch the editor.");
            shell.Width = 1100;
            await SettleAsync(shell);
            if (shell.LibraryVisibleForTest) throw new InvalidOperationException("The library did not auto-hide beside the editor at 1100 DIP.");
            shell.ToggleLibraryForTest();
            await SettleAsync(shell);
            if (!shell.LibraryVisibleForTest) throw new InvalidOperationException("The sidebar toggle did not restore the library manually at 1100 DIP.");
            VerifyVisibleControlsFit(shell, "manually restored library and editor at 1100 x 900");
            editor = shell.EmbeddedEditorForTest!;
            editor.VerifyChromeFitsForTest("manually restored library and editor at 1100 x 900");
            shell.ToggleLibraryForTest();
            await SettleAsync(shell);
            if (shell.LibraryVisibleForTest) throw new InvalidOperationException("The sidebar toggle did not hide the manually restored library.");
            editor = shell.EmbeddedEditorForTest!;
            editor.VerifyChromeFitsForTest("editor without library at 1100 x 900");
            Render(shell, Path.Combine(directory, "shell-editor-no-library.png"));
            shell.Width = 1800;
            await SettleAsync(shell);
            editor.VerifyChromeFitsForTest("wide editor without library");
            if (!editor.ToolbarInHeaderForTest || !editor.ToolbarExtrasVisibleForTest) throw new InvalidOperationException("The wide editor did not move the tool pill (with quick colors) into the header.");
            Render(shell, Path.Combine(directory, "shell-editor-wide.png"));
            shell.ToggleLibraryForTest();
            shell.Width = shell.MinWidth;
            shell.Height = shell.MinHeight;
            await SettleAsync(shell);
            if (shell.LibraryVisibleForTest) throw new InvalidOperationException("The library did not auto-hide beside the editor at the minimum size.");
            VerifyVisibleControlsFit(shell, $"editor at minimum size ({shell.MinWidth:0} x {shell.MinHeight:0})");
            editor.VerifyChromeFitsForTest("editor at minimum size");
            Render(shell, Path.Combine(directory, "shell-editor-min.png"));
            editor.SetInspectorForTest(true, docked: true);
            shell.CloseEditorForTest();
            await SettleAsync(shell);
            if (shell.EmbeddedEditorForTest is not null || !shell.LibraryVisibleForTest) throw new InvalidOperationException("Closing the editor did not return to the library.");

            VerifyVisibleControlsFit(shell, $"library at minimum size ({shell.MinWidth:0} x {shell.MinHeight:0})");
            Render(shell, Path.Combine(directory, "shell-populated-min.png"));

            shell.ShowVideosForTest(true);
            await SettleAsync(shell);
            VerifyVisibleControlsFit(shell, "Videos tab");
            Render(shell, Path.Combine(directory, "shell-videos.png"));
            shell.ShowVideosForTest(false);

            shell.Width = 1100;
            shell.Height = 900;
            await SettleAsync(shell);
            VerifyVisibleControlsFit(shell, "default size");
            Render(shell, Path.Combine(directory, "shell-populated-default.png"));
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
            App.WriteTestResult(Path.Combine(directory, "shell-test.txt"), "PASS: empty and populated library (list, grid, Videos, day groups), default-collapsed editor library, persisted roomy and narrow library preferences across switches and restarts, responsive auto-hide, floating inspector, wide header toolbar, minimum-size editor and library, Settings panel, thumbnail edit refresh, and close-to-tray reopen behavior");
        }
        finally { shell?.CloseForTest(); }
    }

    private static async Task VerifyLibraryPreferenceAsync(string root)
    {
        Directory.CreateDirectory(root);
        CaptureDocument first = ShellPreviewSamples.Board(), second = ShellPreviewSamples.Code();
        MainWindow? shell = null;
        try
        {
            shell = new MainWindow(enableHistory: true, historyRoot: root) { Width = 1500, Height = 900 };
            shell.Show(); shell.PresentDocumentForTest(first); shell.PresentDocumentForTest(second); shell.OpenInEditorForTest(first);
            await SettleAsync(shell);
            if (shell.LibraryVisibleForTest) throw new InvalidOperationException("The persisted editor library preference did not default to collapsed.");
            shell.ToggleLibraryForTest(); await SettleAsync(shell);
            if (!shell.LibraryVisibleForTest || !new Persistence.CaptureHistoryStore(root).Settings.EditorLibraryVisible)
                throw new InvalidOperationException("Opening the editor library did not persist the preference.");
            shell.SelectDocumentForTest(second); await SettleAsync(shell);
            if (!shell.LibraryVisibleForTest) throw new InvalidOperationException("Switching documents lost the open editor library preference.");
            shell.CloseEditorForTest(); shell.OpenInEditorForTest(first); await SettleAsync(shell);
            if (!shell.LibraryVisibleForTest) throw new InvalidOperationException("Reopening the editor lost the open library preference.");
            shell.CloseForTest(); shell = null;

            shell = new MainWindow(enableHistory: true, historyRoot: root) { Width = 1500, Height = 900 };
            shell.Show(); shell.PresentDocumentForTest(first); shell.OpenInEditorForTest(first); await SettleAsync(shell);
            if (!shell.LibraryVisibleForTest) throw new InvalidOperationException("Restart did not restore the open editor library preference.");
            shell.Width = 1100; await SettleAsync(shell);
            if (shell.LibraryVisibleForTest) throw new InvalidOperationException("The responsive breakpoint did not auto-hide a preferred-open library.");
            shell.ToggleLibraryForTest(); await SettleAsync(shell);
            if (!shell.LibraryVisibleForTest || !new Persistence.CaptureHistoryStore(root).Settings.EditorLibraryNarrowVisible)
                throw new InvalidOperationException("The narrow manual preference did not reveal and persist the library.");
            shell.CloseForTest(); shell = null;

            shell = new MainWindow(enableHistory: true, historyRoot: root) { Width = 1100, Height = 900 };
            shell.Show(); shell.PresentDocumentForTest(first); shell.OpenInEditorForTest(first); await SettleAsync(shell);
            if (!shell.LibraryVisibleForTest) throw new InvalidOperationException("Restart at a narrow width did not restore the explicit library preference.");
            shell.Width = 1500; await SettleAsync(shell);
            if (!shell.LibraryVisibleForTest) throw new InvalidOperationException("Widening did not restore the saved roomy open preference.");
            shell.ToggleLibraryForTest(); await SettleAsync(shell);
            if (shell.LibraryVisibleForTest || new Persistence.CaptureHistoryStore(root).Settings.EditorLibraryVisible)
                throw new InvalidOperationException("Hiding the editor library did not persist the preference.");
            shell.CloseEditorForTest(); shell.OpenInEditorForTest(first); await SettleAsync(shell);
            if (shell.LibraryVisibleForTest) throw new InvalidOperationException("Reopening the editor lost the hidden library preference.");
            shell.CloseForTest(); shell = null;

            shell = new MainWindow(enableHistory: true, historyRoot: root) { Width = 1500, Height = 900 };
            shell.Show(); shell.PresentDocumentForTest(first); shell.OpenInEditorForTest(first); await SettleAsync(shell);
            if (shell.LibraryVisibleForTest) throw new InvalidOperationException("Restart did not restore the hidden editor library preference.");
            shell.CloseEditorForTest(); await SettleAsync(shell);
            if (!shell.LibraryVisibleForTest) throw new InvalidOperationException("Library-only browsing was not full width after a hidden editor preference.");
            if (new Persistence.CaptureHistoryStore(root).Settings.EditorLibraryVisible)
                throw new InvalidOperationException("Library-only browsing changed the editor library preference.");
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
            // Content inside a vertically scrolling panel (e.g. the long Settings list) may extend below the
            // fold; it must still fit horizontally within its scroller, and the scroller itself must fit the window.
            Rect limit = client;
            bool scrollsVertically = false;
            if (VerticalScrollerAbove(element, shell) is { } scroller)
            {
                limit = scroller.TransformToAncestor(shell).TransformBounds(new Rect(scroller.RenderSize));
                scrollsVertically = true;
            }
            bool horizontallyClipped = bounds.Left < limit.Left - 1 || bounds.Right > limit.Right + 1;
            bool verticallyClipped = !scrollsVertically && (bounds.Top < limit.Top - 1 || bounds.Bottom > limit.Bottom + 1);
            if (horizontallyClipped || verticallyClipped)
                throw new InvalidOperationException($"{element.GetType().Name} is clipped in the {state}.");
        }
    }

    private static System.Windows.Controls.ScrollViewer? VerticalScrollerAbove(DependencyObject element, Window shell)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null && !ReferenceEquals(parent, shell); parent = VisualTreeHelper.GetParent(parent))
            if (parent is System.Windows.Controls.ScrollViewer { VerticalScrollBarVisibility: not System.Windows.Controls.ScrollBarVisibility.Disabled } scroller) return scroller;
        return null;
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
