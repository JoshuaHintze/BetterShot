using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace BetterShot.RecordingEditor;

/// <summary>Location and format of the PCM samples inside a WAV file.</summary>
internal readonly record struct WaveLayout(int SampleRate, int Channels, int BitsPerSample, long DataOffset, long DataLength)
{
    internal int BytesPerFrame => Channels * BitsPerSample / 8;

    /// <summary>Finds the "fmt " and "data" chunks of a RIFF/WAVE header (pure parsing; unknown chunks are skipped).</summary>
    internal static WaveLayout Parse(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file.");
        reader.ReadUInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");
        int rate = 0, channels = 0, bits = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            string id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            long size = reader.ReadUInt32();
            long next = stream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                ushort format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format is not 1 and not 0xFFFE) throw new InvalidDataException("Only PCM audio is supported.");
            }
            else if (id == "data")
            {
                if (rate <= 0 || channels <= 0 || bits != 16) throw new InvalidDataException("The audio format is not 16-bit PCM.");
                long available = Math.Min(size == uint.MaxValue ? stream.Length - stream.Position : size, stream.Length - stream.Position);
                return new WaveLayout(rate, channels, bits, stream.Position, available);
            }
            stream.Position = next;
        }
        throw new InvalidDataException("The WAV file has no audio data.");
    }
}

/// <summary>
/// The recording's soundtrack decoded once to a temporary 48 kHz stereo 16-bit WAV (by Windows' transcoder), or an
/// already re-mixed WAV, read back for just the kept stretches so exports keep audio exactly in step with the cut video.
/// </summary>
internal sealed class KeptAudio : IDisposable
{
    private readonly FileStream _stream;
    private readonly WaveLayout _layout;
    private readonly IReadOnlyList<KeptRange> _ranges;
    private readonly string? _temporaryPath;
    private int _rangeIndex;
    private long _frameInRange;
    private long _framesWritten;

    private KeptAudio(string? temporaryPath,FileStream stream, WaveLayout layout, IReadOnlyList<KeptRange> ranges)
    {
        _temporaryPath = temporaryPath;
        _stream = stream;
        _layout = layout;
        _ranges = ranges;
    }

    internal int SampleRate => _layout.SampleRate;
    internal int Channels => _layout.Channels;

    internal static async Task<KeptAudio> ExtractAsync(string moviePath, IReadOnlyList<KeptRange> ranges, string temporaryFolder, CancellationToken cancellation)
    {
        string wavePath = await ExtractSoundtrackAsync(moviePath, temporaryFolder, cancellation);
        try { return FromWave(wavePath, ranges, deleteOnDispose: true); }
        catch
        {
            TryDelete(wavePath);
            throw;
        }
    }

    /// <summary>Reads the kept stretches of an existing 16-bit PCM WAV; the file is deleted on dispose when asked.</summary>
    internal static KeptAudio FromWave(string wavePath, IReadOnlyList<KeptRange> ranges, bool deleteOnDispose)
    {
        var stream = new FileStream(wavePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { return new KeptAudio(deleteOnDispose ? wavePath : null, stream, WaveLayout.Parse(stream), ranges); }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>Decodes the movie's soundtrack to a temporary 48 kHz stereo 16-bit WAV and returns its path (the caller deletes it).</summary>
    internal static async Task<string> ExtractSoundtrackAsync(string moviePath, string temporaryFolder, CancellationToken cancellation)
    {
        Directory.CreateDirectory(temporaryFolder);
        string wavePath = Path.Combine(temporaryFolder, "audio-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            StorageFile source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(moviePath));
            await using (File.Create(wavePath)) { }
            StorageFile destination = await StorageFile.GetFileFromPathAsync(wavePath);
            MediaEncodingProfile profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
            profile.Audio = AudioEncodingProperties.CreatePcm(RecordingExportProfiles.AudioSampleRate, RecordingExportProfiles.AudioChannels, 16);
            var transcoder = new MediaTranscoder();
            PrepareTranscodeResult prepared = await transcoder.PrepareFileTranscodeAsync(source, destination, profile).AsTask(cancellation);
            if (!prepared.CanTranscode) throw new InvalidOperationException($"Windows could not read the recording's audio ({prepared.FailureReason}).");
            await prepared.TranscodeAsync().AsTask(cancellation);
            return wavePath;
        }
        catch
        {
            TryDelete(wavePath);
            throw;
        }
    }

    /// <summary>The next block of kept audio (up to 100 ms) and its output timestamp, or null at the end.</summary>
    internal (byte[] Data, TimeSpan Timestamp)? ReadNext()
    {
        int maximumFrames = _layout.SampleRate / 10;
        while (_rangeIndex < _ranges.Count)
        {
            KeptRange range = _ranges[_rangeIndex];
            long first = (long)Math.Round(range.Start * _layout.SampleRate), last = (long)Math.Round(range.End * _layout.SampleRate);
            long available = Math.Min(last - first - _frameInRange, maximumFrames);
            long totalFrames = _layout.DataLength / _layout.BytesPerFrame;
            available = Math.Min(available, totalFrames - (first + _frameInRange));
            if (available <= 0)
            {
                _rangeIndex++;
                _frameInRange = 0;
                continue;
            }
            byte[] data = new byte[available * _layout.BytesPerFrame];
            _stream.Position = _layout.DataOffset + (first + _frameInRange) * _layout.BytesPerFrame;
            _stream.ReadExactly(data);
            var timestamp = TimeSpan.FromSeconds(_framesWritten / (double)_layout.SampleRate);
            _frameInRange += available;
            _framesWritten += available;
            return (data, timestamp);
        }
        return null;
    }

    public void Dispose()
    {
        _stream.Dispose();
        if (_temporaryPath is not null) TryDelete(_temporaryPath);
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { } // A leftover temp WAV is harmless; the temp folder is cleaned by Windows.
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Encodes frames produced on demand (markup already burned in) to an H.264 MP4 through a MediaStreamSource and
/// MediaTranscoder, optionally muxing the kept audio. Frames are converted to NV12 here so no orientation or color
/// conversion is left to guesswork in the encoder.
/// </summary>
internal sealed class FrameMovieEncoder
{
    private readonly int _width;
    private readonly int _height;
    private readonly double _frameRate;
    private readonly int _frameCount;
    private readonly Func<int, CancellationToken, Task<DecodedFrame>> _renderFrame;
    private readonly KeptAudio? _audio;
    private readonly IProgress<double>? _progress;
    private readonly CancellationToken _cancellation;
    private readonly VideoStreamDescriptor _videoDescriptor;
    private int _nextFrame;
    private Exception? _failure;

    private FrameMovieEncoder(int width, int height, double frameRate, int frameCount, Func<int, CancellationToken, Task<DecodedFrame>> renderFrame,
        KeptAudio? audio, IProgress<double>? progress, CancellationToken cancellation)
    {
        _width = width;
        _height = height;
        _frameRate = frameRate;
        _frameCount = frameCount;
        _renderFrame = renderFrame;
        _audio = audio;
        _progress = progress;
        _cancellation = cancellation;
        _videoDescriptor = new VideoStreamDescriptor(VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Nv12, (uint)width, (uint)height));
    }

    /// <param name="renderFrame">Returns frame <c>index</c> as BGRA at exactly <paramref name="width"/> x <paramref name="height"/>.</param>
    internal static async Task EncodeAsync(string outputPath, int width, int height, double frameRate, int frameCount,
        Func<int, CancellationToken, Task<DecodedFrame>> renderFrame, KeptAudio? audio, IProgress<double>? progress, CancellationToken cancellation)
    {
        (int evenWidth, int evenHeight) = ExportPlanning.Even(width, height);
        var encoder = new FrameMovieEncoder(evenWidth, evenHeight, frameRate, frameCount, renderFrame, audio, progress, cancellation);
        await encoder.RunAsync(outputPath);
    }

    private async Task RunAsync(string outputPath)
    {
        MediaStreamSource source;
        if (_audio is not null)
        {
            var audioDescriptor = new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm((uint)_audio.SampleRate, (uint)_audio.Channels, 16));
            source = new MediaStreamSource(_videoDescriptor, audioDescriptor);
        }
        else source = new MediaStreamSource(_videoDescriptor);
        source.BufferTime = TimeSpan.Zero;
        source.CanSeek = false;
        source.Duration = TimeSpan.FromSeconds(_frameCount / _frameRate);
        source.Starting += (_, args) => args.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += Source_SampleRequested;

        MediaEncodingProfile profile = RecordingExportProfiles.Mp4(_width, _height, _frameRate, _audio is not null);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = true };
        PrepareTranscodeResult prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, output.AsRandomAccessStream(), profile).AsTask(_cancellation);
        if (!prepared.CanTranscode) throw new InvalidOperationException($"Windows could not start the video encoder ({prepared.FailureReason}).");
        await prepared.TranscodeAsync().AsTask(_cancellation);
        _cancellation.ThrowIfCancellationRequested();
        if (_failure is not null) throw new InvalidOperationException($"A frame could not be prepared: {_failure.Message}", _failure);
    }

    private async void Source_SampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        MediaStreamSourceSampleRequest request = args.Request;
        MediaStreamSourceSampleRequestDeferral deferral = request.GetDeferral();
        try
        {
            request.Sample = request.StreamDescriptor is VideoStreamDescriptor ? await NextVideoSampleAsync() : NextAudioSample();
        }
        catch (Exception error)
        {
            // Ending the stream lets the transcode finish; RunAsync then reports the failure or cancellation.
            if (error is not OperationCanceledException) _failure ??= error;
            request.Sample = null;
        }
        finally { deferral.Complete(); }
    }

    private async Task<MediaStreamSample?> NextVideoSampleAsync()
    {
        if (_failure is not null || _cancellation.IsCancellationRequested) return null;
        int index = Interlocked.Increment(ref _nextFrame) - 1;
        if (index >= _frameCount) return null;
        DecodedFrame frame = await _renderFrame(index, _cancellation).ConfigureAwait(false);
        if (frame.Width < _width || frame.Height < _height) throw new InvalidOperationException("A rendered frame has the wrong size.");
        byte[] nv12 = new byte[Nv12Converter.BufferSize(_width, _height)];
        Nv12Converter.Convert(frame.Bgra, _width, _height, frame.Stride, nv12, bt709: _height >= 720);
        var timestamp = TimeSpan.FromSeconds(index / _frameRate);
        MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(nv12.AsBuffer(), timestamp);
        sample.Duration = TimeSpan.FromSeconds(1 / _frameRate);
        sample.KeyFrame = index == 0;
        _progress?.Report((index + 1) / (double)_frameCount);
        return sample;
    }

    private MediaStreamSample? NextAudioSample()
    {
        if (_audio is null || _failure is not null || _cancellation.IsCancellationRequested) return null;
        if (_audio.ReadNext() is not (byte[] data, TimeSpan timestamp)) return null;
        if (timestamp.TotalSeconds >= _frameCount / _frameRate) return null;
        MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(data.AsBuffer(), timestamp);
        sample.Duration = TimeSpan.FromSeconds(data.Length / (double)(_audio.Channels * 2 * _audio.SampleRate));
        return sample;
    }
}
