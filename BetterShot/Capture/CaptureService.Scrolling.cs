namespace BetterShot.Capture;

public sealed partial class CaptureService
{
    /// <summary>
    /// Opens the frozen area selector (drag a region or click a window) and returns the chosen rectangle in
    /// physical virtual-desktop pixels, clipped to the desktop. Returns null when canceled.
    /// </summary>
    internal async Task<Int32Rect?> SelectScreenRegionAsync()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            return await dispatcher.InvokeAsync(SelectScreenRegionAsync).Task.Unwrap();

        var (desktop, virtualBounds) = CaptureVirtualDesktop();
        Int32Rect? selected = await SelectionSession.SelectAsync(desktop, virtualBounds, CaptureMode.Area);
        if (selected is not Int32Rect region || region.Width <= 0 || region.Height <= 0) return null;
        Int32Rect clipped = ScreenGeometry.Intersect(region, virtualBounds);
        return clipped.IsEmpty ? null : clipped;
    }
}
