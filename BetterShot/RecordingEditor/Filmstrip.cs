using System.Threading;
using System.Windows.Media.Imaging;

namespace BetterShot.RecordingEditor;

/// <summary>
/// Pure tiling math for the video track's filmstrip. Tile lengths come from a fixed ladder (each step 1.5x the last),
/// so zooming reuses already decoded thumbnails until the tiles would be stretched too far.
/// </summary>
internal static class FilmstripLayout
{
    internal const double LadderBase = 1.5;
    internal const double SmallestTileSeconds = 1 / 30.0;

    /// <summary>The ladder step whose tile length is closest to (and not shorter than) one tile width at this zoom.</summary>
    internal static int Step(double pixelsPerSecond, double tileWidth)
    {
        double ideal = tileWidth / Math.Max(1e-6, pixelsPerSecond);
        int step = (int)Math.Ceiling(Math.Log(Math.Max(ideal, SmallestTileSeconds) / SmallestTileSeconds, LadderBase) - 1e-9);
        return Math.Clamp(step, 0, 60);
    }

    internal static double TileSeconds(int step) => SmallestTileSeconds * Math.Pow(LadderBase, step);

    /// <summary>Indices of the tiles that overlap [start, end), clamped to the recording.</summary>
    internal static IEnumerable<int> VisibleTiles(double start, double end, double duration, double tileSeconds)
    {
        int first = (int)Math.Floor(Math.Max(0, start) / tileSeconds);
        int last = (int)Math.Ceiling(Math.Min(duration, end) / tileSeconds) - 1;
        for (int index = first; index <= last; index++) yield return index;
    }

    /// <summary>The frame shown for a tile: its middle, kept inside the recording.</summary>
    internal static double SampleTime(int index, double tileSeconds, double duration) => Math.Clamp((index + 0.5) * tileSeconds, 0, Math.Max(0, duration - 0.001));
}

/// <summary>
/// Decodes small frame thumbnails for the filmstrip in the background (Windows' media composition thumbnails, in
/// batches), keeps the most recent few hundred, and raises <see cref="Updated"/> when new ones arrive. Requests and
/// decoding happen on the UI dispatcher's async context because the frame reader was created there; the decoding
/// itself is done by Windows off the UI thread.
/// </summary>
internal sealed class FilmstripCache : IDisposable
{
    private const int Capacity = 600, Batch = 8;
    private readonly Dictionary<(int Step, int Index), BitmapSource> _thumbnails = [];
    private readonly LinkedList<(int Step, int Index)> _recent = new();
    private readonly HashSet<(int Step, int Index)> _pending = [];
    private readonly Queue<(int Step, int Index)> _queue = new();
    private readonly CancellationTokenSource _cancellation = new();
    private RecordingFrameReader? _reader;
    private int _thumbnailWidth = 96, _thumbnailHeight = 54;
    private double _duration;
    private bool _running;

    internal event EventHandler? Updated;

    internal void Attach(RecordingFrameReader? reader, int thumbnailWidth, int thumbnailHeight)
    {
        RecordingFrameReader? previous = _reader;
        _reader = reader;
        if (!ReferenceEquals(previous, reader)) previous?.Dispose();
        _duration = reader?.Info.Duration ?? 0;
        _thumbnailWidth = Math.Max(8, thumbnailWidth);
        _thumbnailHeight = Math.Max(8, thumbnailHeight);
        Clear();
    }

    internal void Clear()
    {
        _thumbnails.Clear();
        _recent.Clear();
        _queue.Clear();
        _pending.Clear();
    }

    /// <summary>The thumbnail of a tile, or the nearest coarser one already decoded, or null (and it is requested).</summary>
    internal BitmapSource? Get(int step, int index)
    {
        if (_thumbnails.TryGetValue((step, index), out BitmapSource? exact)) { Touch((step, index)); return exact; }
        Request(step, index);
        double time = FilmstripLayout.SampleTime(index, FilmstripLayout.TileSeconds(step), _duration);
        for (int coarser = step + 1; coarser <= step + 4; coarser++)
        {
            int coarseIndex = (int)Math.Floor(time / FilmstripLayout.TileSeconds(coarser));
            if (_thumbnails.TryGetValue((coarser, coarseIndex), out BitmapSource? fallback)) return fallback;
        }
        return null;
    }

    private void Request(int step, int index)
    {
        if (_reader is null || !_pending.Add((step, index))) return;
        _queue.Enqueue((step, index));
        while (_queue.Count > 120) _pending.Remove(_queue.Dequeue()); // Tiles scrolled past long ago; asked again if seen again.
        if (!_running) _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        _running = true;
        try
        {
            while (_queue.Count > 0 && _reader is RecordingFrameReader reader && !_cancellation.IsCancellationRequested)
            {
                var batch = new List<(int Step, int Index)>();
                while (batch.Count < Batch && _queue.Count > 0) batch.Add(_queue.Dequeue());
                double[] times = batch.Select(key => FilmstripLayout.SampleTime(key.Index, FilmstripLayout.TileSeconds(key.Step), _duration)).ToArray();
                IReadOnlyList<DecodedFrame> frames;
                try { frames = await reader.ReadAsync(times, _thumbnailWidth, _thumbnailHeight, _cancellation.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception) { foreach ((int, int) key in batch) _pending.Remove(key); return; } // Thumbnails are cosmetic; the clip still draws.
                if (!ReferenceEquals(reader, _reader)) return;
                for (int position = 0; position < batch.Count && position < frames.Count; position++) Store(batch[position], frames[position].ToBitmapSource());
                Updated?.Invoke(this, EventArgs.Empty);
            }
        }
        finally { _running = false; }
    }

    private void Store((int Step, int Index) key, BitmapSource bitmap)
    {
        _pending.Remove(key);
        _thumbnails[key] = bitmap;
        Touch(key);
        while (_recent.Count > Capacity)
        {
            (int, int) oldest = _recent.Last!.Value;
            _recent.RemoveLast();
            _thumbnails.Remove(oldest);
        }
    }

    private void Touch((int Step, int Index) key)
    {
        _recent.Remove(key);
        _recent.AddFirst(key);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        _reader?.Dispose();
        _reader = null;
        Clear();
    }
}
