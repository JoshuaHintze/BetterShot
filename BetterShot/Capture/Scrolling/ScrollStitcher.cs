namespace BetterShot.Capture.Scrolling;

/// <summary>Outcome of adding one frame to a <see cref="ScrollStitcher"/>.</summary>
internal enum StitchStatus
{
    /// <summary>The frame scrolled and its new rows were appended.</summary>
    Appended,
    /// <summary>The scrolling area did not change, usually because the end of the content was reached.</summary>
    NoChange,
    /// <summary>The frame changed but no reliable vertical overlap with the previous frame was found.</summary>
    NoMatch,
    /// <summary>New rows were appended up to the maximum height; no further frames are accepted.</summary>
    ReachedMaxHeight
}

internal readonly record struct StitchStepResult(StitchStatus Status, int Shift, int AppendedRows);

/// <summary>
/// Pure, UI-free stitching of consecutive same-size BGRA frames from a vertically scrolling region.
/// Overlap is found by matching per-row signatures (downsampled grayscale) with a coarse sampled pass
/// followed by a full-row refinement. Rows that are identical at the top (sticky headers, title bars) or
/// bottom (sticky footers, status bars) of consecutive frames are treated as fixed bands: the header appears
/// once at the top of the result and the footer once at the bottom.
/// </summary>
internal sealed class ScrollStitcher
{
    internal const int DefaultMaxHeight = 20000;
    private const int SignatureColumns = 128;
    private const int CoarseSampleRows = 72;
    private const int RefineCandidates = 6;
    private const double IdenticalRowTolerance = 0.75;
    private const double MatchTolerance = 6.0;
    private const double TieTolerance = 0.35;
    private const double MinimumOverlapFraction = 0.12;
    private const int MinimumOverlapRows = 12;

    private readonly int _width;
    private readonly int _height;
    private readonly int _maxHeight;
    private readonly int _rowBytes;
    private readonly List<byte[]> _scrolledRows = [];
    private byte[]? _firstFrame;
    private byte[]? _lastFrame;
    private float[][]? _lastSignatures;
    private int _footerRows = -1;
    private int _previousShift;

    internal ScrollStitcher(int width, int height, int maxHeight = DefaultMaxHeight)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Frames must have a positive size.");
        _width = width;
        _height = height;
        _maxHeight = Math.Max(height, maxHeight);
        _rowBytes = width * 4;
    }

    internal int Width => _width;
    internal int FrameHeight => _height;
    internal int MaxHeight => _maxHeight;
    internal int FrameCount { get; private set; }
    internal int TotalAppendedRows { get; private set; }
    internal bool IsFull { get; private set; }
    /// <summary>Height of the stitched image if it were composed now.</summary>
    internal int ResultHeight => _firstFrame is null ? 0 : _height + TotalAppendedRows;
    /// <summary>Fixed footer rows detected on the first comparison, or 0 before then.</summary>
    internal int FooterRows => Math.Max(0, _footerRows);

    /// <summary>Adds a frame (top-down BGRA, stride = width * 4). The first frame always succeeds with <see cref="StitchStatus.Appended"/> and 0 rows.</summary>
    internal StitchStepResult AddFrame(byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length < _rowBytes * _height) throw new ArgumentException("The frame is smaller than the stitcher's frame size.", nameof(bgra));
        if (IsFull) return new StitchStepResult(StitchStatus.ReachedMaxHeight, 0, 0);
        float[][] signatures = ComputeSignatures(bgra, _width, _height);
        if (_firstFrame is null)
        {
            _firstFrame = (byte[])bgra.Clone();
            _lastFrame = _firstFrame;
            _lastSignatures = signatures;
            FrameCount = 1;
            return new StitchStepResult(StitchStatus.Appended, 0, 0);
        }

        float[][] previous = _lastSignatures!;
        (int header, int footer) = FixedBands(previous, signatures);
        if (header >= _height) return new StitchStepResult(StitchStatus.NoChange, 0, 0);
        // The footer is fixed by the first frame that actually scrolls so composition stays consistent.
        int footerRows = _footerRows >= 0 ? _footerRows : Math.Min(footer, _height / 3);
        int top = Math.Min(header, _height / 2);
        int bottom = _height - footerRows;
        if (bottom - top < MinimumOverlapRows * 2) return new StitchStepResult(StitchStatus.NoMatch, 0, 0);

        int? shift = FindShift(previous, signatures, top, bottom, _previousShift);
        if (shift is not int found) return new StitchStepResult(StitchStatus.NoMatch, 0, 0);
        if (found == 0) return new StitchStepResult(StitchStatus.NoChange, 0, 0);

        _footerRows = footerRows;
        int newRowsStart = bottom - found;
        int allowed = _maxHeight - ResultHeight;
        int appended = Math.Min(found, Math.Max(0, allowed));
        for (int row = 0; row < appended; row++)
            _scrolledRows.Add(CopyRow(bgra, newRowsStart + row));
        TotalAppendedRows += appended;
        _previousShift = found;
        _lastFrame = (byte[])bgra.Clone();
        _lastSignatures = signatures;
        FrameCount++;
        if (appended < found || ResultHeight >= _maxHeight)
        {
            IsFull = true;
            return new StitchStepResult(StitchStatus.ReachedMaxHeight, found, appended);
        }
        return new StitchStepResult(StitchStatus.Appended, found, appended);
    }

    /// <summary>Composes header + scrolled content + footer into one top-down BGRA buffer.</summary>
    internal (byte[] Pixels, int Width, int Height) Compose()
    {
        if (_firstFrame is null || _lastFrame is null) throw new InvalidOperationException("No frames were added.");
        int footer = TotalAppendedRows > 0 ? FooterRows : 0;
        int height = ResultHeight;
        var output = new byte[_rowBytes * height];
        int firstPart = _height - footer;
        Buffer.BlockCopy(_firstFrame, 0, output, 0, firstPart * _rowBytes);
        int offset = firstPart * _rowBytes;
        foreach (byte[] row in _scrolledRows)
        {
            Buffer.BlockCopy(row, 0, output, offset, _rowBytes);
            offset += _rowBytes;
        }
        if (footer > 0) Buffer.BlockCopy(_lastFrame, (_height - footer) * _rowBytes, output, offset, footer * _rowBytes);
        return (output, _width, height);
    }

    private byte[] CopyRow(byte[] frame, int row)
    {
        var copy = new byte[_rowBytes];
        Buffer.BlockCopy(frame, row * _rowBytes, copy, 0, _rowBytes);
        return copy;
    }

    /// <summary>Counts identical leading and trailing rows. Returns (height, 0) when every row is identical.</summary>
    internal static (int Header, int Footer) FixedBands(float[][] previous, float[][] current)
    {
        int height = Math.Min(previous.Length, current.Length);
        int header = 0;
        while (header < height && RowDistance(previous[header], current[header]) <= IdenticalRowTolerance) header++;
        if (header == height) return (height, 0);
        int footer = 0;
        while (footer < height - header && RowDistance(previous[height - 1 - footer], current[height - 1 - footer]) <= IdenticalRowTolerance) footer++;
        return (header, footer);
    }

    /// <summary>
    /// Finds d such that current[y] matches previous[y + d] for rows in [top, bottom - d). Returns 0 when the band
    /// is unchanged, null when no shift is reliable. Near-ties prefer the shift closest to <paramref name="expectedShift"/>.
    /// </summary>
    internal static int? FindShift(float[][] previous, float[][] current, int top, int bottom, int expectedShift = 0)
    {
        int band = bottom - top;
        if (band <= 0) return null;
        if (BandDistance(previous, current, top, bottom, 0, sampleRows: 0) <= IdenticalRowTolerance) return 0;
        int minimumOverlap = Math.Max(MinimumOverlapRows, (int)Math.Ceiling(band * MinimumOverlapFraction));
        int maximumShift = band - minimumOverlap;
        if (maximumShift < 1) return null;

        var coarse = new List<(int Shift, double Cost)>(maximumShift);
        for (int shift = 1; shift <= maximumShift; shift++)
            coarse.Add((shift, BandDistance(previous, current, top, bottom, shift, CoarseSampleRows)));
        var refined = coarse.OrderBy(candidate => candidate.Cost).Take(RefineCandidates)
            .Select(candidate => (candidate.Shift, Cost: BandDistance(previous, current, top, bottom, candidate.Shift, sampleRows: 0)))
            .ToList();
        double best = refined.Min(candidate => candidate.Cost);
        if (best > MatchTolerance) return null;
        var ties = refined.Where(candidate => candidate.Cost <= best + TieTolerance).Select(candidate => candidate.Shift).ToList();
        if (ties.Count == 1) return ties[0];
        // Uniform content (blank rows) matches several shifts equally well; the scroll step is usually stable.
        return expectedShift > 0
            ? ties.OrderBy(shift => Math.Abs(shift - expectedShift)).First()
            : ties.Min();
    }

    /// <summary>Mean absolute difference between current[y] and previous[y + shift] over the overlap; sampleRows 0 compares every row.</summary>
    private static double BandDistance(float[][] previous, float[][] current, int top, int bottom, int shift, int sampleRows)
    {
        int overlap = bottom - top - shift;
        if (overlap <= 0) return double.MaxValue;
        int step = sampleRows <= 0 || overlap <= sampleRows ? 1 : overlap / sampleRows;
        double total = 0;
        int count = 0;
        for (int row = top; row < top + overlap; row += step)
        {
            total += RowDistance(current[row], previous[row + shift]);
            count++;
        }
        return count == 0 ? double.MaxValue : total / count;
    }

    private static double RowDistance(float[] first, float[] second)
    {
        double total = 0;
        for (int column = 0; column < first.Length; column++) total += Math.Abs(first[column] - second[column]);
        return total / first.Length;
    }

    /// <summary>Per-row grayscale signatures averaged into at most <see cref="SignatureColumns"/> horizontal bins.</summary>
    internal static float[][] ComputeSignatures(byte[] bgra, int width, int height)
    {
        int bins = Math.Min(SignatureColumns, width);
        var signatures = new float[height][];
        var binStart = new int[bins + 1];
        for (int bin = 0; bin <= bins; bin++) binStart[bin] = (int)((long)bin * width / bins);
        for (int row = 0; row < height; row++)
        {
            var signature = new float[bins];
            int rowOffset = row * width * 4;
            for (int bin = 0; bin < bins; bin++)
            {
                int start = binStart[bin], end = binStart[bin + 1];
                float sum = 0;
                for (int column = start; column < end; column++)
                {
                    int pixel = rowOffset + column * 4;
                    sum += bgra[pixel] * 0.114f + bgra[pixel + 1] * 0.587f + bgra[pixel + 2] * 0.299f;
                }
                signature[bin] = sum / Math.Max(1, end - start);
            }
            signatures[row] = signature;
        }
        return signatures;
    }
}
