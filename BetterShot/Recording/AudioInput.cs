using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BetterShot.Recording;

/// <summary>
/// One WASAPI capture stream (microphone, or system audio through render-device loopback) converted to 48 kHz
/// stereo float and buffered for <see cref="AudioMixer"/>. Capture callbacks arrive on NAudio's capture thread.
/// </summary>
internal sealed class AudioInput : IDisposable
{
    internal const int OutputRate = 48_000;
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private readonly WasapiCapture _capture;
    private readonly MMDevice _device;
    private readonly FloatRingBuffer _buffer = new(OutputRate * 2 * 4); // Four seconds of stereo audio.
    private readonly PcmSampleKind _kind;
    private readonly int _channels;
    private readonly LinearResampler _resampler;
    private float[] _stereo = [];
    private float[] _resampled = [];
    private bool _disposed;

    private AudioInput(MMDevice device, WasapiCapture capture, bool isMicrophone)
    {
        _device = device;
        _capture = capture;
        IsMicrophone = isMicrophone;
        WaveFormat format = capture.WaveFormat;
        _kind = SampleKind(format);
        _channels = Math.Max(1, format.Channels);
        _resampler = new LinearResampler(format.SampleRate, OutputRate);
        _capture.DataAvailable += Capture_DataAvailable;
        _capture.RecordingStopped += Capture_RecordingStopped;
    }

    internal bool IsMicrophone { get; }
    internal string DeviceName { get; private set; } = string.Empty;
    /// <summary>Set when the device stopped unexpectedly (unplugged, privacy toggled, driver error).</summary>
    internal Exception? Failure { get; private set; }

    /// <summary>Stereo float samples currently buffered.</summary>
    internal int BufferedSamples => _buffer.Count;

    /// <summary>Opens the microphone (empty id = Windows default recording device). Throws <see cref="RecordingException"/>.</summary>
    internal static AudioInput OpenMicrophone(string deviceId)
    {
        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                : enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active) throw new InvalidOperationException("The microphone is disconnected or disabled.");
            return Create(device, new WasapiCapture(device, true, 50), isMicrophone: true);
        }
        catch (Exception error) when (error is not RecordingException)
        {
            device?.Dispose();
            throw RecordingException.ForMicrophone(error);
        }
    }

    /// <summary>Opens loopback capture of the default playback device. Throws <see cref="RecordingException"/>.</summary>
    internal static AudioInput OpenSystemAudio()
    {
        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return Create(device, new WasapiLoopbackCapture(device), isMicrophone: false);
        }
        catch (Exception error)
        {
            device?.Dispose();
            throw new RecordingException(RecordingProblem.SystemAudio, "System audio could not be recorded. Check that a playback device is enabled in Windows sound settings.", error);
        }
    }

    private static AudioInput Create(MMDevice device, WasapiCapture capture, bool isMicrophone)
    {
        try { return new AudioInput(device, capture, isMicrophone) { DeviceName = SafeName(device) }; }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    internal void Start()
    {
        try { _capture.StartRecording(); }
        catch (Exception error)
        {
            throw IsMicrophone
                ? RecordingException.ForMicrophone(error)
                : new RecordingException(RecordingProblem.SystemAudio, "System audio could not be recorded.", error);
        }
    }

    /// <summary>Reads up to <paramref name="destination"/>.Length interleaved samples; returns how many were read.</summary>
    internal int Read(Span<float> destination) => _buffer.Read(destination);

    internal void Discard() => _buffer.Clear();

    /// <summary>Bounds latency after a stall: keeps only the newest <paramref name="samples"/> samples.</summary>
    internal void TrimTo(int samples) => _buffer.TrimTo(samples);

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed || e.BytesRecorded <= 0) return;
        int frames = e.BytesRecorded / (PcmConverter.BytesPerSample(_kind) * _channels);
        if (frames <= 0) return;
        if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
        int converted = PcmConverter.ToStereoFloat(e.Buffer.AsSpan(0, e.BytesRecorded), _kind, _channels, _stereo);
        if (_resampler.IsPassthrough)
        {
            _buffer.Write(_stereo.AsSpan(0, converted * 2));
            return;
        }
        int capacity = _resampler.MaxOutputFrames(converted) * 2;
        if (_resampled.Length < capacity) _resampled = new float[capacity];
        int written = _resampler.Process(_stereo, converted, _resampled);
        _buffer.Write(_resampled.AsSpan(0, written * 2));
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Failure = e.Exception;
    }

    private static PcmSampleKind SampleKind(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            (format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubFormat);
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm ||
            (format is WaveFormatExtensible pcm && pcm.SubFormat == PcmSubFormat);
        if (isFloat && format.BitsPerSample == 32) return PcmSampleKind.Float32;
        if (isPcm) return format.BitsPerSample switch
        {
            16 => PcmSampleKind.Int16,
            24 => PcmSampleKind.Int24,
            32 => PcmSampleKind.Int32,
            _ => throw new NotSupportedException($"{format.BitsPerSample}-bit audio devices are not supported.")
        };
        throw new NotSupportedException($"The audio device format ({format.Encoding}) is not supported.");
    }

    private static string SafeName(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch { return string.Empty; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _capture.StopRecording(); } catch { } // Already stopped or the device vanished; nothing left to release here.
        _capture.DataAvailable -= Capture_DataAvailable;
        _capture.RecordingStopped -= Capture_RecordingStopped;
        _capture.Dispose();
        _device.Dispose();
    }
}
