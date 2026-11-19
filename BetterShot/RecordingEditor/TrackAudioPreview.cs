using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Buffers.Binary;
using System.IO;

namespace BetterShot.RecordingEditor;

/// <summary>
/// Mixes the unmuted separate audio tracks (48 kHz stereo 16-bit WAV sidecars) from a source position, at a playback
/// rate, as 32-bit float for WASAPI. Faster or slower rates resample linearly, so pitch follows the speed.
/// Read runs on the audio thread; the editor seeks and changes rate/volume from the UI thread.
/// </summary>
internal sealed class TrackMixProvider : IWaveProvider, IDisposable
{
    private readonly List<(FileStream Stream, WaveLayout Layout)> _tracks = [];
    private readonly object _gate = new();
    private double _position;
    private double _rate = 1;
    private float _volume = 1;
    private byte[] _raw = [];
    private float[] _mixed = [];

    internal TrackMixProvider(IReadOnlyList<string> wavePaths)
    {
        try
        {
            foreach (string path in wavePaths)
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _tracks.Add((stream, WaveLayout.Parse(stream)));
                if (_tracks[^1].Layout.SampleRate != SampleRate || _tracks[^1].Layout.Channels != 2) throw new InvalidDataException("A separate audio track is not 48 kHz stereo.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal const int SampleRate = Recording.RecordingAudioTracks.SampleRate;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

    /// <summary>The source time of the next sample handed to the device (ahead of what is heard by the device latency).</summary>
    internal double PositionSeconds { get { lock (_gate) return _position / SampleRate; } }

    internal void Seek(double seconds) { lock (_gate) _position = Math.Max(0, seconds) * SampleRate; }

    internal double Rate { set { lock (_gate) _rate = Math.Clamp(value, 0.1, 8); } }

    internal float Volume { set { lock (_gate) _volume = Math.Clamp(value, 0f, 1f); } }

    public int Read(byte[] buffer, int offset, int count)
    {
        int frames = count / 8;
        lock (_gate)
        {
            long first = (long)Math.Floor(_position);
            int needed = (int)Math.Ceiling(frames * _rate) + 2;
            if (_mixed.Length < needed * 2) { _mixed = new float[needed * 2]; _raw = new byte[needed * 4]; }
            Array.Clear(_mixed, 0, needed * 2);
            foreach ((FileStream stream, WaveLayout layout) in _tracks) AddTrack(stream, layout, first, needed);
            for (int frame = 0; frame < frames; frame++)
            {
                double source = _position + frame * _rate - first;
                int index = (int)source;
                float fraction = (float)(source - index);
                for (int channel = 0; channel < 2; channel++)
                {
                    float before = _mixed[index * 2 + channel], after = _mixed[Math.Min(index + 1, needed - 1) * 2 + channel];
                    float value = Math.Clamp((before + (after - before) * fraction) * _volume, -1f, 1f);
                    BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset + (frame * 2 + channel) * 4), value);
                }
            }
            _position += frames * _rate;
        }
        return frames * 8;
    }

    private void AddTrack(FileStream stream, WaveLayout layout, long first, int frames)
    {
        long available = layout.DataLength / layout.BytesPerFrame - first;
        int readable = (int)Math.Clamp(available, 0, frames);
        if (readable <= 0) return;
        stream.Position = layout.DataOffset + first * layout.BytesPerFrame;
        stream.ReadExactly(_raw, 0, readable * 4);
        for (int sample = 0; sample < readable * 2; sample++) _mixed[sample] += BinaryPrimitives.ReadInt16LittleEndian(_raw.AsSpan(sample * 2)) / 32768f;
    }

    public void Dispose()
    {
        foreach ((FileStream stream, _) in _tracks) stream.Dispose();
        _tracks.Clear();
    }
}

/// <summary>
/// Plays the unmuted separate tracks through the default output device while the player's own (mixed) sound is muted,
/// so muting the microphone or system audio is heard in the preview. The editor keeps it within a frame or two of the
/// video by re-seeking when they drift apart.
/// </summary>
internal sealed class TrackAudioPreview : IDisposable
{
    private const int LatencyMilliseconds = 80;
    private readonly TrackMixProvider _provider;
    private WasapiOut? _output;
    private double _rate = 1;

    private TrackAudioPreview(TrackMixProvider provider) => _provider = provider;

    internal IReadOnlyList<string> Paths { get; private init; } = [];

    internal static TrackAudioPreview Open(IReadOnlyList<string> wavePaths) => new(new TrackMixProvider(wavePaths)) { Paths = wavePaths };

    internal bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    /// <summary>The source time being heard now (approximately: the device buffer is subtracted).</summary>
    internal double HeardPosition => Math.Max(0, _provider.PositionSeconds - LatencyMilliseconds / 1000.0 * _rate);

    internal void Play(double seconds, double rate, float volume)
    {
        _rate = rate;
        _provider.Rate = rate;
        _provider.Volume = volume;
        _provider.Seek(seconds);
        if (_output is null)
        {
            _output = new WasapiOut(AudioClientShareMode.Shared, true, LatencyMilliseconds);
            _output.Init(_provider);
        }
        _output.Play();
    }

    internal void Seek(double seconds) => _provider.Seek(seconds);

    internal void SetVolume(float volume) => _provider.Volume = volume;

    internal void SetRate(double rate)
    {
        _rate = rate;
        _provider.Rate = rate;
    }

    internal void Stop()
    {
        try { _output?.Stop(); }
        catch (InvalidOperationException) { } // The device went away; nothing is playing.
    }

    public void Dispose()
    {
        Stop();
        _output?.Dispose();
        _output = null;
        _provider.Dispose();
    }
}
