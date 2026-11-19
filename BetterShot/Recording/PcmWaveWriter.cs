using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace BetterShot.Recording;

/// <summary>
/// Streams 16-bit PCM to a RIFF/WAVE file. The header is written up front with placeholder sizes and patched by
/// <see cref="Complete"/>, so a crash leaves a file whose header still says "empty" rather than a wrong length.
/// Not thread-safe; the owner serializes calls.
/// </summary>
internal sealed class PcmWaveWriter : IDisposable
{
    internal const int HeaderBytes = 44;
    private readonly FileStream _stream;
    private byte[] _pcm = [];
    private bool _completed;

    internal PcmWaveWriter(string path, int sampleRate, int channels)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        SampleRate = sampleRate;
        Channels = channels;
        FilePath = path;
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
        _stream.Write(BuildHeader(sampleRate, channels, 0));
    }

    internal string FilePath { get; }
    internal int SampleRate { get; }
    internal int Channels { get; }
    internal long FramesWritten { get; private set; }

    /// <summary>Clamps interleaved float samples to 16-bit PCM and appends them.</summary>
    internal void WriteFloat(ReadOnlySpan<float> interleaved)
    {
        if (_completed) throw new InvalidOperationException("The WAV file is already complete.");
        int bytes = interleaved.Length * 2;
        if (_pcm.Length < bytes) _pcm = new byte[bytes];
        PcmConverter.ToInt16(interleaved, _pcm);
        _stream.Write(_pcm, 0, bytes);
        FramesWritten += interleaved.Length / Channels;
    }

    /// <summary>Appends interleaved little-endian 16-bit PCM bytes as they are.</summary>
    internal void WritePcm(ReadOnlySpan<byte> pcm)
    {
        if (_completed) throw new InvalidOperationException("The WAV file is already complete.");
        _stream.Write(pcm);
        FramesWritten += pcm.Length / (Channels * 2);
    }

    /// <summary>Writes the final RIFF and data sizes and flushes. Safe to call more than once.</summary>
    internal void Complete()
    {
        if (_completed) return;
        _completed = true;
        long dataBytes = FramesWritten * Channels * 2;
        _stream.Position = 0;
        _stream.Write(BuildHeader(SampleRate, Channels, dataBytes));
        _stream.Flush(true);
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>The canonical 44-byte PCM header (sizes saturate at 4 GB, which readers treat as "to the end").</summary>
    internal static byte[] BuildHeader(int sampleRate, int channels, long dataBytes)
    {
        byte[] header = new byte[HeaderBytes];
        uint dataSize = (uint)Math.Min(dataBytes, uint.MaxValue - 36);
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), dataSize + 36);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)(sampleRate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), dataSize);
        return header;
    }
}
