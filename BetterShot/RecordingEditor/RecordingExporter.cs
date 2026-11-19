using BetterShot.Editing;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterShot.RecordingEditor;

/// <summary>One export: what to write, from which edit state, and with which GIF/timelapse choices.</summary>
internal sealed record RecordingExportJob(
    RecordingExportKind Kind,
    string MoviePath,
    string OutputPath,
    RecordingEditState State,
    VideoInfo Info,
    int GifFrameRate,
    int GifMaxDimension,
    int? TimelapseSpeed,
    string TemporaryFolder);

/// <summary>Renders the frame at a source time with the visible marks burned in, using the screenshot editor's renderer.</summary>
internal static class MarkCompositor
{
    internal static bool HasMarkupAt(RecordingEditState state, double time) => state.VisibleMarksAt(time).Any() || state.BlursWholePictureAt(time);

    /// <summary>Must run on a thread with a WPF dispatcher (the editor's UI thread).</summary>
    internal static BitmapSource Compose(BitmapSource frame, RecordingEditState state, double time)
    {
        var document = new CaptureDocument(frame);
        var items = state.VisibleMarksAt(time).Select(mark => mark.ShapeAt(time)).ToList();
        if (state.BlursWholePictureAt(time)) items.Add(WholePictureBlur(document.CanvasRect));
        return DocumentRenderer.Render(document, items, null, compose: false);
    }

    /// <summary>A blur redaction over the whole frame, coarse enough to hide text at any recording size.</summary>
    internal static RectangleAnnotation WholePictureBlur(Rect canvas) =>
        new(canvas, Colors.Black, Math.Max(6, Math.Max(canvas.Width, canvas.Height) / 80), Redaction: true) { RedactionMode = RedactionStyle.Blur };
}

/// <summary>
/// Writes exports. Without visible markup an MP4 is a precise trim by Windows' media composition engine (fast, audio
/// kept). With markup, every output frame is decoded, composited with the marks (so pixelate/blur and followed
/// redactions use real pixels), and encoded; the kept audio is muxed back in. GIFs and timelapses always sample frames.
/// The original recording is never modified here. Output is written to a temporary file and moved into place at the end.
/// </summary>
internal static class RecordingExporter
{
    private const int FrameBatch = 8;

    /// <returns>A warning to show the user (for example, audio that could not be included), or null.</returns>
    internal static async Task<string?> ExportAsync(RecordingExportJob job, RecordingFrameReader reader, Dispatcher renderer, IProgress<double> progress, CancellationToken cancellation)
    {
        if (job.State.Timeline.KeptRanges.Count == 0) throw new InvalidOperationException("Nothing is kept, so there is nothing to export.");
        string folder = Path.GetDirectoryName(Path.GetFullPath(job.OutputPath)) ?? job.TemporaryFolder;
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, "." + Path.GetFileNameWithoutExtension(job.OutputPath) + "-" + Guid.NewGuid().ToString("N")[..8] + ".partial" + Path.GetExtension(job.OutputPath));
        try
        {
            string? warning = job.Kind switch
            {
                RecordingExportKind.Gif => await WriteGifAsync(job, reader, renderer, temporary, progress, cancellation),
                RecordingExportKind.Timelapse => await WriteTimelapseAsync(job, reader, renderer, temporary, progress, cancellation),
                _ => await WriteMovieAsync(job, reader, renderer, temporary, progress, cancellation)
            };
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, job.OutputPath, overwrite: true);
            return warning;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { } // A leftover partial file is hidden and harmless.
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<string?> WriteMovieAsync(RecordingExportJob job, RecordingFrameReader reader, Dispatcher renderer, string output, IProgress<double> progress, CancellationToken cancellation)
    {
        RecordingEditState state = job.State;
        bool includeAudio = job.Kind == RecordingExportKind.Mp4 && job.Info.HasAudio;
        if (!state.HasVisibleMarks)
        {
            await TrimmedMovieRenderer.RenderAsync(job.MoviePath, state.Timeline.KeptRanges, output, job.Info, includeAudio, progress, cancellation);
            return null;
        }
        string? warning = null;
        KeptAudio? audio = null;
        if (includeAudio)
        {
            try { audio = await KeptAudio.ExtractAsync(job.MoviePath, state.Timeline.KeptRanges, job.TemporaryFolder, cancellation); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                warning = $"The audio could not be included ({error.Message}).";
            }
        }
        using (audio)
        {
            IReadOnlyList<double> times = ExportPlanning.FrameTimes(state.Timeline, job.Info.FrameRate);
            var frames = new MarkedFrameSource(reader, renderer, state, times, job.Info.Width, job.Info.Height);
            await FrameMovieEncoder.EncodeAsync(output, job.Info.Width, job.Info.Height, job.Info.FrameRate, times.Count, frames.RenderAsync, audio, progress, cancellation);
        }
        return warning;
    }

    private static async Task<string?> WriteTimelapseAsync(RecordingExportJob job, RecordingFrameReader reader, Dispatcher renderer, string output, IProgress<double> progress, CancellationToken cancellation)
    {
        int speed = ExportPlanning.TimelapseSpeed(job.State.Timeline.OutputDuration, job.TimelapseSpeed);
        IReadOnlyList<double> times = ExportPlanning.FrameTimes(job.State.Timeline, ExportPlanning.TimelapseFrameRate, speed);
        var frames = new MarkedFrameSource(reader, renderer, job.State, times, job.Info.Width, job.Info.Height);
        await FrameMovieEncoder.EncodeAsync(output, job.Info.Width, job.Info.Height, ExportPlanning.TimelapseFrameRate, times.Count, frames.RenderAsync, null, progress, cancellation);
        return null;
    }

    private static async Task<string?> WriteGifAsync(RecordingExportJob job, RecordingFrameReader reader, Dispatcher renderer, string output, IProgress<double> progress, CancellationToken cancellation)
    {
        int frameRate = ExportPlanning.NormalizeGifFrameRate(job.GifFrameRate);
        if (ExportPlanning.IsTooShortForGif(job.State.Timeline, frameRate)) throw new InvalidOperationException("Selection is too short for a GIF.");
        IReadOnlyList<double> times = ExportPlanning.FrameTimes(job.State.Timeline, frameRate);
        (int width, int height) = ExportPlanning.FitWithin(job.Info.Width, job.Info.Height, ExportPlanning.NormalizeGifMaxDimension(job.GifMaxDimension));
        var frames = new MarkedFrameSource(reader, renderer, job.State, times, job.Info.Width, job.Info.Height);
        var palettized = new List<BitmapSource>(times.Count);
        for (int index = 0; index < times.Count; index++)
        {
            DecodedFrame frame = await frames.RenderAsync(index, cancellation);
            BitmapSource scaled = await renderer.InvokeAsync(() => GifWriter.Palettize(Scale(frame.ToBitmapSource(), width, height)), DispatcherPriority.Background).Task;
            palettized.Add(scaled);
            progress.Report((index + 1) / (double)times.Count * 0.95);
        }
        IReadOnlyList<ushort> delays = ExportPlanning.GifDelays(times.Count, frameRate);
        await renderer.InvokeAsync(() =>
        {
            using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
            GifWriter.Write(stream, palettized, delays);
        }, DispatcherPriority.Background).Task;
        progress.Report(1);
        return null;
    }

    private static BitmapSource Scale(BitmapSource source, int width, int height)
    {
        if (source.PixelWidth == width && source.PixelHeight == height) return source;
        var scaled = new TransformedBitmap(source, new ScaleTransform(width / (double)source.PixelWidth, height / (double)source.PixelHeight));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>Decodes source frames in small batches and burns in the marks of each frame's time on the UI thread.</summary>
    private sealed class MarkedFrameSource
    {
        private readonly RecordingFrameReader _reader;
        private readonly Dispatcher _renderer;
        private readonly RecordingEditState _state;
        private readonly IReadOnlyList<double> _times;
        private readonly int _width;
        private readonly int _height;
        private readonly Dictionary<int, DecodedFrame> _ready = [];

        internal MarkedFrameSource(RecordingFrameReader reader, Dispatcher renderer, RecordingEditState state, IReadOnlyList<double> times, int width, int height)
        {
            _reader = reader;
            _renderer = renderer;
            _state = state;
            _times = times;
            _width = width;
            _height = height;
        }

        internal async Task<DecodedFrame> RenderAsync(int index, CancellationToken cancellation)
        {
            if (!_ready.TryGetValue(index, out DecodedFrame? frame))
            {
                _ready.Clear();
                int count = Math.Min(FrameBatch, _times.Count - index);
                double[] batch = _times.Skip(index).Take(count).ToArray();
                // The encoder asks for frames on a media worker thread; the reader was created on the UI thread, so call it there.
                IReadOnlyList<DecodedFrame> decoded = await _renderer.InvokeAsync(() => _reader.ReadAsync(batch, _width, _height, cancellation)).Task.Unwrap();
                for (int offset = 0; offset < decoded.Count; offset++)
                    _ready[index + offset] = await ComposeAsync(decoded[offset], batch[offset]);
                frame = _ready[index];
            }
            _ready.Remove(index);
            return frame;
        }

        private async Task<DecodedFrame> ComposeAsync(DecodedFrame frame, double time)
        {
            if (!MarkCompositor.HasMarkupAt(_state, time)) return frame;
            return await _renderer.InvokeAsync(() => DecodedFrame.FromBitmapSource(MarkCompositor.Compose(frame.ToBitmapSource(), _state, time)), DispatcherPriority.Background).Task;
        }
    }
}
