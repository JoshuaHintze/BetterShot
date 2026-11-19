using BetterShot.Ocr;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BetterShot.Presentation;

/// <summary>Thumbnail hover action that copies all text in the capture, read on this PC.</summary>
public partial class CaptureThumbnailWindow
{
    private DispatcherTimer? _toastTimer;
    private bool _readingText;

    internal string TextToastForTest => TextToastText.Text;

    private async void CopyText_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_readingText) return;
        _readingText = true;
        ShowTextToast("Reading the text…", persistent: true);
        try
        {
            RecognizedText text = await CaptureTextReader.ReadVisibleAsync(Document, default);
            if (text.IsEmpty) { ShowTextToast("No text found in this capture."); return; }
            string? error = await ClipboardText.SetAsync(text.FullText);
            ShowTextToast(error is null ? "Copied to clipboard" : $"Couldn't copy: {error}");
        }
        catch (OcrUnavailableException ex) { ShowTextToast(ex.Message, seconds: 8); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowTextToast($"Couldn't read the text: {ex.Message}"); }
        finally { _readingText = false; }
    }

    private void ShowTextToast(string message, bool persistent = false, double seconds = 2.4)
    {
        TextToastText.Text = message;
        _toastTimer?.Stop();
        TextToast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
        if (persistent) return;
        _toastTimer ??= new DispatcherTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Tick -= ToastTimer_Tick;
        _toastTimer.Tick += ToastTimer_Tick;
        _toastTimer.Start();
    }

    private void ToastTimer_Tick(object? sender, EventArgs e)
    {
        _toastTimer?.Stop();
        TextToast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)));
    }
}
