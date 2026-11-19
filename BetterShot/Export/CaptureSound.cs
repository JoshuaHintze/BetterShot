using System.IO;
using System.Media;

namespace BetterShot.Export;

/// <summary>
/// A short, quiet shutter click synthesized at runtime (two damped noise bursts), so no
/// third-party or copyrighted audio asset is shipped. Played asynchronously through winmm.
/// </summary>
internal static class CaptureSound
{
    internal const int SampleRate = 44100;
    private static readonly object Gate = new();
    private static SoundPlayer? _player;

    internal static void Play()
    {
        try
        {
            lock (Gate)
            {
                if (_player is null)
                {
                    _player = new SoundPlayer(new MemoryStream(CreateShutterWave(), writable: false));
                    _player.Load();
                }
                _player.Play();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            // Audio devices can be missing or busy; a capture must never fail because of its sound.
        }
    }

    /// <summary>Builds a 16-bit mono PCM WAV of roughly 110 ms.</summary>
    internal static byte[] CreateShutterWave()
    {
        const double durationSeconds = 0.11;
        int sampleCount = (int)(SampleRate * durationSeconds);
        var samples = new short[sampleCount];
        var noise = new Random(1729);
        double lowPassed = 0;
        for (int index = 0; index < sampleCount; index++)
        {
            double time = (double)index / SampleRate;
            double white = noise.NextDouble() * 2 - 1;
            lowPassed += 0.35 * (white - lowPassed);
            double envelope = Burst(time, 0.000, 70) + 0.75 * Burst(time, 0.055, 90);
            double tone = Math.Sin(2 * Math.PI * 1850 * time) * 0.18 * Burst(time, 0.000, 120);
            double value = Math.Clamp((lowPassed * envelope + tone) * 0.42, -1, 1);
            samples[index] = (short)Math.Round(value * short.MaxValue);
        }
        return EncodeWave(samples);
    }

    private static double Burst(double time, double start, double decay) =>
        time < start ? 0 : Math.Exp(-(time - start) * decay);

    private static byte[] EncodeWave(short[] samples)
    {
        const short channels = 1, bitsPerSample = 16;
        int dataLength = samples.Length * sizeof(short);
        using var stream = new MemoryStream(44 + dataLength);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + dataLength);
            writer.Write("WAVE"u8.ToArray());
            writer.Write("fmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(SampleRate);
            writer.Write(SampleRate * channels * bitsPerSample / 8);
            writer.Write((short)(channels * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write("data"u8.ToArray());
            writer.Write(dataLength);
            foreach (short sample in samples) writer.Write(sample);
        }
        return stream.ToArray();
    }
}
