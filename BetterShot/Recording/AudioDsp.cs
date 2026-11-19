using System.Buffers.Binary;

namespace BetterShot.Recording;

/// <summary>Sample formats a WASAPI mix format can arrive in.</summary>
internal enum PcmSampleKind { Int16, Int24, Int32, Float32 }

/// <summary>Converts interleaved device audio (any channel count) to interleaved stereo float.</summary>
internal static class PcmConverter
{
    internal static int BytesPerSample(PcmSampleKind kind) => kind switch
    {
        PcmSampleKind.Int16 => 2,
        PcmSampleKind.Int24 => 3,
        _ => 4
    };

    /// <summary>Returns the number of stereo frames written to <paramref name="destination"/> (2 floats per frame).</summary>
    internal static int ToStereoFloat(ReadOnlySpan<byte> source, PcmSampleKind kind, int channels, Span<float> destination)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        int sampleBytes = BytesPerSample(kind);
        int frameBytes = sampleBytes * channels;
        int frames = Math.Min(source.Length / frameBytes, destination.Length / 2);
        for (int frame = 0; frame < frames; frame++)
        {
            ReadOnlySpan<byte> frameData = source.Slice(frame * frameBytes, frameBytes);
            float left = ReadSample(frameData, kind);
            float right = channels > 1 ? ReadSample(frameData[sampleBytes..], kind) : left;
            destination[frame * 2] = left;
            destination[frame * 2 + 1] = right;
        }
        return frames;
    }

    private static float ReadSample(ReadOnlySpan<byte> data, PcmSampleKind kind) => kind switch
    {
        PcmSampleKind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(data) / 32768f,
        PcmSampleKind.Int24 => ((data[0] | (data[1] << 8) | (data[2] << 16)) << 8 >> 8) / 8388608f,
        PcmSampleKind.Int32 => BinaryPrimitives.ReadInt32LittleEndian(data) / 2147483648f,
        _ => BinaryPrimitives.ReadSingleLittleEndian(data)
    };

    /// <summary>Clamps and converts interleaved float samples to 16-bit little-endian PCM.</summary>
    internal static void ToInt16(ReadOnlySpan<float> source, Span<byte> destination)
    {
        for (int index = 0; index < source.Length; index++)
        {
            float clamped = Math.Clamp(source[index], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(destination[(index * 2)..], (short)Math.Round(clamped * 32767f));
        }
    }
}

/// <summary>
/// Streaming stereo linear-interpolation resampler. Keeps the last input frame between calls so chunk boundaries
/// are seamless. Devices almost always run at 48 kHz already; this covers 44.1 kHz and other mix rates.
/// </summary>
internal sealed class LinearResampler
{
    private readonly double _step;
    private double _position = -1;
    private float _lastLeft, _lastRight;
    private bool _hasLast;

    internal LinearResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        _step = inputRate / (double)outputRate;
        IsPassthrough = inputRate == outputRate;
    }

    internal bool IsPassthrough { get; }

    /// <summary>Upper bound of output frames for a given input frame count.</summary>
    internal int MaxOutputFrames(int inputFrames) => (int)Math.Ceiling((inputFrames + 2) / _step) + 2;

    /// <summary>Resamples interleaved stereo frames and returns how many output frames were written.</summary>
    internal int Process(ReadOnlySpan<float> input, int inputFrames, Span<float> output)
    {
        if (inputFrames <= 0) return 0;
        if (output.Length / 2 < (IsPassthrough ? inputFrames : MaxOutputFrames(inputFrames)))
            throw new ArgumentException("The output buffer is too small for this input.", nameof(output));
        if (IsPassthrough)
        {
            input[..(inputFrames * 2)].CopyTo(output);
            return inputFrames;
        }
        if (!_hasLast)
        {
            _lastLeft = input[0]; _lastRight = input[1];
            _hasLast = true;
        }
        int written = 0;
        while (_position <= inputFrames - 1)
        {
            int index = (int)Math.Floor(_position);
            double fraction = _position - index;
            float leftA = index < 0 ? _lastLeft : input[index * 2], rightA = index < 0 ? _lastRight : input[index * 2 + 1];
            float leftB = input[(index + 1) * 2], rightB = input[(index + 1) * 2 + 1];
            output[written * 2] = (float)(leftA + (leftB - leftA) * fraction);
            output[written * 2 + 1] = (float)(rightA + (rightB - rightA) * fraction);
            written++;
            _position += _step;
        }
        _position -= inputFrames;
        _lastLeft = input[(inputFrames - 1) * 2];
        _lastRight = input[(inputFrames - 1) * 2 + 1];
        return written;
    }
}

/// <summary>Thread-safe FIFO of interleaved float samples with a fixed capacity (oldest data is dropped on overflow).</summary>
internal sealed class FloatRingBuffer
{
    private readonly object _gate = new();
    private readonly float[] _buffer;
    private int _start, _count;

    internal FloatRingBuffer(int capacity) => _buffer = new float[Math.Max(2, capacity)];

    internal int Count { get { lock (_gate) return _count; } }

    internal void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (samples.Length >= _buffer.Length) samples = samples[^(_buffer.Length - (_buffer.Length & 1))..];
            int overflow = _count + samples.Length - _buffer.Length;
            if (overflow > 0) DropUnsafe(overflow + (overflow & 1));
            foreach (float sample in samples)
            {
                _buffer[(_start + _count) % _buffer.Length] = sample;
                _count++;
            }
        }
    }

    internal int Read(Span<float> destination)
    {
        lock (_gate)
        {
            int read = Math.Min(destination.Length, _count);
            for (int index = 0; index < read; index++) destination[index] = _buffer[(_start + index) % _buffer.Length];
            DropUnsafe(read);
            return read;
        }
    }

    /// <summary>Drops the oldest samples so at most <paramref name="keep"/> remain.</summary>
    internal void TrimTo(int keep)
    {
        lock (_gate) if (_count > keep) DropUnsafe(_count - Math.Max(0, keep));
    }

    internal void Clear()
    {
        lock (_gate) { _start = 0; _count = 0; }
    }

    private void DropUnsafe(int samples)
    {
        int dropped = Math.Min(samples, _count);
        _start = (_start + dropped) % _buffer.Length;
        _count -= dropped;
    }
}
