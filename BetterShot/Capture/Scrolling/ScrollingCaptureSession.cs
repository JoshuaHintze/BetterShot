using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Capture.Scrolling;

internal enum ScrollingCaptureOutcome
{
    /// <summary>The content scrolled and was stitched; <see cref="ScrollingCaptureResult.Image"/> is set.</summary>
    Captured,
    /// <summary>Nothing under the region moved after scrolling.</summary>
    NothingScrolled
}

internal enum ScrollingStopReason { EndOfContent, UserStopped, MaxHeight, LostTrack, StepLimit }

internal sealed record ScrollingCaptureResult(ScrollingCaptureOutcome Outcome, BitmapSource? Image, ScrollingStopReason StopReason);

/// <summary>
/// Drives one scrolling capture: grab a frame, scroll, wait for the content to settle, grab again, and stitch,
/// until the content stops moving, the user stops, or the maximum height is reached. Runs on the UI thread with
/// awaits between steps; pixel work happens on the thread pool.
/// </summary>
internal sealed class ScrollingCaptureSession
{
    private const int MaximumSteps = 400;
    private const int InitialNotches = 3;
    private const int MaximumNotches = 15;
    private const int SettleAttempts = 5;
    private static readonly TimeSpan ScrollSettleDelay = TimeSpan.FromMilliseconds(170);
    private static readonly TimeSpan StabilityDelay = TimeSpan.FromMilliseconds(90);

    private readonly Int32Rect _region;
    private readonly int _maxHeight;
    private readonly CancellationTokenSource _stop = new();
    private ScrollingCaptureControlWindow? _controls;

    internal ScrollingCaptureSession(Int32Rect region, int maxHeight = ScrollStitcher.DefaultMaxHeight)
    {
        if (region.Width < 32 || region.Height < 48) throw new ArgumentException("Choose a larger area to scroll.", nameof(region));
        _region = region;
        _maxHeight = maxHeight;
    }

    internal async Task<ScrollingCaptureResult> RunAsync()
    {
        _controls = new ScrollingCaptureControlWindow();
        _controls.StopRequested += (_, _) => _stop.Cancel();
        try
        {
            _controls.Show();
            _controls.PlaceFor(_region);
            var stitcher = new ScrollStitcher(_region.Width, _region.Height, _maxHeight);
            stitcher.AddFrame(await GrabAsync());
            ScrollingStopReason reason = await ScrollLoopAsync(stitcher);
            if (stitcher.TotalAppendedRows == 0)
                return new ScrollingCaptureResult(ScrollingCaptureOutcome.NothingScrolled, null, reason);
            BitmapSource image = ToBitmap(await Task.Run(stitcher.Compose));
            return new ScrollingCaptureResult(ScrollingCaptureOutcome.Captured, image, reason);
        }
        finally
        {
            _controls.Close();
            _controls = null;
            _stop.Dispose();
        }
    }

    private async Task<ScrollingStopReason> ScrollLoopAsync(ScrollStitcher stitcher)
    {
        int notches = InitialNotches;
        int unchanged = 0;
        bool automationFallback = false;
        int centerX = _region.X + _region.Width / 2, centerY = _region.Y + _region.Height / 2;
        for (int step = 0; step < MaximumSteps; step++)
        {
            if (_stop.IsCancellationRequested) return ScrollingStopReason.UserStopped;
            bool scrolled = automationFallback
                ? await Task.Run(() => ScrollInput.TryScrollWithAutomation(centerX, centerY, notches))
                : ScrollInput.SendWheel(centerX, centerY, notches);
            if (!scrolled && !automationFallback) automationFallback = true;
            if (!await DelayUnlessStoppedAsync(ScrollSettleDelay)) return ScrollingStopReason.UserStopped;
            byte[]? frame = await GrabSettledAsync();
            if (frame is null) return ScrollingStopReason.UserStopped;
            StitchStepResult result = await Task.Run(() => stitcher.AddFrame(frame));
            switch (result.Status)
            {
                case StitchStatus.Appended:
                    unchanged = 0;
                    notches = AdaptNotches(notches, result.Shift);
                    _controls?.SetProgress(stitcher.ResultHeight);
                    break;
                case StitchStatus.ReachedMaxHeight:
                    _controls?.SetProgress(stitcher.ResultHeight);
                    return ScrollingStopReason.MaxHeight;
                case StitchStatus.NoChange:
                    unchanged++;
                    // Wheel input can be ignored by some apps; try UI Automation once before deciding nothing moves.
                    if (stitcher.TotalAppendedRows == 0 && !automationFallback) { automationFallback = true; unchanged = 0; continue; }
                    if (unchanged >= 2) return ScrollingStopReason.EndOfContent;
                    break;
                case StitchStatus.NoMatch:
                    // A scroll larger than the visible area loses the overlap; scroll less and stop if it happens again.
                    if (notches > 1) { notches = Math.Max(1, notches / 3); continue; }
                    return ScrollingStopReason.LostTrack;
            }
        }
        return ScrollingStopReason.StepLimit;
    }

    /// <summary>Aims each step at about 45% of the visible height so consecutive frames keep a large overlap.</summary>
    private int AdaptNotches(int notches, int shift)
    {
        if (shift <= 0 || notches <= 0) return notches;
        double perNotch = (double)shift / notches;
        int target = (int)Math.Floor(_region.Height * 0.45 / perNotch);
        return Math.Clamp(target, 1, MaximumNotches);
    }

    /// <summary>Grabs until two consecutive frames match (smooth scrolling has finished), up to a few attempts.</summary>
    private async Task<byte[]?> GrabSettledAsync()
    {
        byte[] current = await GrabAsync();
        for (int attempt = 0; attempt < SettleAttempts; attempt++)
        {
            if (!await DelayUnlessStoppedAsync(StabilityDelay)) return null;
            byte[] next = await GrabAsync();
            if (next.AsSpan().SequenceEqual(current)) return next;
            current = next;
        }
        return current;
    }

    private async Task<byte[]> GrabAsync()
    {
        bool hide = _controls is { ExcludedFromCapture: false, IsVisible: true };
        if (hide)
        {
            _controls!.Hide();
            await Task.Delay(60);
        }
        try { return await Task.Run(() => ScreenRegionGrabber.Grab(_region)); }
        finally { if (hide) _controls?.Show(); }
    }

    private async Task<bool> DelayUnlessStoppedAsync(TimeSpan delay)
    {
        try { await Task.Delay(delay, _stop.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private static BitmapSource ToBitmap((byte[] Pixels, int Width, int Height) composed)
    {
        var bitmap = BitmapSource.Create(composed.Width, composed.Height, 96, 96, PixelFormats.Bgra32, null, composed.Pixels, composed.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
