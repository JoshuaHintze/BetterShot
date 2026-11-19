using System.IO;
using System.Threading;

namespace BetterShot.Recording;

/// <summary>
/// Mixes the microphone and system-audio inputs into one 48 kHz stereo 16-bit PCM timeline driven by the recording
/// clock, not by the devices: silent loopback devices deliver no packets at all, so gaps are filled with silence and
/// the audio track always has exactly as much audio as the video has time. A small fixed latency absorbs device
/// jitter. Output is pulled by the encoder through <see cref="TryRead"/>.
/// When given a movie path, each input is also written losslessly to its own hidden WAV sidecar
/// (<see cref="RecordingAudioTracks"/>) from the very same samples, so the separate tracks line up with the mix exactly.
/// </summary>
internal sealed class AudioMixer : IDisposable
{
    internal const int SampleRate = AudioInput.OutputRate;
    internal const int Channels = 2;
    internal const int BytesPerFrame = Channels * 2;
    private static readonly TimeSpan MixLatency = TimeSpan.FromMilliseconds(120);
    private static readonly int MaximumBacklogSamples = SampleRate * Channels * 3 / 10; // 300 ms
    private static readonly int SteadyBacklogSamples = (int)(SampleRate * Channels * MixLatency.TotalSeconds);
    private readonly IReadOnlyList<IAudioSource> _inputs;
    private readonly PcmWaveWriter?[] _trackWriters;
    private readonly RecordingClock _clock;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _output = new();
    private Timer? _timer;
    private long _producedFrames;
    private long _readFrames;
    private int _queuedBytes;
    private byte[]? _partial;
    private int _partialOffset;
    private bool _flushedForPause;
    private bool _finished;
    private float[] _mix = [];
    private float[] _scratch = [];

    internal AudioMixer(IReadOnlyList<IAudioSource> inputs, RecordingClock clock, string? trackSidecarMoviePath = null)
    {
        _inputs = inputs;
        _clock = clock;
        _trackWriters = new PcmWaveWriter?[inputs.Count];
        if (trackSidecarMoviePath is not null) OpenTrackWriters(trackSidecarMoviePath);
    }

    /// <summary>Set when a separate track could not be written; the mixed soundtrack is unaffected.</summary>
    internal string? TrackWarning { get; private set; }

    /// <summary>Paths of the separate track files being written (completed by <see cref="Finish"/>).</summary>
    internal IReadOnlyList<string> TrackPaths => _trackWriters.OfType<PcmWaveWriter>().Select(writer => writer.FilePath).ToArray();

    private void OpenTrackWriters(string moviePath)
    {
        for (int index = 0; index < _inputs.Count; index++)
        {
            AudioTrackKind kind = _inputs[index].IsMicrophone ? AudioTrackKind.Microphone : AudioTrackKind.System;
            string path = RecordingAudioTracks.SidecarPath(moviePath, kind);
            if (_trackWriters.Any(writer => writer?.FilePath == path)) continue; // One file per kind.
            try { _trackWriters[index] = new PcmWaveWriter(path, SampleRate, Channels); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                TrackWarning ??= $"The separate audio tracks could not be saved ({error.Message}); the movie keeps the mixed audio.";
            }
        }
    }

    internal bool MicrophoneMuted { get; set; }
    internal bool HasMicrophone => _inputs.Any(input => input.IsMicrophone);
    internal Exception? Failure { get; private set; }

    /// <summary>True once <see cref="Finish"/> ran and every produced sample has been read.</summary>
    internal bool IsDrained { get { lock (_gate) return _finished && _queuedBytes == 0; } }

    internal void StartInputs()
    {
        foreach (IAudioSource input in _inputs) input.Start();
    }

    /// <summary>Call right after the clock starts: drops pre-roll audio and begins mixing every 10 ms.</summary>
    internal void BeginTimeline()
    {
        foreach (IAudioSource input in _inputs) input.Discard();
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
    }

    /// <summary>Stops mixing and produces audio up to the (stopped) clock so the track ends with the video.</summary>
    internal void Finish()
    {
        _timer?.Dispose();
        _timer = null;
        lock (_gate)
        {
            if (_finished) return;
            ProduceUpTo(_clock.Elapsed);
            _finished = true;
            CompleteTrackWriters();
        }
    }

    private void CompleteTrackWriters()
    {
        for (int index = 0; index < _trackWriters.Length; index++)
        {
            if (_trackWriters[index] is not PcmWaveWriter writer) continue;
            try
            {
                writer.Complete();
                writer.Dispose();
                RecordingAudioTracks.Hide(writer.FilePath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                writer.Dispose();
                RecordingAudioTracks.TryDelete(writer.FilePath);
                TrackWarning ??= $"A separate audio track could not be finished ({error.Message}); the movie keeps the mixed audio.";
                _trackWriters[index] = null;
            }
        }
    }

    /// <summary>
    /// Returns the next PCM chunk (at most <paramref name="maximumFrames"/>) and its start frame, or null when fewer
    /// than <paramref name="minimumFrames"/> are ready (the minimum is ignored after <see cref="Finish"/>).
    /// </summary>
    internal (byte[] Data, long StartFrame)? TryRead(int minimumFrames, int maximumFrames)
    {
        lock (_gate)
        {
            int availableFrames = _queuedBytes / BytesPerFrame;
            if (availableFrames == 0 || (!_finished && availableFrames < minimumFrames)) return null;
            int frames = Math.Min(availableFrames, maximumFrames);
            byte[] data = new byte[frames * BytesPerFrame];
            int copied = 0;
            while (copied < data.Length)
            {
                if (_partial is null) { _partial = _output.Dequeue(); _partialOffset = 0; }
                int count = Math.Min(data.Length - copied, _partial.Length - _partialOffset);
                Buffer.BlockCopy(_partial, _partialOffset, data, copied, count);
                copied += count;
                _partialOffset += count;
                if (_partialOffset >= _partial.Length) _partial = null;
            }
            _queuedBytes -= data.Length;
            long start = _readFrames;
            _readFrames += frames;
            return (data, start);
        }
    }

    /// <summary>Runs one mixing pass synchronously (diagnostics drive the clock by hand instead of using the timer).</summary>
    internal void PumpForTest() => Tick();

    private void Tick()
    {
        try
        {
            lock (_gate)
            {
                if (_finished) return;
                if (_inputs.FirstOrDefault(input => input.Failure is not null) is { } failed && Failure is null) Failure = failed.Failure;
                if (_clock.IsPaused)
                {
                    if (!_flushedForPause) { ProduceUpTo(_clock.Elapsed); _flushedForPause = true; }
                    foreach (IAudioSource input in _inputs) input.Discard();
                    return;
                }
                _flushedForPause = false;
                foreach (IAudioSource input in _inputs)
                    if (input.BufferedSamples > MaximumBacklogSamples) input.TrimTo(SteadyBacklogSamples);
                ProduceUpTo(_clock.Elapsed - MixLatency);
            }
        }
        catch (Exception error)
        {
            Failure ??= error; // Surfaced by the recorder; a timer callback must never throw.
        }
    }

    /// <summary>Mixes inputs (zero-filling whatever is missing) until the timeline reaches <paramref name="time"/>.</summary>
    private void ProduceUpTo(TimeSpan time)
    {
        long target = (long)Math.Floor(Math.Max(0, time.TotalSeconds) * SampleRate);
        long frames = Math.Min(target - _producedFrames, SampleRate); // At most one second per tick.
        while (frames > 0)
        {
            MixFrames((int)frames);
            frames = Math.Min(target - _producedFrames, SampleRate);
        }
    }

    private void MixFrames(int frames)
    {
        int samples = frames * Channels;
        if (_mix.Length < samples) { _mix = new float[samples]; _scratch = new float[samples]; }
        Span<float> mix = _mix.AsSpan(0, samples);
        mix.Clear();
        for (int inputIndex = 0; inputIndex < _inputs.Count; inputIndex++)
        {
            IAudioSource input = _inputs[inputIndex];
            Span<float> scratch = _scratch.AsSpan(0, samples);
            int read = input.Read(scratch);
            bool muted = input.IsMicrophone && MicrophoneMuted; // Still drained so unmuting doesn't replay old audio.
            if (muted) read = 0;
            scratch[read..].Clear(); // Missing device audio is silence, on the mix and on the separate track alike.
            WriteTrack(inputIndex, scratch);
            for (int index = 0; index < read; index++) mix[index] += scratch[index];
        }
        byte[] pcm = new byte[samples * 2];
        PcmConverter.ToInt16(mix, pcm);
        _output.Enqueue(pcm);
        _queuedBytes += pcm.Length;
        _producedFrames += frames;
    }

    private void WriteTrack(int inputIndex, ReadOnlySpan<float> samples)
    {
        if (_trackWriters[inputIndex] is not PcmWaveWriter writer) return;
        try { writer.WriteFloat(samples); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A full disk must not stop the recording: drop this track and keep the mixed soundtrack.
            writer.Dispose();
            RecordingAudioTracks.TryDelete(writer.FilePath);
            _trackWriters[inputIndex] = null;
            TrackWarning ??= $"A separate audio track stopped saving ({error.Message}); the movie keeps the mixed audio.";
        }
    }

    /// <summary>Releases the inputs. Separate tracks not completed by <see cref="Finish"/> (a failed start) are deleted.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        foreach (IAudioSource input in _inputs) input.Dispose();
        lock (_gate)
        {
            if (_finished) return;
            for (int index = 0; index < _trackWriters.Length; index++)
            {
                if (_trackWriters[index] is not PcmWaveWriter writer) continue;
                writer.Dispose();
                RecordingAudioTracks.TryDelete(writer.FilePath);
                _trackWriters[index] = null;
            }
        }
    }
}
