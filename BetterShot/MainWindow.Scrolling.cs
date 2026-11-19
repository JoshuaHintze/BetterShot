using BetterShot.Capture.Scrolling;
using BetterShot.Presentation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterShot;

/// <summary>Scrolling capture: select an area or window, auto-scroll it, and stitch the frames into one capture.</summary>
public partial class MainWindow
{
    private void Scrolling_Click(object sender, RoutedEventArgs e) => _ = ScrollingCaptureAsync();

    internal async Task ScrollingCaptureAsync()
    {
        if (_capturing) { _countdownCancellation?.Cancel(); return; }
        _capturing = true; SetCaptureControls(false); SetStatus("Select the area to scroll, or click a window…", persistent: true);
        CaptureVisibilitySnapshot snapshot = _presentation.HideForCapture();
        bool keepMainHidden = false;
        try
        {
            if (!await WaitForCaptureDelayAsync()) { SetStatus("Capture canceled."); return; }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(180);
            Int32Rect? region = await _captureService.SelectScreenRegionAsync();
            if (region is not Int32Rect selected) { SetStatus("Capture canceled."); return; }
            if (selected.Width < 32 || selected.Height < 48) { SetStatus("Choose a larger area for a scrolling capture.", error: true); return; }
            await Task.Delay(150); // Let the selection overlay disappear before the first frame.
            ScrollingCaptureResult result = await new ScrollingCaptureSession(selected).RunAsync();
            if (result.Outcome == ScrollingCaptureOutcome.NothingScrolled || result.Image is not BitmapSource image)
            {
                SetStatus("Nothing scrolled. Point the selection at scrollable content and try again.", error: true, persistent: true);
                ShowAndActivate();
                return;
            }
            keepMainHidden = true;
            _presentation.RestoreAfterCapture(snapshot, this);
            await CompleteCaptureAsync(image);
            if (result.StopReason == ScrollingStopReason.MaxHeight)
                SetStatus($"Scrolling capture stopped at the {image.PixelHeight:N0} px limit.");
            else if (result.StopReason == ScrollingStopReason.LostTrack)
                SetStatus("The content changed while scrolling, so BetterShot kept what it captured so far.");
        }
        catch (Exception ex)
        {
            SetStatus($"Scrolling capture failed: {FriendlyMessage(ex)}", error: true, persistent: true);
            ShowAndActivate();
        }
        finally
        {
            if (!keepMainHidden) _presentation.RestoreAfterCapture(snapshot);
            _capturing = false; SetCaptureControls(true);
        }
    }
}
