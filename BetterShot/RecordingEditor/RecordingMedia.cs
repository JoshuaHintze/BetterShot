using System.IO;
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
/// Reads frames of a recording at arbitrary times through <see cref="MediaComposition"/> thumbnails (frame-accurate with
/// <see cref="VideoFramePrecision.NearestFrame"/>). Used for tracking, GIF/timelapse sampling, and burning markup into
/// exports. Frames are requested in batches so Windows builds its decode graph once per batch.
/// </summary>
internal sealed class RecordingFrameReader
{
    private readonly MediaComposition _composition;

    private RecordingFrameReader(MediaComposition composition, VideoInfo info)
    {
        _composition = composition;
        Info = info;
    }

    internal VideoInfo Info { get; }

    internal static async Task<RecordingFrameReader> OpenAsync(string moviePath)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(moviePath));
        MediaClip clip = await MediaClip.CreateFromFileAsync(file);
        VideoEncodingProperties video = clip.GetVideoEncodingProperties();
        double frameRate = video.FrameRate.Denominator == 0 ? 30 : video.FrameRate.Numerator / (double)video.FrameRate.Denominator;
        if (!double.IsFinite(frameRate) || frameRate < 1 || frameRate > 240) frameRate = 30;
        var info = new VideoInfo(clip.OriginalDuration.TotalSeconds, (int)video.Width, (int)video.Height, frameRate, clip.EmbeddedAudioTracks.Count > 0);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        return new RecordingFrameReader(composition, info);
    }

    /// <summary>Frames at the given source times, scaled to <paramref name="width"/> x <paramref name="height"/>.</summary>
    internal async Task<IReadOnlyList<DecodedFrame>> ReadAsync(IReadOnlyList<double> times, int width, int height, CancellationToken cancellation)
    {
        if (times.Count == 0) return [];
        double last = Math.Max(0, Info.Duration - 0.5 / Info.FrameRate);
        TimeSpan[] stamps = times.Select(time => TimeSpan.FromSeconds(Math.Clamp(time, 0, last))).ToArray();
        IReadOnlyList<ImageStream> streams = await _composition.GetThumbnailsAsync(stamps, width, height, VideoFramePrecision.NearestFrame).AsTask(cancellation);
        var frames = new List<DecodedFrame>(streams.Count);
        foreach (ImageStream stream in streams)
        {
            using (stream) frames.Add(await DecodeAsync(stream, width, height));
            cancellation.ThrowIfCancellationRequested();
        }
        if (frames.Count != times.Count) throw new InvalidOperationException($"Windows returned {frames.Count} frames for {times.Count} requested.");
        return frames;
    }

    internal async Task<DecodedFrame> ReadAsync(double time, int width, int height, CancellationToken cancellation) =>
        (await ReadAsync([time], width, height, cancellation))[0];

    private static async Task<DecodedFrame> DecodeAsync(IRandomAccessStream stream, int width, int height)
    {
        WinBitmapDecoder decoder = await WinBitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform();
        if (decoder.PixelWidth != width || decoder.PixelHeight != height)
        {
            transform.ScaledWidth = (uint)width;
            transform.ScaledHeight = (uint)height;
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }
        PixelDataProvider data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] pixels = data.DetachPixelData();
        for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        return new DecodedFrame(width, height, pixels);
    }
}

/// <summary>Builds the H.264/AAC MP4 profiles used by recording exports.</summary>
internal static class RecordingExportProfiles
{
    internal const uint AudioSampleRate = 48_000, AudioChannels = 2;

    internal static MediaEncodingProfile Mp4(int width, int height, double frameRate, bool includeAudio)
    {
        (int evenWidth, int evenHeight) = ExportPlanning.Even(width, height);
        int fps = (int)Math.Clamp(Math.Round(frameRate), 1, 120);
        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = (uint)evenWidth;
        profile.Video.Height = (uint)evenHeight;
        profile.Video.Bitrate = Recording.RecordingGeometry.SuggestBitrate(evenWidth, evenHeight, fps);
        profile.Video.FrameRate.Numerator = (uint)fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Audio = includeAudio ? AudioEncodingProperties.CreateAac(AudioSampleRate, AudioChannels, 192_000) : null;
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
    internal static async Task RenderAsync(string sourcePath, IReadOnlyList<KeptRange> ranges, string outputPath, VideoInfo info, bool includeAudio,
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
        bool hasAudio = replacementAudioPath is not null || includeAudio && info.HasAudio;
        MediaEncodingProfile profile = RecordingExportProfiles.Mp4(info.Width, info.Height, info.FrameRate, hasAudio);
        var reportProgress = new Progress<double>(percent => progress?.Report(Math.Clamp(percent / 100, 0, 1)));
        Windows.Media.Transcoding.TranscodeFailureReason failure =
            await composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile).AsTask(cancellation, reportProgress);
        if (failure != Windows.Media.Transcoding.TranscodeFailureReason.None)
            throw new InvalidOperationException($"Windows could not render the movie ({failure}).");
    }
}
