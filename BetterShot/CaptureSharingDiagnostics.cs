using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace BetterShot;

internal static class CaptureSharingDiagnostics
{
    internal static async Task RunAsync(string? output)
    {
        MainWindow? shell = null;
        try
        {
            shell = new MainWindow();
            Application.Current.MainWindow = shell;
            shell.InitializeInTray();
            var image = Editing.EditorDiagnostics.CreateSampleDocument().Original;
            int calls = 0;
            var thumbnail = await shell.CompleteCaptureAsync(image, copied =>
            {
                calls++;
                if (!ReferenceEquals(copied, image) || shell.Presentation.Recent.Count != 0)
                    throw new InvalidOperationException("Auto-copy was delayed until after history presentation or copied another image.");
            });
            Check(calls == 1 && shell.Presentation.Recent.Count == 1 && thumbnail.IsVisible, "Capture did not auto-copy and present a thumbnail.");
            Check(!shell.IsVisible && shell.Presentation.OpenEditorCount == 0, "Auto-copy opened the shell or editor.");
            int attempts = 0;
            await shell.CompleteCaptureAsync(image, _ => { if (++attempts < 3) throw new COMException("busy"); });
            Check(attempts == 3 && shell.Presentation.Recent.Count == 2, "Clipboard busy retry did not recover.");
            attempts = 0;
            var failedCopy = await shell.CompleteCaptureAsync(image, _ => { attempts++; throw new COMException("busy"); });
            Check(attempts == 5 && shell.Presentation.Recent.Count == 3 && failedCopy.IsVisible, "Clipboard failure lost the capture or did not exhaust retries.");
            Check(shell.StatusText.Text.Contains("could not copy"), "Clipboard failure was not reported.");
            App.WriteTestResult(output, "PASS: immediate automatic clipboard write, busy retry, thumbnail-first behavior, and capture preservation on clipboard failure (injected clipboard writer)", "sharing-test.txt");
        }
        finally { shell?.CloseForTest(); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}