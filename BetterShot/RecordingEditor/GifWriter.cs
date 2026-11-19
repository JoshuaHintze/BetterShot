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
        byte[] bytes = EnsureLooping(buffer.ToArray());
        output.Write(bytes, 0, bytes.Length);
    }

    internal static BitmapSource Palettize(BitmapSource frame)
    {
        BitmapSource bgra = frame.Format == PixelFormats.Bgra32 || frame.Format == PixelFormats.Bgr32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
        var palette = new BitmapPalette(bgra, 256);
        var indexed = new FormatConvertedBitmap(bgra, PixelFormats.Indexed8, palette, 0);
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
