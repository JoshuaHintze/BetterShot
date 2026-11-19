using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Editing.BackgroundRemoval;

/// <summary>
/// Immutable 8-bit opacity mask the size of a capture's original image (0 = removed, 255 = kept). The original
/// pixels are never changed; the mask is applied when the document renders, so removal is undoable and reversible.
/// </summary>
internal sealed class AlphaMask
{
    private readonly byte[] _alpha;

    internal AlphaMask(int width, int height, byte[] alpha)
    {
        ArgumentNullException.ThrowIfNull(alpha);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "The mask must have a positive size.");
        if (alpha.Length != width * height) throw new ArgumentException("The mask data does not match its size.", nameof(alpha));
        Width = width;
        Height = height;
        _alpha = (byte[])alpha.Clone();
    }

    internal int Width { get; }
    internal int Height { get; }
    internal ReadOnlySpan<byte> Values => _alpha;

    internal bool Fits(BitmapSource image) => image.PixelWidth == Width && image.PixelHeight == Height;

    /// <summary>Returns the image with its alpha multiplied by the mask (straight-alpha BGRA32).</summary>
    internal BitmapSource ApplyTo(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!Fits(image)) throw new ArgumentException("The mask size does not match the image.", nameof(image));
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[Width * Height * 4];
        converted.CopyPixels(pixels, Width * 4, 0);
        ApplyToBgra(pixels, _alpha);
        var result = BitmapSource.Create(Width, Height, image.DpiX > 0 ? image.DpiX : 96, image.DpiY > 0 ? image.DpiY : 96, PixelFormats.Bgra32, null, pixels, Width * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Multiplies each pixel's alpha by the mask value. Pure; exposed for diagnostics.</summary>
    internal static void ApplyToBgra(byte[] bgra, ReadOnlySpan<byte> mask)
    {
        if (bgra.Length != mask.Length * 4) throw new ArgumentException("Pixel and mask sizes differ.", nameof(bgra));
        for (int index = 0; index < mask.Length; index++)
        {
            int alpha = index * 4 + 3;
            bgra[alpha] = (byte)((bgra[alpha] * mask[index] + 127) / 255);
        }
    }

    /// <summary>Places a mask computed for a cropped region into a full-size mask; everything outside the region is removed.</summary>
    internal static AlphaMask Embed(AlphaMask region, Int32Rect at, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (at.Width != region.Width || at.Height != region.Height || at.X < 0 || at.Y < 0 || at.X + at.Width > width || at.Y + at.Height > height)
            throw new ArgumentException("The region does not fit inside the full mask.", nameof(at));
        var values = new byte[width * height];
        for (int row = 0; row < region.Height; row++)
            region._alpha.AsSpan(row * region.Width, region.Width).CopyTo(values.AsSpan((at.Y + row) * width + at.X, region.Width));
        return new AlphaMask(width, height, values);
    }

    internal byte[] EncodePng()
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Gray8, null, _alpha, Width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    internal static AlphaMask DecodePng(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("The background mask is invalid.");
        BitmapSource frame = decoder.Frames[0];
        var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
        var values = new byte[gray.PixelWidth * gray.PixelHeight];
        gray.CopyPixels(values, gray.PixelWidth, 0);
        return new AlphaMask(gray.PixelWidth, gray.PixelHeight, values);
    }
}
