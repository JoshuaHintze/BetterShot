using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Graphics.Capture;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace BetterShot.Recording;

/// <summary>Per-recording choices, snapshotted from Settings when recording starts.</summary>
internal sealed record RecordingSessionOptions(int FramesPerSecond, bool Microphone, string MicrophoneId, bool SystemAudio, bool Camera, string CameraId);

/// <summary>Result of a finished recording. Files are still at their temporary paths.</summary>
internal sealed record RecordingOutcome(string MoviePath, string? CameraPath, TimeSpan Duration, int Width, int Height, long Frames, string? Warning);

/// <summary>
/// One screen recording: Windows.Graphics.Capture frames + WASAPI audio encoded to an MP4 at a temporary path, with an
/// optional webcam sidecar. Start, pause/resume, and stop are safe to call from the UI thread; heavy work runs on
/// capture, audio, and Media Foundation threads.
/// </summary>
internal sealed class ScreenRecorder
{
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(60);
    private readonly ScreenCaptureSource _video;
    private readonly AudioMixer? _audio;
    private readonly CameraRecorder? _camera;
    private readonly RecordingEncoder _encoder;
    private readonly Stream _output;
    private readonly Task _transcode;
    private readonly string _moviePath;
    private int _stopped;

    private ScreenRecorder(ScreenCaptureSource video, AudioMixer? audio, CameraRecorder? camera, RecordingEncoder encoder, Stream output, Task transcode, string moviePath, RecordingClock clock)
    {
        _video = video;
        _audio = audio;
        _camera = camera;
        _encoder = encoder;
        _output = output;
        _transcode = transcode;
        _moviePath = moviePath;
        Clock = clock;
        _video.SourceClosed += (_, _) => SourceClosed?.Invoke(this, EventArgs.Empty);
    }

    internal RecordingClock Clock { get; }
    internal int Width => _video.Width;
    internal int Height => _video.Height;
    internal bool HasMicrophone => _audio?.HasMicrophone == true;
    internal bool HasCamera => _camera is not null;

    /// <summary>Raised on a background thread when the recorded window closes or the display goes away.</summary>
    internal event EventHandler? SourceClosed;

    /// <summary>First failure from capture, audio, or the encoder while recording; the owner should stop and report it.</summary>
    internal Exception? Failure =>
        _video.Failure ?? _encoder.Failure ?? _audio?.Failure ?? (_transcode.IsFaulted ? _transcode.Exception?.GetBaseException() : null);

    internal bool MicrophoneMuted
    {
        get => _audio?.MicrophoneMuted == true;
        set { if (_audio is not null) _audio.MicrophoneMuted = value; }
    }

    internal static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch (Exception) { return false; } // Missing WinRT class on very old or stripped Windows builds.
        }
    }

    /// <summary>
    /// Starts everything and returns once frames are flowing. On failure every resource is released, the temporary
    /// files are removed, and a <see cref="RecordingException"/> is thrown.
    /// </summary>
    internal static async Task<ScreenRecorder> StartAsync(RecordingTarget target, RecordingSessionOptions options, string moviePath, string? cameraPath, RecordingEventLog events)
    {
        if (!IsSupported) throw new RecordingException(RecordingProblem.Unsupported, "Screen recording needs Windows 10 version 2004 or later with graphics capture available.");
        ScreenCaptureSource? video = null;
        AudioMixer? audio = null;
        CameraRecorder? camera = null;
        Stream? output = null;
        TimerResolution.Begin();
        try
        {
            GraphicsCaptureItem item = CreateItem(target);
            (int width, int height, Int32Rect? crop) = OutputFormat(target, item);
            video = new ScreenCaptureSource(item, crop, width, height);
            var clock = new RecordingClock();
            audio = OpenAudio(options, clock, moviePath);
            audio?.StartInputs();
            video.Start();
            if (options.Camera && cameraPath is not null) camera = await CameraRecorder.StartAsync(options.CameraId, cameraPath);
            TimeSpan cameraStartedAt = clock.Now;

            Directory.CreateDirectory(Path.GetDirectoryName(moviePath)!);
            output = new FileStream(moviePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            var encoder = new RecordingEncoder(video, audio, clock, options.FramesPerSecond);
            PrepareTranscodeResult prepared = await encoder.PrepareAsync(output.AsRandomAccessStream());
            await video.WaitForFirstFrameAsync(TimeSpan.FromSeconds(2));

            clock.Start();
            audio?.BeginTimeline();
            if (camera is not null) events.AddCamera(cameraStartedAt - clock.StartedAt, "start");
            Task transcode = Task.Run(async () => await prepared.TranscodeAsync());
            return new ScreenRecorder(video, audio, camera, encoder, output, transcode, moviePath, clock);
        }
        catch (Exception error)
        {
            if (camera is not null) await camera.DisposeAsync();
            audio?.Dispose();
            video?.Dispose();
            output?.Dispose();
            TimerResolution.End();
            TryDelete(moviePath);
            if (cameraPath is not null) TryDelete(cameraPath);
            if (error is RecordingException) throw;
            throw new RecordingException(RecordingProblem.Failed, FriendlyStartError(error), error);
        }
    }

    internal async Task<bool> PauseAsync()
    {
        if (!Clock.Pause()) return false;
        await SetCameraPausedAsync(true);
        return true;
    }

    internal async Task<bool> ResumeAsync()
    {
        if (!Clock.Resume()) return false;
        await SetCameraPausedAsync(false);
        return true;
    }

    /// <summary>
    /// Finalizes the MP4 and the camera sidecar. The movie stays at its temporary path even on failure, so the caller
    /// can tell the user where it is. Throws <see cref="RecordingException"/> (CouldNotFinish) when finalizing fails.
    /// </summary>
    internal async Task<RecordingOutcome> StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) throw new InvalidOperationException("The recording was already stopped.");
        Clock.Stop();
        _audio?.Finish();
        _encoder.RequestStop();
        Exception? finishError = null;
        try
        {
            if (await Task.WhenAny(_transcode, Task.Delay(FinishTimeout)) != _transcode)
                finishError = new TimeoutException("The video encoder did not finish in time.");
            else await _transcode;
        }
        catch (Exception error) { finishError = error; }

        string? warning = null;
        string? cameraPath = null;
        if (_camera is not null)
        {
            try { await _camera.StopAsync(); cameraPath = _camera.OutputPath; }
            catch (Exception error) { warning = $"The camera recording could not be finished: {error.Message}"; }
            await _camera.DisposeAsync();
        }
        _audio?.Dispose();
        _video.Dispose();
        try { await _output.FlushAsync(); }
        catch (Exception error) { finishError ??= error; }
        await _output.DisposeAsync();
        TimerResolution.End();

        string kept = File.Exists(_moviePath) ? $" The unfinished movie was kept at {_moviePath}." : string.Empty;
        if (finishError is not null)
            throw new RecordingException(RecordingProblem.CouldNotFinish, $"{RecordingException.CouldNotFinishTitle}: {finishError.Message}.{kept}", finishError);
        if (_encoder.FramesWritten == 0)
            throw new RecordingException(RecordingProblem.CouldNotFinish, $"{RecordingException.CouldNotFinishTitle}: no video frames were captured.{kept}");
        // The encoder ended early but the MP4 was finalized: keep what was recorded and say why it is short.
        if (_encoder.Failure is Exception encoderError) warning ??= $"Recording stopped early: {encoderError.Message}";
        warning ??= _audio?.TrackWarning;
        return new RecordingOutcome(_moviePath, cameraPath, Clock.Elapsed, Width, Height, _encoder.FramesWritten, warning);
    }

    private async Task SetCameraPausedAsync(bool paused)
    {
        if (_camera is null) return;
        try { await _camera.SetPausedAsync(paused); }
        catch (Exception) { } // Camera pause is best effort; the "camera" events still describe the timeline.
    }

    private static GraphicsCaptureItem CreateItem(RecordingTarget target)
    {
        try
        {
            return target.Kind == RecordingTargetKind.Window
                ? Direct3D11Interop.CreateItemForWindow(target.Window)
                : Direct3D11Interop.CreateItemForMonitor(target.Monitor);
        }
        catch (Exception error)
        {
            throw new RecordingException(RecordingProblem.Failed, target.Kind == RecordingTargetKind.Window
                ? "That window can't be recorded. It may have closed or be protected by its app."
                : "That display can't be recorded right now.", error);
        }
    }

    private static (int Width, int Height, Int32Rect? Crop) OutputFormat(RecordingTarget target, GraphicsCaptureItem item)
    {
        (int width, int height, Int32Rect? crop) = target.Kind switch
        {
            RecordingTargetKind.Region when target.Crop is Int32Rect cropBox => (cropBox.Width, cropBox.Height, (Int32Rect?)cropBox),
            RecordingTargetKind.Window => (RecordingGeometry.EvenDown(item.Size.Width), RecordingGeometry.EvenDown(item.Size.Height), null),
            _ => (RecordingGeometry.EvenDown(target.MonitorBounds.Width), RecordingGeometry.EvenDown(target.MonitorBounds.Height), null)
        };
        if (width < RecordingGeometry.MinimumDimension || height < RecordingGeometry.MinimumDimension)
            throw new RecordingException(RecordingProblem.Failed, "The selected area is too small to record.");
        if (RecordingGeometry.ExceedsEncoderLimits(width, height))
            throw new RecordingException(RecordingProblem.Failed, $"Recordings can be at most {RecordingGeometry.MaximumEncoderDimension} pixels wide or tall. Select a smaller region.");
        return (width, height, crop);
    }

    /// <summary>Opens the chosen inputs; the mixer also keeps each one as a hidden WAV track beside <paramref name="moviePath"/>.</summary>
    private static AudioMixer? OpenAudio(RecordingSessionOptions options, RecordingClock clock, string moviePath)
    {
        var inputs = new List<IAudioSource>();
        try
        {
            if (options.Microphone) inputs.Add(AudioInput.OpenMicrophone(options.MicrophoneId));
            if (options.SystemAudio) inputs.Add(AudioInput.OpenSystemAudio());
        }
        catch
        {
            foreach (IAudioSource input in inputs) input.Dispose();
            throw;
        }
        return inputs.Count == 0 ? null : new AudioMixer(inputs, clock, moviePath);
    }

    private static string FriendlyStartError(Exception error) => error switch
    {
        IOException => $"{RecordingException.FailedTitle}: the recording file could not be created ({error.Message}).",
        COMException com => $"{RecordingException.FailedTitle}: Windows graphics capture reported 0x{com.ErrorCode:X8}.",
        _ => $"{RecordingException.FailedTitle}: {error.Message}"
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Raises the Windows timer resolution to 1 ms while recording so 60 fps frame pacing is accurate.</summary>
internal static class TimerResolution
{
    internal static void Begin() => _ = TimeBeginPeriod(1);
    internal static void End() => _ = TimeEndPeriod(1);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);
}
