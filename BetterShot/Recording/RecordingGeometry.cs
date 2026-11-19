using System.Windows.Media;
using BetterShot.Capture;

namespace BetterShot.Recording;

/// <summary>
/// Pure physical-pixel math for screen recording: which monitor a region belongs to, the crop box inside that
/// monitor's capture frames, and H.264-friendly (even) output sizes. All rectangles are physical pixels.
/// </summary>
internal static class RecordingGeometry
{
    /// <summary>Regions smaller than this (either side) are rejected; H.264 encoders handle tiny frames poorly.</summary>
    internal const int MinimumDimension = 32;
    /// <summary>Common hardware H.264 encoders accept up to 4096 pixels on a side.</summary>
    internal const int MaximumEncoderDimension = 4096;

    /// <summary>Rounds down to an even number (H.264 4:2:0 needs even width and height). Never below 2.</summary>
    internal static int EvenDown(int value) => Math.Max(2, value - (value & 1));

    /// <summary>Returns the monitor that overlaps the region most (ties go to the first), or null when none overlap.</summary>
    internal static Int32Rect? ChooseMonitor(Int32Rect region, IReadOnlyList<Int32Rect> monitors)
    {
        Int32Rect? best = null;
        long bestArea = 0;
        foreach (Int32Rect monitor in monitors)
        {
            Int32Rect overlap = ScreenGeometry.Intersect(region, monitor);
            long area = overlap.IsEmpty ? 0 : (long)overlap.Width * overlap.Height;
            if (area > bestArea) { bestArea = area; best = monitor; }
        }
        return best;
    }

    /// <summary>
    /// Converts a virtual-screen region into a crop box relative to the monitor's top-left corner, clipped to the
    /// monitor and shrunk on the right/bottom edge to even dimensions. Returns <see cref="Int32Rect.Empty"/> when the
    /// usable part is smaller than <see cref="MinimumDimension"/>.
    /// </summary>
    internal static Int32Rect ComputeCrop(Int32Rect region, Int32Rect monitor)
    {
        Int32Rect clipped = ScreenGeometry.Intersect(region, monitor);
        if (clipped.IsEmpty || clipped.Width < MinimumDimension || clipped.Height < MinimumDimension) return Int32Rect.Empty;
        return new Int32Rect(clipped.X - monitor.X, clipped.Y - monitor.Y, EvenDown(clipped.Width), EvenDown(clipped.Height));
    }

    /// <summary>Even output size for a whole display or window frame.</summary>
    internal static (int Width, int Height) EvenSize(int width, int height) => (EvenDown(width), EvenDown(height));

    /// <summary>
    /// The part of <paramref name="crop"/> that exists in a frame of the given size (frames can shrink when a window
    /// is resized or a display changes mode). Empty when nothing overlaps.
    /// </summary>
    internal static Int32Rect ClampToFrame(Int32Rect crop, int frameWidth, int frameHeight) =>
        ScreenGeometry.Intersect(crop, new Int32Rect(0, 0, Math.Max(0, frameWidth), Math.Max(0, frameHeight)));

    /// <summary>Copy box when there is no crop: the top-left part of the frame that fits the fixed output size.</summary>
    internal static Int32Rect FitFrameToOutput(int frameWidth, int frameHeight, int outputWidth, int outputHeight)
    {
        int width = Math.Max(0, Math.Min(frameWidth, outputWidth)), height = Math.Max(0, Math.Min(frameHeight, outputHeight));
        return width == 0 || height == 0 ? Int32Rect.Empty : new Int32Rect(0, 0, width, height);
    }

    internal static bool ExceedsEncoderLimits(int width, int height) =>
        width > MaximumEncoderDimension || height > MaximumEncoderDimension;

    /// <summary>
    /// Maps a physical screen point into a window-local WPF coordinate (DIP) for an overlay window whose top-left
    /// corner is at <paramref name="windowBounds"/> physical pixels and whose DPI scale is <paramref name="scale"/>.
    /// </summary>
    internal static Point ScreenToOverlay(int screenX, int screenY, Int32Rect windowBounds, double scale)
    {
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        return new Point((screenX - windowBounds.X) / scale, (screenY - windowBounds.Y) / scale);
    }

    /// <summary>Point relative to the recorded area in output video pixels, or null when outside it.</summary>
    internal static Point? ToVideoPixels(int screenX, int screenY, Int32Rect recordedScreenArea)
    {
        int x = screenX - recordedScreenArea.X, y = screenY - recordedScreenArea.Y;
        if (x < 0 || y < 0 || x >= recordedScreenArea.Width || y >= recordedScreenArea.Height) return null;
        return new Point(x, y);
    }

    /// <summary>A reasonable H.264 bitrate for the frame size and rate (about 0.1 bits per pixel per frame, clamped).</summary>
    internal static uint SuggestBitrate(int width, int height, int framesPerSecond)
    {
        double bits = (double)width * height * framesPerSecond * 0.1;
        return (uint)Math.Clamp(bits, 2_000_000, 40_000_000);
    }

    /// <summary>Geometry for the dim overlay: the whole monitor with the recorded region cut out, in DIPs.</summary>
    internal static Geometry BuildDimGeometry(Rect monitorDip, Rect holeDip)
    {
        var geometry = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(monitorDip), new RectangleGeometry(holeDip));
        geometry.Freeze();
        return geometry;
    }
}
