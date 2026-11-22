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
    string TemporaryFolder,
    IReadOnlyList<Recording.AudioTrackKind>? RemixTracks = null,
    CameraSource? Camera = null,
    Mp4ExportPreset Mp4Preset = Mp4ExportPreset.Original);

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

    /// <summary>Draws the camera over the screen using the same centered aspect-fill square/circle as the player.</summary>
    internal static BitmapSource ComposeCamera(BitmapSource frame, BitmapSource camera, CameraEdit edit)
    {
        Rect target = edit.Placement.ToPixels(frame.PixelWidth, frame.PixelHeight);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawImage(frame, new Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
            Geometry clip = edit.Shape == CameraShape.Circle
                ? new EllipseGeometry(target)
                : new RectangleGeometry(target);
            drawing.PushClip(clip);
            var brush = new ImageBrush(camera)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            drawing.DrawRectangle(brush, null, target);
            drawing.Pop();
        }
        var rendered = new RenderTargetBitmap(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return rendered;
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
        if (!string.IsNullOrWhiteSpace(job.Camera?.Path) && !job.Camera.IsAvailable)
            throw new InvalidOperationException("The camera video is missing, so the export was stopped instead of silently leaving out the camera picture.");
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
        Mp4ExportPlan plan = ExportPlanning.PlanMp4(job.Mp4Preset, job.Info.Width, job.Info.Height, job.Info.FrameRate);
        if (job.Kind == RecordingExportKind.Mp4 && job.RemixTracks is { Count: > 0 } tracks) return await WriteRemixedMovieAsync(job, tracks, reader, renderer, output, progress, cancellation);
        bool includeAudio = job.Kind == RecordingExportKind.Mp4 && job.Info.HasAudio;
        if (!state.HasVisibleMarks && !HasCamera(job))
        {
            await TrimmedMovieRenderer.RenderAsync(job.MoviePath, state.Timeline.KeptRanges, output, plan, includeAudio, progress, cancellation);
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
        await EncodeMarkedFramesAsync(job, reader, renderer, output, audio, plan, progress, cancellation);
        return warning;
    }

    /// <summary>
    /// An MP4 without the muted tracks: the unmuted separate tracks are mixed again for just the kept stretches, then
    /// replace the movie's mixed soundtrack (as a background track on the fast path, or muxed with burned-in frames).
    /// </summary>
    private static async Task<string?> WriteRemixedMovieAsync(RecordingExportJob job, IReadOnlyList<Recording.AudioTrackKind> tracks, RecordingFrameReader reader,
        Dispatcher renderer, string output, IProgress<double> progress, CancellationToken cancellation)
    {
        RecordingEditState state = job.State;
        Mp4ExportPlan plan = ExportPlanning.PlanMp4(job.Mp4Preset, job.Info.Width, job.Info.Height, job.Info.FrameRate);
        Directory.CreateDirectory(job.TemporaryFolder);
        string remix = Path.Combine(job.TemporaryFolder, "remix-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            string[] sources = tracks.Select(kind => Recording.RecordingAudioTracks.SidecarPath(job.MoviePath, kind)).ToArray();
            IReadOnlyList<KeptRange> ranges = state.Timeline.KeptRanges;
            await Task.Run(() => AudioRemixer.WriteKeptMix(sources, ranges, remix, cancellation), cancellation);
            if (!state.HasVisibleMarks && !HasCamera(job))
            {
                await TrimmedMovieRenderer.RenderAsync(job.MoviePath, ranges, output, plan, includeAudio: true, progress, cancellation, remix);
                return null;
            }
            KeptAudio audio = KeptAudio.FromWave(remix, [new KeptRange(0, state.Timeline.OutputDuration, 0)], deleteOnDispose: false);
            await EncodeMarkedFramesAsync(job, reader, renderer, output, audio, plan, progress, cancellation);
            return null;
        }
        finally { KeptAudio.TryDelete(remix); }
    }

    private static async Task EncodeMarkedFramesAsync(RecordingExportJob job, RecordingFrameReader reader, Dispatcher renderer, string output, KeptAudio? audio,
        Mp4ExportPlan plan, IProgress<double> progress, CancellationToken cancellation)
    {
        RecordingEditState state = job.State;
        using (audio)
        {
            IReadOnlyList<double> times = ExportPlanning.FrameTimes(state.Timeline, plan.FrameRate);
            using var frames = await MarkedFrameSource.CreateAsync(job, reader, renderer, times, cancellation);
            async Task<DecodedFrame> RenderScaledAsync(int index, CancellationToken token)
            {
                DecodedFrame frame = await frames.RenderAsync(index, token);
                if (frame.Width == plan.Width && frame.Height == plan.Height) return frame;
                return await renderer.InvokeAsync(() => DecodedFrame.FromBitmapSource(Scale(frame.ToBitmapSource(), plan.Width, plan.Height)), DispatcherPriority.Background).Task;
            }
            await FrameMovieEncoder.EncodeAsync(output, plan, times.Count, RenderScaledAsync, audio, progress, cancellation);
        }
    }

    private static async Task<string?> WriteTimelapseAsync(RecordingExportJob job, RecordingFrameReader reader, Dispatcher renderer, string output, IProgress<double> progress, CancellationToken cancellation)
    {
        int speed = ExportPlanning.TimelapseSpeed(job.State.Timeline.OutputDuration, job.TimelapseSpeed);
        IReadOnlyList<double> times = ExportPlanning.FrameTimes(job.State.Timeline, ExportPlanning.TimelapseFrameRate, speed);
        using var frames = await MarkedFrameSource.CreateAsync(job, reader, renderer, times, cancellation);
        await FrameMovieEncoder.EncodeAsync(output, job.Info.Width, job.Info.Height, ExportPlanning.TimelapseFrameRate, times.Count, frames.RenderAsync, null, progress, cancellation);
        return null;
    }

    private static async Task<string?> WriteGifAsync(RecordingExportJob job, RecordingFrameReader reader, Dispatcher renderer, string output, IProgress<double> progress, CancellationToken cancellation)
    {
        int frameRate = ExportPlanning.NormalizeGifFrameRate(job.GifFrameRate);
        if (ExportPlanning.IsTooShortForGif(job.State.Timeline, frameRate)) throw new InvalidOperationException("Selection is too short for a GIF.");
        IReadOnlyList<double> times = ExportPlanning.FrameTimes(job.State.Timeline, frameRate);
        (int width, int height) = ExportPlanning.FitWithin(job.Info.Width, job.Info.Height, ExportPlanning.NormalizeGifMaxDimension(job.GifMaxDimension));
        using var frames = await MarkedFrameSource.CreateAsync(job, reader, renderer, times, cancellation);
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

    private static bool HasCamera(RecordingExportJob job) => job.Camera is { IsAvailable: true } && job.State.Camera.Visible;

    /// <summary>Decodes source frames in small batches and burns in the marks of each frame's time on the UI thread.</summary>
    private sealed class MarkedFrameSource : IDisposable
    {
        private readonly RecordingFrameReader _reader;
        private readonly Dispatcher _renderer;
        private readonly RecordingEditState _state;
        private readonly IReadOnlyList<double> _times;
        private readonly int _width;
        private readonly int _height;
        private readonly CameraSource? _camera;
        private readonly RecordingFrameReader? _cameraReader;
        private readonly Dictionary<int, DecodedFrame> _ready = [];

        private MarkedFrameSource(RecordingFrameReader reader, Dispatcher renderer, RecordingEditState state, IReadOnlyList<double> times, int width, int height,
            CameraSource? camera, RecordingFrameReader? cameraReader)
        {
            _reader = reader;
            _renderer = renderer;
            _state = state;
            _times = times;
            _width = width;
            _height = height;
            _camera = camera;
            _cameraReader = cameraReader;
        }

        internal static async Task<MarkedFrameSource> CreateAsync(RecordingExportJob job, RecordingFrameReader reader, Dispatcher renderer,
            IReadOnlyList<double> times, CancellationToken cancellation)
        {
            RecordingFrameReader? cameraReader = null;
            if (HasCamera(job))
            {
                cancellation.ThrowIfCancellationRequested();
                try { cameraReader = await RecordingFrameReader.OpenAsync(job.Camera!.Path!); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw new InvalidOperationException($"The camera video could not be opened, so the export was stopped ({error.Message}).", error);
                }
            }
            return new MarkedFrameSource(reader, renderer, job.State, times, job.Info.Width, job.Info.Height, job.Camera, cameraReader);
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
                IReadOnlyDictionary<int, DecodedFrame> cameras = await ReadCameraBatchAsync(batch, cancellation);
                for (int offset = 0; offset < decoded.Count; offset++)
                    _ready[index + offset] = await ComposeAsync(decoded[offset], batch[offset], cameras.GetValueOrDefault(offset));
                frame = _ready[index];
            }
            _ready.Remove(index);
            return frame;
        }

        private async Task<IReadOnlyDictionary<int, DecodedFrame>> ReadCameraBatchAsync(IReadOnlyList<double> screenTimes, CancellationToken cancellation)
        {
            if (_camera is null || _cameraReader is null) return new Dictionary<int, DecodedFrame>();
            int[] offsets = screenTimes.Select((time, offset) => (time, offset))
                .Where(item => _camera.IsVisibleAt(item.time, _state.Camera)
                    && _camera.CameraTimeAt(item.time, _cameraReader.Info.Duration) is not null)
                .Select(item => item.offset).ToArray();
            if (offsets.Length == 0) return new Dictionary<int, DecodedFrame>();
            double[] cameraTimes = offsets.Select(offset => _camera.CameraTimeAt(screenTimes[offset], _cameraReader.Info.Duration)!.Value).ToArray();
            try
            {
                IReadOnlyList<DecodedFrame> frames = await _renderer.InvokeAsync(() => _cameraReader.ReadAsync(cameraTimes,
                    _cameraReader.Info.Width, _cameraReader.Info.Height, cancellation)).Task.Unwrap();
                return offsets.Select((offset, index) => (offset, frame: frames[index])).ToDictionary(item => item.offset, item => item.frame);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new InvalidOperationException($"A camera frame could not be decoded, so the export was stopped ({error.Message}).", error);
            }
        }

        private async Task<DecodedFrame> ComposeAsync(DecodedFrame frame, double time, DecodedFrame? camera)
        {
            bool marks = MarkCompositor.HasMarkupAt(_state, time);
            if (camera is null && !marks) return frame;
            return await _renderer.InvokeAsync(() =>
            {
                BitmapSource composed = frame.ToBitmapSource();
                if (camera is not null) composed = MarkCompositor.ComposeCamera(composed, camera.ToBitmapSource(), _state.Camera);
                if (marks) composed = MarkCompositor.Compose(composed, _state, time);
                return DecodedFrame.FromBitmapSource(composed);
            }, DispatcherPriority.Background).Task;
        }

        public void Dispose() => _cameraReader?.Dispose();
    }
}
