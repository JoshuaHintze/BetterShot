using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace BetterShot.RecordingEditor;

/// <summary>
/// A peak envelope of one audio track: the loudest absolute sample (0..1, either channel) in each fixed-length bucket.
/// Small enough to keep in memory for long recordings (200 buckets per second is 0.7 MB per hour).
/// </summary>
internal sealed class WaveformPeaks
{
    internal const int DefaultBucketsPerSecond = 200;

    internal WaveformPeaks(int bucketsPerSecond, float[] peaks)
    {
        BucketsPerSecond = Math.Max(1, bucketsPerSecond);
        Peaks = peaks;
    }

    internal int BucketsPerSecond { get; }
    internal float[] Peaks { get; }
    internal double Duration => Peaks.Length / (double)BucketsPerSecond;

    /// <summary>The loudest peak between two source times (0 outside the track).</summary>
    internal float PeakBetween(double start, double end)
    {
        int first = (int)Math.Floor(Math.Max(0, start) * BucketsPerSecond);
        int last = (int)Math.Ceiling(Math.Max(0, end) * BucketsPerSecond);
        last = Math.Min(Math.Max(last, first + 1), Peaks.Length);
        float peak = 0;
        for (int index = first; index < last; index++) peak = Math.Max(peak, Peaks[index]);
        return peak;
    }
}

/// <summary>Pure waveform math: building the peak envelope from 16-bit PCM, and the bar height drawn for a peak.</summary>
internal static class WaveformEnvelope
{
    /// <summary>Peaks quieter than this many dB below full scale draw as a dotted silence line.</summary>
    internal const double FloorDecibels = -48;

    /// <summary>Peak envelope of interleaved 16-bit little-endian PCM.</summary>
    internal static WaveformPeaks Compute(ReadOnlySpan<byte> pcm, int sampleRate, int channels, int bucketsPerSecond = WaveformPeaks.DefaultBucketsPerSecond)
    {
        var builder = new Builder(sampleRate, channels, bucketsPerSecond);
        builder.Add(pcm);
        return builder.Finish();
    }

    /// <summary>Reads a 16-bit PCM WAV file in blocks (off the UI thread) and returns its peak envelope.</summary>
    internal static WaveformPeaks FromWaveFile(string path, CancellationToken cancellation, int bucketsPerSecond = WaveformPeaks.DefaultBucketsPerSecond)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        WaveLayout layout = WaveLayout.Parse(stream);
        var builder = new Builder(layout.SampleRate, layout.Channels, bucketsPerSecond);
        stream.Position = layout.DataOffset;
        byte[] buffer = new byte[layout.BytesPerFrame * 8192];
        long remaining = layout.DataLength - layout.DataLength % layout.BytesPerFrame;
        while (remaining > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) break;
            builder.Add(buffer.AsSpan(0, read - read % layout.BytesPerFrame));
            remaining -= read;
        }
        return builder.Finish();
    }

    /// <summary>Bar height as a fraction of the lane (0 = silence), on a decibel scale so quiet speech stays visible.</summary>
    internal static double BarFraction(float peak)
    {
        if (!(peak > 0)) return 0;
        double decibels = 20 * Math.Log10(Math.Min(1, peak));
        return Math.Clamp((decibels - FloorDecibels) / -FloorDecibels, 0, 1);
    }

    private sealed class Builder
    {
        private readonly int _channels;
        private readonly double _framesPerBucket;
        private readonly int _bucketsPerSecond;
        private readonly List<float> _peaks = [];
        private long _frame;
        private float _current;

        internal Builder(int sampleRate, int channels, int bucketsPerSecond)
        {
            _channels = Math.Max(1, channels);
            _bucketsPerSecond = Math.Max(1, bucketsPerSecond);
            _framesPerBucket = Math.Max(1, sampleRate) / (double)_bucketsPerSecond;
        }

        internal void Add(ReadOnlySpan<byte> pcm)
        {
            int frames = pcm.Length / (2 * _channels);
            for (int frame = 0; frame < frames; frame++)
            {
                for (int channel = 0; channel < _channels; channel++)
                {
                    int sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[((frame * _channels + channel) * 2)..]);
                    float magnitude = Math.Abs(sample) / 32768f;
                    if (magnitude > _current) _current = magnitude;
                }
                _frame++;
                if (_frame >= (long)Math.Round((_peaks.Count + 1) * _framesPerBucket))
                {
                    _peaks.Add(_current);
                    _current = 0;
                }
            }
        }

        internal WaveformPeaks Finish()
        {
            if (_frame > (long)Math.Round(_peaks.Count * _framesPerBucket)) _peaks.Add(_current);
            return new WaveformPeaks(_bucketsPerSecond, _peaks.ToArray());
        }
    }
}
