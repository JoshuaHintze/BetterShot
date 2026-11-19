namespace BetterShot.RecordingEditor;

/// <summary>One stretch of the source recording, in source seconds. Cut stretches stay on the timeline so they can be put back.</summary>
internal sealed record TimelineSegment(double Start, double End, bool Kept)
{
    internal double Duration => End - Start;
    internal bool Contains(double time) => time >= Start && time < End;
}

/// <summary>A kept stretch of source time and where it lands in the edited (output) timeline.</summary>
internal readonly record struct KeptRange(double Start, double End, double OutputStart)
{
    internal double Duration => End - Start;
    internal double OutputEnd => OutputStart + Duration;
}

/// <summary>
/// The non-destructive cut list of a recording: contiguous segments covering [0, Duration], each kept or cut, plus
/// optional in/out points. Every operation returns a new timeline so undo/redo can keep plain snapshots. All times are
/// source seconds; <see cref="KeptRanges"/> maps them to the edited output.
/// </summary>
internal sealed record EditTimeline
{
    /// <summary>Splits closer than this to an existing edge are ignored (shorter than a frame at 120 fps).</summary>
    internal const double MinimumSegment = 1.0 / 120;

    private EditTimeline(double duration, IReadOnlyList<TimelineSegment> segments, double? inPoint, double? outPoint)
    {
        Duration = duration;
        Segments = segments;
        InPoint = inPoint;
        OutPoint = outPoint;
    }

    internal double Duration { get; }
    internal IReadOnlyList<TimelineSegment> Segments { get; }
    /// <summary>Null means the start of the recording.</summary>
    internal double? InPoint { get; }
    /// <summary>Null means the end of the recording.</summary>
    internal double? OutPoint { get; }

    internal double EffectiveIn => InPoint ?? 0;
    internal double EffectiveOut => OutPoint ?? Duration;

    internal static EditTimeline Create(double duration)
    {
        double safe = double.IsFinite(duration) && duration > 0 ? duration : MinimumSegment;
        return new EditTimeline(safe, [new TimelineSegment(0, safe, true)], null, null);
    }

    /// <summary>Rebuilds a stored timeline, repairing gaps, overlaps, and out-of-range values instead of failing.</summary>
    internal static EditTimeline Restore(double duration, IEnumerable<TimelineSegment> stored, double? inPoint, double? outPoint)
    {
        EditTimeline timeline = Create(duration);
        foreach (TimelineSegment segment in stored.Where(item => double.IsFinite(item.Start) && double.IsFinite(item.End)).OrderBy(item => item.Start))
        {
            timeline = timeline.SplitAt(segment.Start).SplitAt(segment.End);
            if (!segment.Kept) timeline = timeline.SetRangeKept(segment.Start, segment.End, false);
        }
        return timeline.WithInPoint(inPoint).WithOutPoint(outPoint);
    }

    /// <summary>True when nothing is cut and no in/out point is set.</summary>
    internal bool IsUnedited => Segments.All(segment => segment.Kept) && InPoint is null && OutPoint is null;

    internal bool HasCuts => !IsUnedited;

    internal int IndexAt(double time)
    {
        double clamped = Math.Clamp(time, 0, Duration);
        for (int index = 0; index < Segments.Count; index++)
            if (Segments[index].Contains(clamped)) return index;
        return Segments.Count - 1;
    }

    internal TimelineSegment SegmentAt(double time) => Segments[IndexAt(time)];

    internal EditTimeline SplitAt(double time)
    {
        if (!double.IsFinite(time)) return this;
        int index = IndexAt(time);
        TimelineSegment segment = Segments[index];
        if (time - segment.Start < MinimumSegment || segment.End - time < MinimumSegment) return this;
        var segments = Segments.ToList();
        segments[index] = segment with { End = time };
        segments.Insert(index + 1, segment with { Start = time });
        return With(segments);
    }

    internal EditTimeline SetKept(int index, bool kept)
    {
        if (index < 0 || index >= Segments.Count || Segments[index].Kept == kept) return this;
        var segments = Segments.ToList();
        segments[index] = segments[index] with { Kept = kept };
        return With(segments);
    }

    /// <summary>Cuts (or keeps) [start, end], splitting at both edges first.</summary>
    internal EditTimeline SetRangeKept(double start, double end, bool kept)
    {
        double from = Math.Clamp(Math.Min(start, end), 0, Duration), to = Math.Clamp(Math.Max(start, end), 0, Duration);
        if (to - from < MinimumSegment) return this;
        EditTimeline split = SplitAt(from).SplitAt(to);
        var segments = split.Segments
            .Select(segment => segment.Start >= from - 1e-9 && segment.End <= to + 1e-9 ? segment with { Kept = kept } : segment)
            .ToList();
        return split.With(segments);
    }

    internal EditTimeline CutRange(double start, double end) => SetRangeKept(start, end, false);

    /// <summary>Q: removes the kept clip under the playhead from its start up to the playhead.</summary>
    internal EditTimeline DeleteFromClipStart(double playhead)
    {
        TimelineSegment clip = SegmentAt(playhead);
        return clip.Kept ? CutRange(clip.Start, playhead) : this;
    }

    /// <summary>W: removes the kept clip under the playhead from the playhead to its end.</summary>
    internal EditTimeline DeleteToClipEnd(double playhead)
    {
        TimelineSegment clip = SegmentAt(playhead);
        return clip.Kept ? CutRange(playhead, clip.End) : this;
    }

    /// <summary>"Put the recording back the way it was": no cuts, no splits, no in/out points.</summary>
    internal EditTimeline Reset() => Create(Duration);

    internal EditTimeline WithInPoint(double? time)
    {
        if (time is not double value || !double.IsFinite(value) || value <= MinimumSegment) return new EditTimeline(Duration, Segments, null, OutPoint);
        double clamped = Math.Min(value, Duration);
        double? outPoint = OutPoint is double existing && existing - clamped < MinimumSegment ? null : OutPoint;
        return new EditTimeline(Duration, Segments, clamped, outPoint);
    }

    internal EditTimeline WithOutPoint(double? time)
    {
        if (time is not double value || !double.IsFinite(value) || value >= Duration - MinimumSegment) return new EditTimeline(Duration, Segments, InPoint, null);
        double clamped = Math.Max(value, 0);
        double? inPoint = InPoint is double existing && clamped - existing < MinimumSegment ? null : InPoint;
        return new EditTimeline(Duration, Segments, inPoint, clamped);
    }

    /// <summary>Kept stretches inside the in/out points, in order, with their output positions.</summary>
    internal IReadOnlyList<KeptRange> KeptRanges
    {
        get
        {
            var ranges = new List<KeptRange>();
            double output = 0, from = EffectiveIn, to = EffectiveOut;
            foreach (TimelineSegment segment in Segments.Where(segment => segment.Kept))
            {
                double start = Math.Max(segment.Start, from), end = Math.Min(segment.End, to);
                if (end - start < 0.001) continue;
                ranges.Add(new KeptRange(start, end, output));
                output += end - start;
            }
            return ranges;
        }
    }

    internal double OutputDuration => KeptRanges.Sum(range => range.Duration);

    /// <summary>Where a source time lands in the output, or null when it is cut or outside the in/out points.</summary>
    internal double? SourceToOutput(double source)
    {
        foreach (KeptRange range in KeptRanges)
            if (source >= range.Start && source < range.End) return range.OutputStart + (source - range.Start);
        IReadOnlyList<KeptRange> all = KeptRanges;
        if (all.Count > 0 && Math.Abs(source - all[^1].End) < 1e-9) return all[^1].OutputEnd;
        return null;
    }

    internal double OutputToSource(double output)
    {
        IReadOnlyList<KeptRange> ranges = KeptRanges;
        if (ranges.Count == 0) return EffectiveIn;
        foreach (KeptRange range in ranges)
            if (output < range.OutputEnd) return range.Start + Math.Max(0, output - range.OutputStart);
        return ranges[^1].End;
    }

    /// <summary>The playable source time at or after <paramref name="source"/>, or null past the last kept stretch.</summary>
    internal double? NextPlayable(double source)
    {
        foreach (KeptRange range in KeptRanges)
        {
            if (source < range.Start) return range.Start;
            if (source < range.End) return source;
        }
        return null;
    }

    /// <summary>The playable source time at or before <paramref name="source"/>, or null before the first kept stretch.</summary>
    internal double? PreviousPlayable(double source)
    {
        foreach (KeptRange range in KeptRanges.Reverse())
        {
            if (source >= range.End) return Math.Max(range.Start, range.End - 0.001);
            if (source >= range.Start) return source;
        }
        return null;
    }

    internal double FirstPlayable => KeptRanges.Count > 0 ? KeptRanges[0].Start : EffectiveIn;
    internal double LastPlayable => KeptRanges.Count > 0 ? KeptRanges[^1].End : EffectiveOut;

    /// <summary>Every segment edge and in/out point, for snapping.</summary>
    internal IEnumerable<double> Edges()
    {
        foreach (TimelineSegment segment in Segments) yield return segment.Start;
        yield return Duration;
        if (InPoint is double inPoint) yield return inPoint;
        if (OutPoint is double outPoint) yield return outPoint;
    }

    private EditTimeline With(List<TimelineSegment> segments) => new(Duration, segments, InPoint, OutPoint);
}

internal static class TimelineSnapping
{
    /// <summary>The nearest target within <paramref name="tolerance"/> seconds, or the value itself.</summary>
    internal static double Snap(double value, IEnumerable<double> targets, double tolerance)
    {
        double best = value, bestDistance = tolerance;
        foreach (double target in targets)
        {
            double distance = Math.Abs(target - value);
            if (distance <= bestDistance) { best = target; bestDistance = distance; }
        }
        return best;
    }
}
