using BetterShot.Editing;
using System.Windows.Media;

namespace BetterShot.RecordingEditor;

internal enum MarkKind { Text, Arrow, Rectangle, Ellipse, Line, Highlighter, Callout, Step, Spotlight, Redaction }

/// <summary>What a followed redaction does once tracking can no longer find what it covers.</summary>
internal enum LostFieldPolicy { StopFollowing, BlurWholePicture }

/// <summary>Where a followed redaction was at <see cref="Time"/> (source seconds), in video pixels.</summary>
internal sealed record TrackKeyframe(double Time, Rect Bounds);

/// <summary>
/// One piece of markup on the recording: an editor annotation (reused for drawing) shown from <see cref="Start"/> for
/// <see cref="Duration"/> source seconds. Redactions can follow what they cover; the path is a list of keyframes.
/// Coordinates are video pixels of the original recording.
/// </summary>
internal sealed record VideoMark(Guid Id, Annotation Shape, double Start, double Duration)
{
    internal const double DefaultDuration = 3;
    internal const double MinimumDuration = 0.1;

    public bool Tracked { get; init; }
    public IReadOnlyList<TrackKeyframe> Keyframes { get; init; } = [];
    /// <summary>Source time where tracking lost the content, if it did.</summary>
    public double? LostAt { get; init; }
    public LostFieldPolicy WhenLost { get; init; } = LostFieldPolicy.StopFollowing;

    internal double End => Start + Duration;
    internal bool IsActiveAt(double time) => time >= Start && time < End;
    internal bool IsRedaction => Shape is RectangleAnnotation { Redaction: true };
    internal bool NeedsSourcePixels => IsRedaction && Shape is RectangleAnnotation { RedactionMode: not RedactionStyle.Solid };

    internal MarkKind Kind => Shape switch
    {
        TextAnnotation { IsCallout: true } => MarkKind.Callout,
        TextAnnotation => MarkKind.Text,
        ArrowAnnotation => MarkKind.Arrow,
        RectangleAnnotation { Redaction: true } => MarkKind.Redaction,
        RectangleAnnotation => MarkKind.Rectangle,
        EllipseAnnotation => MarkKind.Ellipse,
        LineAnnotation => MarkKind.Line,
        HighlighterAnnotation => MarkKind.Highlighter,
        StepAnnotation => MarkKind.Step,
        SpotlightAnnotation => MarkKind.Spotlight,
        _ => MarkKind.Rectangle
    };

    internal string Label => Kind switch
    {
        MarkKind.Text => Shape is TextAnnotation text && text.Text.Length > 0 ? text.Text.Split('\n')[0] : "Text",
        MarkKind.Callout => Shape is TextAnnotation callout && callout.Text.Length > 0 ? callout.Text.Split('\n')[0] : "Callout",
        MarkKind.Step => Shape is StepAnnotation step ? $"Step {step.Number}" : "Step",
        MarkKind.Redaction => Tracked ? "Redaction (follows)" : "Redaction",
        _ => Kind.ToString()
    };

    internal Color BarColor => Kind switch
    {
        MarkKind.Redaction => Color.FromRgb(0x8A, 0x8F, 0x99),
        MarkKind.Spotlight => Color.FromRgb(0xC9, 0xB8, 0x6A),
        _ => Color.FromArgb(255, Shape.Color.R, Shape.Color.G, Shape.Color.B)
    };

    /// <summary>The stationary box this redaction is drawn in when it is not followed.</summary>
    internal Rect? BaseBounds => Shape is RectangleAnnotation rectangle ? rectangle.Bounds : null;

    /// <summary>Where a followed redaction is at <paramref name="time"/>; linear between keyframes, held before/after.</summary>
    internal Rect? BoundsAt(double time)
    {
        if (BaseBounds is not Rect stationary) return null;
        if (!Tracked || Keyframes.Count == 0) return stationary;
        if (time <= Keyframes[0].Time) return Keyframes[0].Bounds;
        for (int index = 1; index < Keyframes.Count; index++)
        {
            TrackKeyframe next = Keyframes[index];
            if (time > next.Time) continue;
            TrackKeyframe previous = Keyframes[index - 1];
            double span = next.Time - previous.Time;
            double progress = span <= 1e-9 ? 1 : (time - previous.Time) / span;
            return Lerp(previous.Bounds, next.Bounds, progress);
        }
        return Keyframes[^1].Bounds;
    }

    /// <summary>The annotation to draw at <paramref name="time"/>, moved along the tracked path when followed.</summary>
    internal Annotation ShapeAt(double time) =>
        Tracked && Shape is RectangleAnnotation rectangle && BoundsAt(time) is Rect bounds ? rectangle with { Bounds = bounds } : Shape;

    /// <summary>True while a followed redaction has lost its content and the policy blurs the whole picture.</summary>
    internal bool BlursWholePictureAt(double time) =>
        Tracked && WhenLost == LostFieldPolicy.BlurWholePicture && LostAt is double lost && time >= lost && IsActiveAt(time);

    internal VideoMark WithTiming(double start, double duration, double recordingDuration)
    {
        double safeDuration = Math.Max(MinimumDuration, double.IsFinite(duration) ? duration : DefaultDuration);
        double safeStart = Math.Clamp(double.IsFinite(start) ? start : 0, 0, Math.Max(0, recordingDuration - MinimumDuration));
        safeDuration = Math.Min(safeDuration, Math.Max(MinimumDuration, recordingDuration - safeStart));
        return this with { Start = safeStart, Duration = safeDuration };
    }

    /// <summary>A new mark placed at the playhead with the default three-second bar (shortened at the end of the recording).</summary>
    internal static VideoMark CreateAt(Annotation shape, double playhead, double recordingDuration) =>
        new VideoMark(Guid.NewGuid(), shape, 0, DefaultDuration).WithTiming(playhead, DefaultDuration, recordingDuration);

    internal static Rect Lerp(Rect from, Rect to, double progress)
    {
        double amount = Math.Clamp(progress, 0, 1);
        return new Rect(
            from.X + (to.X - from.X) * amount, from.Y + (to.Y - from.Y) * amount,
            Math.Max(0, from.Width + (to.Width - from.Width) * amount), Math.Max(0, from.Height + (to.Height - from.Height) * amount));
    }
}

/// <summary>Timeline rows for mark bars: every mark on its own row, or all folded into one.</summary>
internal static class MarkRows
{
    internal static IReadOnlyDictionary<Guid, int> Assign(IEnumerable<VideoMark> marks, bool ownRows)
    {
        var rows = new Dictionary<Guid, int>();
        int row = 0;
        foreach (VideoMark mark in marks.OrderBy(mark => mark.Start).ThenBy(mark => mark.Id))
            rows[mark.Id] = ownRows ? row++ : 0;
        return rows;
    }

    internal static int RowCount(IEnumerable<VideoMark> marks, bool ownRows)
    {
        int count = marks.Count();
        return count == 0 ? 0 : ownRows ? count : 1;
    }
}
