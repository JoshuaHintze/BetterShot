using BetterShot.Capture.Scrolling;
using BetterShot.Editing.BackgroundRemoval;
using BetterShot.Onboarding;
using BetterShot.Platform;
using BetterShot.Updates;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterShot;

/// <summary>
/// Phase 5 diagnostics: scrolling capture stitching, background removal, update checks, first-run setup persistence,
/// and renders of the new windows. Uses isolated fixture folders; never downloads, never captures the screen.
/// </summary>
internal static class ExtrasDiagnostics
{
    [Flags]
    internal enum Areas { Scrolling = 1, BackgroundRemoval = 2, Updates = 4, Onboarding = 8, All = Scrolling | BackgroundRemoval | Updates | Onboarding }

    internal static async Task RunAsync(string? output, Areas areas, string resultFileName)
    {
        string directory = string.IsNullOrWhiteSpace(output) ? AppContext.BaseDirectory : Path.HasExtension(output) ? Path.GetDirectoryName(Path.GetFullPath(output))! : output;
        Directory.CreateDirectory(directory);
        string fixtures = Path.Combine(directory, "extras-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        var passed = new List<string>();
        if (areas.HasFlag(Areas.Scrolling))
        {
            passed.Add(ScrollingDiagnostics.Run());
            await RenderAsync(CreateScrollingControls(), Path.Combine(directory, "extras-scrolling-controls.png"));
        }
        if (areas.HasFlag(Areas.BackgroundRemoval)) passed.Add(await BackgroundRemovalDiagnostics.RunAsync(Path.Combine(fixtures, "background")));
        if (areas.HasFlag(Areas.Updates))
        {
            passed.Add(await UpdateDiagnostics.RunAsync(Path.Combine(fixtures, "updates")));
            await RenderAsync(CreateUpdateWindow(), Path.Combine(directory, "extras-update-window.png"));
        }
        if (areas.HasFlag(Areas.Onboarding))
        {
            passed.Add(OnboardingDiagnostics.Run(Path.Combine(fixtures, "onboarding")));
            passed.Add(await VerifyOnboardingUiAsync(directory));
        }
        App.WriteTestResult(Path.Combine(directory, resultFileName), "PASS: " + string.Join("; ", passed));
    }

    /// <summary>Renders every setup step, and confirms a diagnostic shell never opens setup on its own.</summary>
    private static async Task<string> VerifyOnboardingUiAsync(string directory)
    {
        MainWindow? shell = null;
        try
        {
            shell = new MainWindow();
            shell.Show();
            await SettleAsync(shell);
            if (Application.Current.Windows.OfType<OnboardingWindow>().Any())
                throw new InvalidOperationException("First-run setup: a diagnostic shell opened setup.");
            if (shell.RecorderFor(HotkeyAction.Scrolling).Tag?.ToString() != nameof(HotkeyAction.Scrolling))
                throw new InvalidOperationException("Settings: the scrolling capture shortcut recorder is missing.");
            var host = new PreviewHost();
            var window = new OnboardingWindow(host);
            try
            {
                window.Show();
                for (int step = 0; step < OnboardingWindow.StepCount; step++)
                {
                    window.ShowStep(step);
                    await SettleAsync(window);
                    Save(Render(window), Path.Combine(directory, $"extras-onboarding-step{step + 1}.png"));
                }
            }
            finally { window.Close(); }
            if (!host.Completed) throw new InvalidOperationException("First-run setup: closing the window did not mark setup as finished.");
            shell.OpenSettingsForTest();
            await SettleAsync(shell);
            Save(Render(shell), Path.Combine(directory, "extras-settings.png"));
        }
        finally { shell?.CloseForTest(); }
        return "setup steps render, closing counts as skipping, diagnostic shells never show setup, scrolling shortcut recorder";
    }

    private static Window CreateScrollingControls()
    {
        var controls = new ScrollingCaptureControlWindow();
        controls.SetProgress(4820);
        return controls;
    }

    private static Window CreateUpdateWindow()
    {
        var release = new ReleaseInfo(new SemanticVersion(0, 3, 0), "v0.3.0", "BetterShot 0.3.0", "## New\n- Scrolling capture\n- Background removal\n\n## Fixes\n- Faster thumbnails", "https://github.com/JoshuaHintze/BetterShot/releases/tag/v0.3.0",
            [new ReleaseAsset("BetterShot-0.3.0-Setup-x64.exe", "https://example.invalid/setup.exe", 1), new ReleaseAsset("BetterShot-0.3.0-Setup-x64.exe.sha256", "https://example.invalid/setup.exe.sha256", 1)]);
        return new UpdateWindow(release, new SemanticVersion(0, 2, 0), (_, _) => Task.FromException<string>(new InvalidOperationException("Diagnostics never download.")));
    }

    private static async Task RenderAsync(Window window, string path)
    {
        try
        {
            window.ShowActivated = false;
            window.Show();
            await SettleAsync(window);
            Save(Render(window), path);
        }
        finally { window.Close(); }
    }

    private static async Task SettleAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static BitmapSource Render(Window window)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        var content = (FrameworkElement)window.Content;
        int width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>In-memory host so rendering setup changes no real settings, shortcuts, or startup entries.</summary>
    private sealed class PreviewHost : IOnboardingHost
    {
        private readonly Dictionary<HotkeyAction, HotkeyBinding> _hotkeys = new()
        {
            [HotkeyAction.Window] = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x57)
        };
        internal bool Completed { get; private set; }
        public bool ReplaceScreenshotShortcut { get; private set; } = true;
        public bool SetReplaceScreenshotShortcut(bool enabled) => ReplaceScreenshotShortcut = enabled;
        public bool CanConfigureStartup => true;
        public bool StartWithWindows { get; private set; }
        public bool SetStartWithWindows(bool enabled) => StartWithWindows = enabled;
        public HotkeyBinding HotkeyFor(HotkeyAction action) => _hotkeys.GetValueOrDefault(action);
        public bool IsHotkeyUnavailable(HotkeyAction action) => false;
        public string? ApplyHotkey(HotkeyAction action, HotkeyBinding binding) { _hotkeys[action] = binding; return null; }
        public void BeginHotkeyRecording() { }
        public void EndHotkeyRecording() { }
        public void CompleteOnboarding() => Completed = true;
        public void TakeFirstScreenshot() { }
        public Style? FindStyle(string key) => null;
    }
}
