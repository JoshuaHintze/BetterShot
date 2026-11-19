namespace BetterShot.RecordingEditor;

/// <summary>An 8-bit grayscale frame used for tracking (usually a downscaled copy of the video frame).</summary>
internal sealed class GrayFrame
{
    internal GrayFrame(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height) throw new ArgumentException("Frame size does not match its pixels.");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    internal int Width { get; }
    internal int Height { get; }
    internal byte[] Pixels { get; }

    internal byte this[int x, int y] => Pixels[y * Width + x];

    /// <summary>Converts 32-bit BGRA (or BGRX) pixels with Rec. 601 luma weights.</summary>
    internal static GrayFrame FromBgra(byte[] bgra, int width, int height, int stride)
    {
        byte[] gray = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int source = y * stride, target = y * width;
            for (int x = 0; x < width; x++, source += 4)
                gray[target + x] = (byte)((bgra[source] * 29 + bgra[source + 1] * 150 + bgra[source + 2] * 77) >> 8);
        }
        return new GrayFrame(width, height, gray);
    }
}

/// <summary>A tracked box in frame pixels.</summary>
internal readonly record struct TrackBox(int X, int Y, int Width, int Height)
{
    internal int CenterX => X + Width / 2;
    internal int CenterY => Y + Height / 2;
}

internal readonly record struct TrackStep(TrackBox Box, double Score, bool Lost);

/// <summary>
/// Follows the content under a redaction from frame to frame by template matching: the content in the first frame is
/// the template, and each new frame is searched within a small window around the last position for the offset with
/// the highest normalized cross-correlation. When the best score drops below <see cref="LostThreshold"/> the content
/// is considered lost (covered, scrolled away, or changed). Pure math, no WPF or media types, so it is testable.
/// </summary>
internal sealed class RedactionTracker
{
    internal const double DefaultLostThreshold = 0.55;
    private const int MaximumTemplateSamples = 48;
    private const double FlatVariance = 4; // Below this the template has no texture to follow.
    private readonly int _searchRadius;
    private readonly int _sampleStep;
    private readonly int[] _sampleX;
    private readonly int[] _sampleY;
    private readonly double[] _template;
    private readonly double _templateNorm;
    private readonly bool _flat;
    private TrackBox _box;

    internal RedactionTracker(GrayFrame first, TrackBox box, int searchRadius = 16, double lostThreshold = DefaultLostThreshold)
    {
        _box = Clamp(box, first.Width, first.Height);
        _searchRadius = Math.Max(1, searchRadius);
        LostThreshold = lostThreshold;
        _sampleStep = Math.Max(1, (int)Math.Ceiling(Math.Max(_box.Width, _box.Height) / (double)MaximumTemplateSamples));
        _sampleX = Enumerable.Range(0, (_box.Width + _sampleStep - 1) / _sampleStep).Select(index => index * _sampleStep).ToArray();
        _sampleY = Enumerable.Range(0, (_box.Height + _sampleStep - 1) / _sampleStep).Select(index => index * _sampleStep).ToArray();
        _template = new double[_sampleX.Length * _sampleY.Length];
        double mean = 0;
        int cursor = 0;
        foreach (int y in _sampleY)
            foreach (int x in _sampleX)
            {
                _template[cursor] = first[_box.X + x, _box.Y + y];
                mean += _template[cursor++];
            }
        mean /= _template.Length;
        double sum = 0;
        for (int index = 0; index < _template.Length; index++)
        {
            _template[index] -= mean;
            sum += _template[index] * _template[index];
        }
        _templateNorm = Math.Sqrt(sum);
        _flat = sum / _template.Length < FlatVariance;
    }

    internal double LostThreshold { get; }
    internal TrackBox Box => _box;
    /// <summary>A flat (textureless) template cannot be followed; the box simply stays where it is.</summary>
    internal bool IsFlat => _flat;

    internal TrackStep Step(GrayFrame frame)
    {
        if (_flat) return new TrackStep(_box, 1, false);
        int bestX = _box.X, bestY = _box.Y;
        double best = double.NegativeInfinity;
        // Exhaustive search: screen content (text, icons) has detail finer than any coarse grid could step over.
        for (int dy = -_searchRadius; dy <= _searchRadius; dy++)
            for (int dx = -_searchRadius; dx <= _searchRadius; dx++)
                Consider(frame, _box.X + dx, _box.Y + dy, ref best, ref bestX, ref bestY);
        if (double.IsNegativeInfinity(best) || best < LostThreshold) return new TrackStep(_box, double.IsNegativeInfinity(best) ? 0 : best, true);
        _box = _box with { X = bestX, Y = bestY };
        return new TrackStep(_box, best, false);
    }

    private void Consider(GrayFrame frame, int x, int y, ref double best, ref int bestX, ref int bestY)
    {
        if (x < 0 || y < 0 || x + _box.Width > frame.Width || y + _box.Height > frame.Height) return;
        double score = Correlate(frame, x, y);
        if (score > best) { best = score; bestX = x; bestY = y; }
    }

    /// <summary>Normalized cross-correlation of the template with the frame at (x, y), in [-1, 1].</summary>
    private double Correlate(GrayFrame frame, int x, int y)
    {
        double mean = 0;
        foreach (int sampleY in _sampleY)
        {
            int row = (y + sampleY) * frame.Width + x;
            foreach (int sampleX in _sampleX) mean += frame.Pixels[row + sampleX];
        }
        mean /= _template.Length;
        double cross = 0, energy = 0;
        int cursor = 0;
        foreach (int sampleY in _sampleY)
        {
            int row = (y + sampleY) * frame.Width + x;
            foreach (int sampleX in _sampleX)
            {
                double value = frame.Pixels[row + sampleX] - mean;
                cross += value * _template[cursor++];
                energy += value * value;
            }
        }
        if (energy < 1e-6 || _templateNorm < 1e-6) return 0;
        return cross / (_templateNorm * Math.Sqrt(energy));
    }

    private static TrackBox Clamp(TrackBox box, int width, int height)
    {
        int boxWidth = Math.Clamp(box.Width, 2, width), boxHeight = Math.Clamp(box.Height, 2, height);
        return new TrackBox(Math.Clamp(box.X, 0, width - boxWidth), Math.Clamp(box.Y, 0, height - boxHeight), boxWidth, boxHeight);
    }
}

/// <summary>Reduces a dense per-frame track to the keyframes where the path changes course.</summary>
internal static class TrackPath
{
    /// <summary>
    /// Keeps the first and last sample plus every sample whose position cannot be reproduced within
    /// <paramref name="tolerance"/> pixels by interpolating between the kept neighbors (Douglas–Peucker over time).
    /// </summary>
    internal static IReadOnlyList<TrackKeyframe> Simplify(IReadOnlyList<TrackKeyframe> samples, double tolerance)
    {
        if (samples.Count <= 2) return samples.ToArray();
        bool[] keep = new bool[samples.Count];
        keep[0] = keep[^1] = true;
        var pending = new Stack<(int First, int Last)>();
        pending.Push((0, samples.Count - 1));
        while (pending.Count > 0)
        {
            (int first, int last) = pending.Pop();
            int worst = -1;
            double worstError = tolerance;
            for (int index = first + 1; index < last; index++)
            {
                double error = Error(samples[first], samples[last], samples[index]);
                if (error > worstError) { worst = index; worstError = error; }
            }
            if (worst < 0) continue;
            keep[worst] = true;
            pending.Push((first, worst));
            pending.Push((worst, last));
        }
        return samples.Where((_, index) => keep[index]).ToArray();
    }

    private static double Error(TrackKeyframe first, TrackKeyframe last, TrackKeyframe sample)
    {
        double span = last.Time - first.Time;
        double progress = span <= 1e-9 ? 0 : (sample.Time - first.Time) / span;
        Rect expected = VideoMark.Lerp(first.Bounds, last.Bounds, progress);
        return Math.Max(
            Math.Max(Math.Abs(expected.X - sample.Bounds.X), Math.Abs(expected.Y - sample.Bounds.Y)),
            Math.Max(Math.Abs(expected.Width - sample.Bounds.Width), Math.Abs(expected.Height - sample.Bounds.Height)));
    }
}
