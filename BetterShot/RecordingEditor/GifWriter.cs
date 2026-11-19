using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.RecordingEditor;

/// <summary>
/// Writes an endlessly looping animated GIF with WPF's GIF encoder: each frame gets an optimized 256-color palette and
/// its own delay (/grctlext/Delay), and the NETSCAPE2.0 loop extension is added to the first frame (and inserted into
/// the file afterwards if the encoder dropped it).
/// </summary>
internal static class GifWriter
{
    private static readonly byte[] NetscapeIdentifier = Encoding.ASCII.GetBytes("NETSCAPE2.0");

    internal static void Write(Stream output, IReadOnlyList<BitmapSource> frames, IReadOnlyList<ushort> delays)
    {
        if (frames.Count == 0) throw new ArgumentException("A GIF needs at least one frame.", nameof(frames));
        var encoder = new GifBitmapEncoder();
        for (int index = 0; index < frames.Count; index++)
            encoder.Frames.Add(BitmapFrame.Create(Palettize(frames[index]), null, FrameMetadata(delays[Math.Min(index, delays.Count - 1)], index == 0), null));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        byte[] bytes = EnsureLooping(ApplyFrameDelays(buffer.ToArray(), frames.Count, delays));
        output.Write(bytes, 0, bytes.Length);
    }

    internal static BitmapSource Palettize(BitmapSource frame)
    {
        BitmapSource source = frame.Format == PixelFormats.Bgra32 || frame.Format == PixelFormats.Bgr32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
        // Work on plain pixel copies: a FormatConvertedBitmap here could not be frozen ("This Freezable cannot be
        // frozen"), and the encoder needs frozen frames.
        int stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        BitmapSource bgra = BitmapSource.Create(source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY, source.Format, null, pixels, stride);
        bgra.Freeze();
        var palette = new BitmapPalette(bgra, 256);
        var converted = new FormatConvertedBitmap(bgra, PixelFormats.Indexed8, palette, 0);
        int indexedStride = (converted.PixelWidth + 3) & ~3;
        var indexes = new byte[indexedStride * converted.PixelHeight];
        converted.CopyPixels(indexes, indexedStride, 0);
        BitmapSource indexed = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, bgra.DpiX, bgra.DpiY, PixelFormats.Indexed8, palette, indexes, indexedStride);
        indexed.Freeze();
        return indexed;
    }

    private static BitmapMetadata FrameMetadata(ushort delay, bool first)
    {
        var metadata = new BitmapMetadata("gif");
        metadata.SetQuery("/grctlext/Delay", delay);
        metadata.SetQuery("/grctlext/Disposal", (byte)1); // Leave the frame in place; every frame is full size.
        if (!first) return metadata;
        try
        {
            metadata.SetQuery("/appext/Application", NetscapeIdentifier);
            metadata.SetQuery("/appext/Data", new byte[] { 3, 1, 0, 0, 0 }); // Sub-block: loop forever.
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Some WIC versions refuse frame-level application extensions; EnsureLooping adds the block afterwards.
        }
        return metadata;
    }

    /// <summary>
    /// Rewrites each frame's Graphic Control Extension with its delay (hundredths of a second) and "leave in place"
    /// disposal. WPF's GifBitmapEncoder drops frame metadata, so without this every frame plays at the viewer's default speed.
    /// </summary>
    internal static byte[] ApplyFrameDelays(byte[] gif, int frameCount, IReadOnlyList<ushort> delays)
    {
        if (gif.Length < 13 || Encoding.ASCII.GetString(gif, 0, 3) != "GIF" || delays.Count == 0) return gif;
        int position = 13;
        if ((gif[10] & 0x80) != 0) position += 3 * (1 << ((gif[10] & 0x07) + 1));
        if (position > gif.Length) return gif;
        using var output = new MemoryStream(gif.Length + frameCount * 8);
        output.Write(gif, 0, position);
        int frame = 0;
        while (position < gif.Length)
        {
            byte block = gif[position];
            if (block == 0x3B) { output.Write(gif, position, gif.Length - position); return output.ToArray(); }
            if (block == 0x21 && position + 1 < gif.Length)
            {
                int end = SkipSubBlocks(gif, position + 2);
                if (end < 0) return gif;
                if (gif[position + 1] != 0xF9) output.Write(gif, position, end - position); // Keep every extension except the old control block.
                position = end;
                continue;
            }
            if (block != 0x2C || position + 10 > gif.Length) return gif;
            ushort delay = delays[Math.Min(frame, delays.Count - 1)];
            output.Write([0x21, 0xF9, 0x04, 0x04, (byte)(delay & 0xFF), (byte)(delay >> 8), 0x00, 0x00]);
            int imageStart = position;
            byte packed = gif[position + 9];
            position += 10;
            if ((packed & 0x80) != 0) position += 3 * (1 << ((packed & 0x07) + 1));
            int imageEnd = position < gif.Length ? SkipSubBlocks(gif, position + 1) : -1; // +1 skips the LZW minimum code size.
            if (imageEnd < 0) return gif;
            output.Write(gif, imageStart, imageEnd - imageStart);
            position = imageEnd;
            frame++;
        }
        return gif; // No trailer: leave the encoder's output untouched.
    }

    /// <summary>Returns the index just past a run of GIF data sub-blocks (ending with a zero-length block), or -1 if truncated.</summary>
    private static int SkipSubBlocks(byte[] gif, int position)
    {
        while (position < gif.Length)
        {
            int size = gif[position];
            position += 1 + size;
            if (size == 0) return position <= gif.Length ? position : -1;
        }
        return -1;
    }

    /// <summary>Inserts a NETSCAPE2.0 "loop forever" block after the global color table when the file has none.</summary>
    internal static byte[] EnsureLooping(byte[] gif)
    {
        if (gif.Length < 13 || Encoding.ASCII.GetString(gif, 0, 3) != "GIF" || IndexOf(gif, NetscapeIdentifier) >= 0) return gif;
        byte packed = gif[10];
        int offset = 13 + ((packed & 0x80) != 0 ? 3 * (1 << ((packed & 0x07) + 1)) : 0);
        if (offset > gif.Length) return gif;
        byte[] block = [0x21, 0xFF, 0x0B, .. NetscapeIdentifier, 0x03, 0x01, 0x00, 0x00, 0x00];
        return [.. gif.AsSpan(0, offset), .. block, .. gif.AsSpan(offset)];
    }

    /// <summary>Loop count of the NETSCAPE2.0 block (0 = forever), or null when the file does not loop.</summary>
    internal static int? ReadLoopCount(byte[] gif)
    {
        int index = IndexOf(gif, NetscapeIdentifier);
        if (index < 0 || index + NetscapeIdentifier.Length + 4 > gif.Length) return null;
        int data = index + NetscapeIdentifier.Length;
        if (gif[data] != 3 || gif[data + 1] != 1) return null;
        return gif[data + 2] | gif[data + 3] << 8;
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);
}
