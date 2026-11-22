using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace BetterShot.RecordingEditor;

/// <summary>A decoded video frame as top-down BGRA pixels (stride = width * 4).</summary>
internal sealed record DecodedFrame(int Width, int Height, byte[] Bgra)
{
    internal int Stride => Width * 4;

    internal BitmapSource ToBitmapSource()
    {
        BitmapSource bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, Bgra, Stride);
        bitmap.Freeze();
        return bitmap;
    }

    internal static DecodedFrame FromBitmapSource(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Pbgra32
            ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return new DecodedFrame(bgra.PixelWidth, bgra.PixelHeight, pixels);
    }
}

/// <summary>Basic facts about a movie, read through Windows' media editing APIs.</summary>
internal sealed record VideoInfo(double Duration, int Width, int Height, double FrameRate, bool HasAudio);

/// <summary>
/// Reads actual decoded video samples with Media Foundation. A reader retains its decoder and walks sample timestamps
/// forward, which is important for variable-frame-rate recordings and avoids the repeated/omitted images produced by
/// the shell thumbnail pipeline when frames are requested at export cadence.
/// </summary>
internal sealed class RecordingFrameReader : IDisposable
{
    /// <summary>ERROR_OPLOCK_HANDLE_CLOSED: the file system (notably OneDrive's sync filter) closed our handle because another open broke its oplock.</summary>
    private const int OplockHandleClosed = unchecked((int)0x80070323);
    private const int MaximumReopens = 3;
    private readonly string _moviePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MfVideoDecoder? _decoder;
    private int _decoderWidth;
    private int _decoderHeight;
    private bool _disposed;

    private RecordingFrameReader(string moviePath, VideoInfo info)
    {
        _moviePath = moviePath;
        Info = info;
    }

    internal VideoInfo Info { get; }

    internal static async Task<RecordingFrameReader> OpenAsync(string moviePath)
    {
        MediaClip clip = await OpenClipAsync(moviePath);
        VideoEncodingProperties video = clip.GetVideoEncodingProperties();
        double frameRate = video.FrameRate.Denominator == 0 ? 30 : video.FrameRate.Numerator / (double)video.FrameRate.Denominator;
        if (!double.IsFinite(frameRate) || frameRate < 1 || frameRate > 240) frameRate = 30;
        var info = new VideoInfo(clip.OriginalDuration.TotalSeconds, (int)video.Width, (int)video.Height, frameRate, clip.EmbeddedAudioTracks.Count > 0);
        return new RecordingFrameReader(Path.GetFullPath(moviePath), info);
    }

    private static async Task<MediaClip> OpenClipAsync(string moviePath)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(moviePath));
        return await MediaClip.CreateFromFileAsync(file);
    }

    /// <summary>Frames at the given source times, scaled to <paramref name="width"/> x <paramref name="height"/>.</summary>
    internal async Task<IReadOnlyList<DecodedFrame>> ReadAsync(IReadOnlyList<double> times, int width, int height, CancellationToken cancellation)
    {
        if (times.Count == 0) return [];
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height));
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // Decoding and long GOP seeks are synchronous native work. Keep them off the WPF dispatcher even when
            // this reader is called by scrubbing/filmstrip UI code.
            return await Task.Run(() =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                EnsureDecoder(width, height);
                var frames = new List<DecodedFrame>(times.Count);
                foreach (double requested in times)
                {
                    cancellation.ThrowIfCancellationRequested();
                    double last = Math.Max(0, Info.Duration - 1.0 / Math.Max(1, Info.FrameRate));
                    for (int attempt = 0; ; attempt++)
                    {
                        try { frames.Add(_decoder!.ReadNearest(Math.Clamp(requested, 0, last), cancellation)); break; }
                        catch (Exception error) when (error.HResult == OplockHandleClosed && attempt < MaximumReopens)
                        {
                            // A OneDrive filter can revoke a source-reader handle when a second graph opens the file.
                            _decoder?.Dispose();
                            _decoder = null;
                            EnsureDecoder(width, height);
                        }
                    }
                }
                return (IReadOnlyList<DecodedFrame>)frames;
            }, cancellation).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async Task<DecodedFrame> ReadAsync(double time, int width, int height, CancellationToken cancellation) =>
        (await ReadAsync([time], width, height, cancellation))[0];

    private void EnsureDecoder(int width, int height)
    {
        if (_decoder is not null && _decoderWidth == width && _decoderHeight == height) return;
        _decoder?.Dispose();
        _decoder = new MfVideoDecoder(_moviePath, width, height);
        _decoderWidth = width;
        _decoderHeight = height;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Do not release the COM reader while a background ReadAsync still owns it. Teardown is queued behind that
        // read and deliberately does not block the WPF close/replace path.
        _ = DisposeDecoderAsync();
    }

    private async Task DisposeDecoderAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _decoder?.Dispose();
            _decoder = null;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Stateful IMFSourceReader wrapper. COM calls remain serialized by <see cref="RecordingFrameReader"/>.</summary>
internal sealed class MfVideoDecoder : IDisposable
{
    private const uint VideoStream = 0xFFFFFFFC;
    private const int EndOfStream = 0x00000002;
    private readonly int _width;
    private readonly int _height;
    private IMFSourceReader? _reader;
    private TimestampedFrame? _previous;
    private TimestampedFrame? _next;
    private double _position = double.NegativeInfinity;

    internal MfVideoDecoder(string path, int width, int height)
    {
        _width = width;
        _height = height;
        MfInterop.Startup();
        IMFAttributes? attributes = null;
        IMFMediaType? output = null;
        try
        {
            MfInterop.Throw(MfInterop.MFCreateAttributes(out attributes, 2));
            Guid processing = MfInterop.SourceReaderEnableAdvancedVideoProcessing;
            MfInterop.Throw(attributes.SetUINT32(ref processing, 1));
            MfInterop.Throw(MfInterop.MFCreateSourceReaderFromURL(path, attributes, out _reader));
            MfInterop.Throw(MfInterop.MFCreateMediaType(out output));
            Guid major = MfInterop.MajorType;
            Guid video = MfInterop.Video;
            Guid subtype = MfInterop.Subtype;
            Guid rgb32 = MfInterop.Rgb32;
            Guid frameSize = MfInterop.FrameSize;
            MfInterop.Throw(output.SetGUID(ref major, ref video));
            MfInterop.Throw(output.SetGUID(ref subtype, ref rgb32));
            long packedSize = unchecked((long)(((ulong)(uint)width << 32) | (uint)height));
            MfInterop.Throw(output.SetUINT64(ref frameSize, packedSize));
            MfInterop.Throw(_reader.SetCurrentMediaType(VideoStream, IntPtr.Zero, output));
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            MfInterop.Release(output);
            MfInterop.Release(attributes);
        }
    }

    internal DecodedFrame ReadNearest(double seconds, CancellationToken cancellation)
    {
        // SourceReader seeks to the preceding clean point. Keep the look-ahead sample so monotonically increasing
        // requests decode each source sample once; seek only for backwards/random access.
        if (seconds + 0.000001 < _position || _next is null)
            Seek(seconds);

        while (_next is not null && _next.Time < seconds)
        {
            cancellation.ThrowIfCancellationRequested();
            _previous = _next;
            _next = ReadOne();
        }

        TimestampedFrame? chosen = _next;
        if (_previous is not null && (chosen is null || seconds - _previous.Time <= chosen.Time - seconds)) chosen = _previous;
        if (chosen is null) throw new InvalidOperationException("The video contains no decodable frames.");
        _position = seconds;
        return chosen.Frame;
    }

    private void Seek(double seconds)
    {
        if (_reader is null) throw new ObjectDisposedException(nameof(MfVideoDecoder));
        // Seeking slightly before the request guarantees that nearest-frame selection has both neighbours even when
        // the source reader chooses a sample at/after the requested timestamp.
        long ticks = checked((long)(Math.Max(0, seconds - 1) * TimeSpan.TicksPerSecond));
        var value = new PropVariant(ticks);
        Guid nullFormat = Guid.Empty;
        MfInterop.Throw(_reader.SetCurrentPosition(ref nullFormat, ref value));
        _previous = null;
        _next = ReadOne();
        _position = ticks / (double)TimeSpan.TicksPerSecond;
    }

    private TimestampedFrame? ReadOne()
    {
        if (_reader is null) throw new ObjectDisposedException(nameof(MfVideoDecoder));
        while (true)
        {
            MfInterop.Throw(_reader.ReadSample(VideoStream, 0, out _, out int flags, out long timestamp, out IMFSample? sample));
            if ((flags & EndOfStream) != 0) return null;
            if (sample is null) continue;
            try
            {
                MfInterop.Throw(sample.GetBufferCount(out int bufferCount));
                if (bufferCount == 0) continue;
                return new TimestampedFrame(timestamp / (double)TimeSpan.TicksPerSecond, CopyFrame(sample));
            }
            finally { MfInterop.Release(sample); }
        }
    }

    private DecodedFrame CopyFrame(IMFSample sample)
    {
        IMFMediaBuffer? buffer = null;
        try
        {
            // A decoded video sample normally owns one 2D buffer. Avoid coalescing it: some hardware decoders reject
            // ConvertToContiguousBuffer even though their single IMF2DBuffer is directly readable.
            MfInterop.Throw(sample.GetBufferByIndex(0, out buffer));
            int rowBytes = _width * 4;
            byte[] pixels = new byte[rowBytes * _height];
            if (buffer is IMF2DBuffer twoDimensional)
            {
                MfInterop.Throw(twoDimensional.Lock2D(out IntPtr scanline0, out int stride));
                try
                {
                    if (Math.Abs((long)stride) < rowBytes) throw new InvalidOperationException("Media Foundation returned an invalid video stride.");
                    for (int row = 0; row < _height; row++)
                        Marshal.Copy(IntPtr.Add(scanline0, checked(row * stride)), pixels, row * rowBytes, rowBytes);
                }
                finally { twoDimensional.Unlock2D(); }
            }
            else
            {
                MfInterop.Throw(buffer.Lock(out IntPtr data, out _, out int length));
                try
                {
                    if (length < pixels.Length) throw new InvalidOperationException("Media Foundation returned a short video frame.");
                    Marshal.Copy(data, pixels, 0, pixels.Length);
                }
                finally { buffer.Unlock(); }
            }
            for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
            return new DecodedFrame(_width, _height, pixels);
        }
        finally { MfInterop.Release(buffer); }
    }

    public void Dispose()
    {
        MfInterop.Release(_reader);
        _reader = null;
    }

    private sealed record TimestampedFrame(double Time, DecodedFrame Frame);
}

internal static class MfInterop
{
    internal static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    internal static readonly Guid SourceReaderEnableAdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private static readonly object StartupLock = new();
    private static bool _started;

    internal static void Startup()
    {
        lock (StartupLock)
        {
            if (_started) return;
            Throw(MFStartup(0x00020070, 0));
            _started = true;
        }
    }

    internal static void Throw(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    internal static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes? attributes, out IMFSourceReader reader);
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)] private readonly ushort _type;
    [FieldOffset(8)] private readonly long _value;
    internal PropVariant(long value) { this = default; _type = 20; _value = value; } // VT_I8
}

[ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(ref Guid key, IntPtr value);
    [PreserveSig] int GetItemType(ref Guid key, out int type);
    [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(ref Guid key, out int value);
    [PreserveSig] int GetUINT64(ref Guid key, out long value);
    [PreserveSig] int GetDouble(ref Guid key, out double value);
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(ref Guid key, out int length);
    [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, out int length);
    [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
    [PreserveSig] int GetBlobSize(ref Guid key, out int size);
    [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out int blobSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
    [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid key, int value);
    [PreserveSig] int SetUINT64(ref Guid key, long value);
    [PreserveSig] int SetDouble(ref Guid key, double value);
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
    [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
}

[ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    [PreserveSig] int GetMajorType(out Guid majorType);
    [PreserveSig] int IsCompressedFormat(out int compressed);
    [PreserveSig] int IsEqual(IMFMediaType mediaType, out int flags);
    [PreserveSig] int GetRepresentation(Guid representation, out IntPtr value);
    [PreserveSig] int FreeRepresentation(Guid representation, IntPtr value);
}

[ComImport, Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint streamIndex, out int selected);
    [PreserveSig] int SetStreamSelection(uint streamIndex, int selected);
    [PreserveSig] int GetNativeMediaType(uint streamIndex, uint mediaTypeIndex, out IMFMediaType mediaType);
    [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType mediaType);
    [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType mediaType);
    [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, ref PropVariant position);
    [PreserveSig] int ReadSample(uint streamIndex, uint controlFlags, out uint actualStreamIndex, out int streamFlags, out long timestamp, out IMFSample? sample);
    [PreserveSig] int Flush(uint streamIndex);
    [PreserveSig] int GetServiceForStream(uint streamIndex, ref Guid service, ref Guid iid, out IntPtr value);
    [PreserveSig] int GetPresentationAttribute(uint streamIndex, ref Guid key, IntPtr value);
}

[ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    [PreserveSig] int GetItem(ref Guid key, IntPtr value); [PreserveSig] int GetItemType(ref Guid key, out int type); [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result); [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(ref Guid key, out int value); [PreserveSig] int GetUINT64(ref Guid key, out long value); [PreserveSig] int GetDouble(ref Guid key, out double value); [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(ref Guid key, out int length); [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, out int length); [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length); [PreserveSig] int GetBlobSize(ref Guid key, out int size);
    [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out int blobSize); [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size); [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] int SetItem(ref Guid key, IntPtr value); [PreserveSig] int DeleteItem(ref Guid key); [PreserveSig] int DeleteAllItems(); [PreserveSig] int SetUINT32(ref Guid key, int value); [PreserveSig] int SetUINT64(ref Guid key, long value);
    [PreserveSig] int SetDouble(ref Guid key, double value); [PreserveSig] int SetGUID(ref Guid key, ref Guid value); [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value); [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
    [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value); [PreserveSig] int LockStore(); [PreserveSig] int UnlockStore(); [PreserveSig] int GetCount(out int count); [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value); [PreserveSig] int CopyAllItems(IMFAttributes destination);
    [PreserveSig] int GetSampleFlags(out int flags); [PreserveSig] int SetSampleFlags(int flags); [PreserveSig] int GetSampleTime(out long time); [PreserveSig] int SetSampleTime(long time); [PreserveSig] int GetSampleDuration(out long duration); [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out int count); [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer); [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer); [PreserveSig] int AddBuffer(IMFMediaBuffer buffer); [PreserveSig] int RemoveBufferByIndex(int index); [PreserveSig] int RemoveAllBuffers(); [PreserveSig] int GetTotalLength(out int length); [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out int currentLength);
    [PreserveSig] int SetCurrentLength(int currentLength);
    [PreserveSig] int GetMaxLength(out int maxLength);
}

[ComImport, Guid("7DC9D5F9-9ED9-44EC-9BBF-0600BB589FBB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMF2DBuffer
{
    [PreserveSig] int Lock2D(out IntPtr scanline0, out int stride);
    [PreserveSig] int Unlock2D();
    [PreserveSig] int GetScanline0AndPitch(out IntPtr scanline0, out int stride);
    [PreserveSig] int IsContiguousFormat([MarshalAs(UnmanagedType.Bool)] out bool contiguous);
    [PreserveSig] int GetContiguousLength(out int length);
    [PreserveSig] int ContiguousCopyTo(IntPtr destination, int length);
    [PreserveSig] int ContiguousCopyFrom(IntPtr source, int length);
}

/// <summary>Builds the H.264/AAC MP4 profiles used by recording exports.</summary>
internal static class RecordingExportProfiles
{
    internal const uint AudioSampleRate = 48_000, AudioChannels = 2;

    internal static void SetFrameRate(VideoEncodingProperties properties, double fps)
    {
        // Preserve fractional source rates in both the raw input description and the H.264 profile.
        uint denominator = fps == Math.Truncate(fps) ? 1u : 1001u;
        properties.FrameRate.Numerator = (uint)Math.Round(fps * denominator);
        properties.FrameRate.Denominator = denominator;
    }

    internal static MediaEncodingProfile Mp4(Mp4ExportPlan plan, bool includeAudio)
    {
        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = (uint)plan.Width;
        profile.Video.Height = (uint)plan.Height;
        profile.Video.Bitrate = plan.VideoBitrate;
        SetFrameRate(profile.Video, plan.FrameRate);
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Audio = includeAudio ? AudioEncodingProperties.CreateAac(AudioSampleRate, AudioChannels, plan.AudioBitrate) : null;
        return profile;
    }
}

/// <summary>
/// The fast path for exports without burned-in markup: one <see cref="MediaClip"/> per kept stretch, trimmed precisely,
/// rendered by Windows' media composition engine (hardware encoding, audio included unless asked not to). A re-mixed
/// soundtrack (already cut to the output timeline) replaces the movie's own audio as a background track.
/// </summary>
internal static class TrimmedMovieRenderer
{
    internal static async Task RenderAsync(string sourcePath, IReadOnlyList<KeptRange> ranges, string outputPath, Mp4ExportPlan plan, bool includeAudio,
        IProgress<double>? progress, CancellationToken cancellation, string? replacementAudioPath = null)
    {
        if (ranges.Count == 0) throw new InvalidOperationException("Nothing is kept, so there is nothing to export.");
        StorageFile source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(sourcePath));
        var composition = new MediaComposition();
        foreach (KeptRange range in ranges)
        {
            MediaClip clip = await MediaClip.CreateFromFileAsync(source);
            clip.TrimTimeFromStart = TimeSpan.FromSeconds(range.Start);
            clip.TrimTimeFromEnd = TimeSpan.FromSeconds(Math.Max(0, clip.OriginalDuration.TotalSeconds - range.End));
            if (!includeAudio || replacementAudioPath is not null) clip.Volume = 0;
            composition.Clips.Add(clip);
        }
        if (replacementAudioPath is not null)
        {
            StorageFile audioFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(replacementAudioPath));
            composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(audioFile));
        }
        await using (File.Create(outputPath)) { }
        StorageFile output = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(outputPath));
        bool hasAudio = replacementAudioPath is not null || includeAudio;
        MediaEncodingProfile profile = RecordingExportProfiles.Mp4(plan, hasAudio);
        var reportProgress = new Progress<double>(percent => progress?.Report(Math.Clamp(percent / 100, 0, 1)));
        Windows.Media.Transcoding.TranscodeFailureReason failure =
            await composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile).AsTask(cancellation, reportProgress);
        if (failure != Windows.Media.Transcoding.TranscodeFailureReason.None)
            throw new InvalidOperationException($"Windows could not render the movie ({failure}).");
    }
}
