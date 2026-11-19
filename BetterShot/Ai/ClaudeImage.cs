using BetterShot.Export;
using System.IO;
using System.Windows.Media.Imaging;

namespace BetterShot.Ai;

/// <summary>Prepares the rendered capture for the API: long edge at most 1568 px, PNG, base64.</summary>
internal static class ClaudeImage
{
    internal const int MaxLongEdge = 1568;
    /// <summary>The API limits base64 images to 5 MB; stay under it after the 4/3 base64 expansion.</summary>
    internal const int MaxEncodedBytes = 3_700_000;

    internal static (int Width, int Height) FitLongEdge(int width, int height) =>
        ExportResize.FitWithin(width, height, MaxLongEdge);

    /// <summary>Must run on a UI (STA) thread: resizing renders through WPF.</summary>
    internal static BitmapSource Downscale(BitmapSource image) => ImageExporter.Resize(image, MaxLongEdge);

    /// <summary>Downscales, encodes PNG, and shrinks further only if a very detailed capture is still over the size limit.</summary>
    internal static string ToPngBase64(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        BitmapSource prepared = Downscale(image);
        byte[] png = EncodePng(prepared);
        while (png.Length > MaxEncodedBytes && Math.Max(prepared.PixelWidth, prepared.PixelHeight) > 400)
        {
            int smaller = (int)(Math.Max(prepared.PixelWidth, prepared.PixelHeight) * 0.75);
            prepared = ImageExporter.Resize(prepared, smaller);
            png = EncodePng(prepared);
        }
        return Convert.ToBase64String(png);
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        using var stream = new MemoryStream();
        ImageExporter.Encode(image, CaptureImageFormat.Png, 100, stream);
        return stream.ToArray();
    }
}
