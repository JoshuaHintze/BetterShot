using BetterShot.Persistence;
using BetterShot.Platform;
using System.IO;
using System.Windows.Interop;

namespace BetterShot;

internal static class StartupDiagnostics
{
    internal static void Run(string? output)
    {
        string directory = string.IsNullOrWhiteSpace(output) ? AppContext.BaseDirectory : Path.HasExtension(output) ? Path.GetDirectoryName(Path.GetFullPath(output))! : output;
        Directory.CreateDirectory(directory);
        string root = Path.Combine(directory, "startup-fixture-" + Guid.NewGuid().ToString("N"));
        string startup = Path.Combine(root, "Startup");
        string history = Path.Combine(root, "History");
        Directory.CreateDirectory(startup);
        File.WriteAllText(Path.Combine(startup, "unrelated.txt"), "preserve");
        var service = new WindowsStartupRegistration(startup);
        string args = service.LaunchArgumentsForTest;
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll")))
            Check(args == "--background", "Standalone startup should launch the app directly.");
        else
        {
            string script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(args.Split(' ').Last()));
            Check(script.Contains("--background") && script.Contains("DOTNET_ROOT") && args.Contains("-WindowStyle Hidden"), "Startup launcher lost tray mode or runtime initialization.");
        }
        service.EnsureEnabled();
        string link = Path.Combine(startup, "BetterShot.lnk");
        Check(service.IsEnabled && File.Exists(link), "Startup shortcut was not created.");
        DateTime stamp = File.GetLastWriteTimeUtc(link);
        service.EnsureEnabled();
        Check(File.GetLastWriteTimeUtc(link) == stamp, "An unchanged startup shortcut was rewritten.");
        service.Disable();
        Check(!service.IsEnabled && File.Exists(Path.Combine(startup, "unrelated.txt")), "Disabling startup damaged an unrelated file.");
        var settings = new CaptureHistoryStore(history);
        settings.SetRetentionDays(14);
        settings.SetReplaceScreenshotShortcut(true);
        settings.SetStartWithWindows(true);
        settings.SetRetentionDays(30);
        settings.SetReplaceScreenshotShortcut(false);
        var restored = new CaptureHistoryStore(history);
        Check(restored.StartWithWindows && restored.RetentionDays == 30 && !restored.ReplaceScreenshotShortcut, "Settings setters did not preserve startup preference.");
        MainWindow? window = null;
        try
        {
            window = new MainWindow(true, history, startup);
            Application.Current.MainWindow = window;
            window.InitializeInTray();
            Check(service.IsEnabled && window.StartWithWindowsCheckBox.IsChecked == true, "Startup registration was not restored.");
            Check(!window.IsVisible && window.TrayVisibleForTest && new WindowInteropHelper(window).Handle != IntPtr.Zero, "Background startup did not initialize a hidden window and tray.");
            window.CloseForTest(); window = null;
            service.Disable();
            window = new MainWindow(true, history, startup);
            Application.Current.MainWindow = window;
            window.InitializeInTray();
            Check(service.IsEnabled, "Missing startup entry was not repaired.");
            window.StartWithWindowsCheckBox.IsChecked = false;
            Check(!service.IsEnabled && !new CaptureHistoryStore(history).StartWithWindows, "Disabling startup did not remove and save the choice.");
            window.CloseForTest(); window = null;
            window = new MainWindow(true, history, startup);
            Application.Current.MainWindow = window;
            window.InitializeInTray();
            Check(!service.IsEnabled && window.StartWithWindowsCheckBox.IsChecked != true, "Disabled startup returned after restart.");
        }
        finally { window?.CloseForTest(); }
        App.WriteTestResult(Path.Combine(directory, "startup-test.txt"), "PASS: isolated startup registration, idempotent check, missing-entry repair, disable persistence, preference preservation, and hidden tray initialization");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}