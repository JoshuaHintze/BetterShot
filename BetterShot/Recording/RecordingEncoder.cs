using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace BetterShot.Recording;

/// <summary>
/// Feeds captured frames and mixed audio into Windows' Media Foundation H.264/AAC MP4 encoder through a
/// MediaStreamSource + MediaTranscoder (the pattern used by Microsoft's SimpleRecorder sample). Requests are paced
/// in real time by the recording clock; pausing simply stops samples from being produced.
/// </summary>
internal sealed class RecordingEncoder
{
    private const int AudioMinimumFrames = AudioMixer.SampleRate / 50;   // 20 ms
    private const int AudioMaximumFrames = AudioMixer.SampleRate / 10;   // 100 ms
    private static readonly TimeSpan FirstFrameGrace = TimeSpan.FromSeconds(2);
    private readonly ScreenCaptureSource _video;
    private readonly AudioMixer? _audio;
    private readonly RecordingClock _clock;
    private readonly TimeSpan _frameInterval;
    private readonly VideoStreamDescriptor _videoDescriptor;
    private readonly AudioStreamDescriptor? _audioDescriptor;
    private readonly MediaStreamSource _source;
    private TimeSpan _nextVideoTime;
    private TimeSpan _lastVideoTime = TimeSpan.FromTicks(-1);
    private volatile bool _stopRequested;

    internal RecordingEncoder(ScreenCaptureSource video, AudioMixer? audio, RecordingClock clock, int framesPerSecond)
    {
        _video = video;
        _audio = audio;
        _clock = clock;
        FramesPerSecond = framesPerSecond;
        _frameInterval = TimeSpan.FromSeconds(1.0 / framesPerSecond);
        var videoProperties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)video.Width, (uint)video.Height);
        _videoDescriptor = new VideoStreamDescriptor(videoProperties);
        if (audio is not null)
        {
            _audioDescriptor = new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(AudioMixer.SampleRate, AudioMixer.Channels, 16));
            _source = new MediaStreamSource(_videoDescriptor, _audioDescriptor);
        }
        else _source = new MediaStreamSource(_videoDescriptor);
        _source.BufferTime = TimeSpan.Zero;
        _source.CanSeek = false;
        _source.Starting += Source_Starting;
        _source.SampleRequested += Source_SampleRequested;
    }

    internal int FramesPerSecond { get; }
    internal long FramesWritten { get; private set; }
    internal Exception? Failure { get; private set; }

    internal MediaEncodingProfile CreateProfile()
    {
        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = (uint)_video.Width;
        profile.Video.Height = (uint)_video.Height;
        profile.Video.Bitrate = RecordingGeometry.SuggestBitrate(_video.Width, _video.Height, FramesPerSecond);
        profile.Video.FrameRate.Numerator = (uint)FramesPerSecond;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Audio = _audio is null ? null : AudioEncodingProperties.CreateAac(AudioMixer.SampleRate, AudioMixer.Channels, 192_000);
        profile.Container = new ContainerEncodingProperties { Subtype = MediaEncodingSubtypes.Mpeg4 };
        return profile;
    }

    /// <summary>Prepares the transcode. Throws <see cref="RecordingException"/> when Windows cannot encode this configuration.</summary>
    internal async Task<PrepareTranscodeResult> PrepareAsync(IRandomAccessStream destination)
    {
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = true };
        PrepareTranscodeResult prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(_source, destination, CreateProfile());
        if (!prepared.CanTranscode)
            throw new RecordingException(RecordingProblem.Failed, $"Windows could not start the video encoder ({prepared.FailureReason}).");
        return prepared;
    }

    /// <summary>Ends both streams; pending requests complete with end-of-stream so the transcode can finalize the MP4.</summary>
    internal void RequestStop() => _stopRequested = true;

    private void Source_Starting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args) =>
        args.Request.SetActualStartPosition(TimeSpan.Zero);

    private async void Source_SampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        MediaStreamSourceSampleRequest request = args.Request;
        MediaStreamSourceSampleRequestDeferral deferral = request.GetDeferral();
        try
        {
            request.Sample = ReferenceEquals(request.StreamDescriptor, _videoDescriptor) || request.StreamDescriptor is VideoStreamDescriptor
                ? await NextVideoSampleAsync().ConfigureAwait(false)
                : await NextAudioSampleAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Failure ??= error;
            _stopRequested = true;
            request.Sample = null; // End of stream: the transcode finishes and the partial movie stays playable.
        }
        finally { deferral.Complete(); }
    }

    private async Task<MediaStreamSample?> NextVideoSampleAsync()
    {
        while (true)
        {
            if (_stopRequested) return null;
            TimeSpan now = _clock.Elapsed;
            bool ready = !_clock.IsPaused && now >= _nextVideoTime && (_video.HasFrame || now >= FirstFrameGrace);
            if (ready) break;
            TimeSpan wait = _clock.IsPaused ? TimeSpan.FromMilliseconds(20) : _nextVideoTime - now;
            await Task.Delay(wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait > TimeSpan.FromMilliseconds(20) ? TimeSpan.FromMilliseconds(20) : wait).ConfigureAwait(false);
        }
        TimeSpan timestamp = _clock.Elapsed;
        if (timestamp <= _lastVideoTime) timestamp = _lastVideoTime + TimeSpan.FromTicks(1);
        _lastVideoTime = timestamp;
        // Schedule against the ideal grid, but never fall more than one frame behind after a stall.
        _nextVideoTime = _nextVideoTime + _frameInterval < timestamp ? timestamp : _nextVideoTime + _frameInterval;
        IDirect3DSurface surface = _video.SnapshotFrame();
        MediaStreamSample sample = MediaStreamSample.CreateFromDirect3D11Surface(surface, timestamp);
        sample.Duration = _frameInterval;
        sample.Processed += (_, _) => surface.Dispose();
        FramesWritten++;
        return sample;
    }

    private async Task<MediaStreamSample?> NextAudioSampleAsync()
    {
        AudioMixer audio = _audio ?? throw new InvalidOperationException("Audio was requested from a video-only recording.");
        while (true)
        {
            if (audio.TryRead(AudioMinimumFrames, AudioMaximumFrames) is (byte[] data, long startFrame))
            {
                MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(data.AsBuffer(), TimeSpan.FromSeconds(startFrame / (double)AudioMixer.SampleRate));
                sample.Duration = TimeSpan.FromSeconds(data.Length / (double)(AudioMixer.BytesPerFrame * AudioMixer.SampleRate));
                return sample;
            }
            if (audio.IsDrained || (_stopRequested && Failure is not null)) return null;
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
