using BetterShot.Controls;
using BetterShot.Recording;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.RecordingEditor;

/// <summary>One audio lane under the video: a separate track (System, Mic) or, for older recordings, the whole soundtrack.</summary>
internal sealed record TimelineAudioLane(string Key, string Label, LucideIconKind Icon, WaveformPeaks? Peaks, bool Muted);

/// <summary>What the timeline reads from, and reports to, the recording editor.</summary>
internal interface IRecordingTimelineHost
{
    RecordingEditState State { get; }
    double Playhead { get; }
    Guid? SelectedMarkId { get; }
    int? SelectedSegmentIndex { get; }
    bool SplitMode { get; }
    bool Snapping { get; }
    bool MarksOnOwnRows { get; }
    IReadOnlyList<RecordingEvent> Events { get; }
    IReadOnlyList<TimelineAudioLane> AudioLanes { get; }
    FilmstripCache Filmstrip { get; }
    double VideoAspect { get; }
    void TimelineSeek(double time);
    void TimelineSplit(double time);
    void TimelineSelectSegment(int index);
    void TimelineSelectMark(Guid id);
    void TimelineClearSelection();
    void TimelineMoveMark(Guid id, double start, double duration);
    void TimelineMoveTrimHandle(bool inHandle, double time);
    void TimelineDragFinished();
}

/// <summary>
/// The editing timeline (KhingShot layout): a time ruler with the playhead's time bubble, the video track as a
/// filmstrip inside a yellow clip whose ends are the in/out trim handles (cuts show as gaps), one waveform lane per
/// audio track, a lane of recorded clicks and shortcuts, and one bar per mark (own rows, or folded into one).
/// Drawn in OnRender for speed; the wheel scrolls, Ctrl+wheel zooms, and <see cref="Fit"/> shows the whole recording.
/// Track names and mute buttons live in a separate label column that reads <see cref="LaneTops"/>.
/// </summary>
internal sealed class RecordingTimeline : FrameworkElement
{
    internal const double RulerHeight = 26, VideoTop = 30, VideoHeight = 52, LaneGap = 6, AudioHeight = 28, EventsHeight = 20, RowHeight = 20, RowGap = 3, SidePadding = 14;
    internal const double MaximumPixelsPerSecond = 2000;
    private const double HandleWidth = 11, EdgeGrip = 6, SnapPixels = 8, BarStep = 3;
    private static readonly double[] TickSteps = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800];
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface MonoFace = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush RulerText = Brush(0x8E, 0x92, 0x9A);
    private static readonly Pen TickPen = new(Brush(0x3A, 0x3C, 0x41), 1);
    private static readonly Pen MinorTickPen = new(Brush(0x2E, 0x30, 0x34), 1);
    private static readonly Brush ClipYellow = Brush(0xFF, 0xD4, 0x00);
    private static readonly Brush HandleGrip = Brush(0x5A, 0x48, 0x00);
    private static readonly Brush EmptyClip = Brush(0x2B, 0x2D, 0x31);
    private static readonly Brush Dimmed = Frozen(new SolidColorBrush(Color.FromArgb(170, 0x10, 0x11, 0x13)));
    private static readonly Brush CutHatch = CreateHatch();
    private static readonly Pen SplitLine = new(Brush(0x1A, 0x1B, 0x1D), 2);
    private static readonly Pen SelectedPen = new(Brushes.White, 2);
    private static readonly Pen MarkSelectedPen = new(Brush(0xFF, 0xF0, 0x7A), 2);
    private static readonly Brush LaneFill = Brush(0x24, 0x25, 0x28);
    private static readonly Brush WaveBrush = Brush(0xC9, 0xCC, 0xD2);
    private static readonly Brush WaveDimBrush = Brush(0x5C, 0x5F, 0x66);
    private static readonly Brush WaveMutedBrush = Brush(0x46, 0x48, 0x4D);
    private static readonly Pen PlayheadPen = new(Brush(0xF4, 0xF4, 0xF5), 1.5);
    private static readonly Pen SplitHoverPen = new(Brushes.White, 1) { DashStyle = DashStyles.Dash };
    private static readonly Brush ClickBrush = Brush(0xC9, 0xCC, 0xD2);
    private static readonly Brush KeyBrush = Brush(0xFF, 0xC8, 0x57);
    private static readonly Brush LostBrush = Brush(0xFF, 0x5A, 0x5F);

    private readonly IRecordingTimelineHost _host;
    private double _viewStart;
    private double _pixelsPerSecond = 50;
    private double _marksScroll;
    private bool _fitted = true;
    private DragMode _drag;
    private Guid _dragMark;
    private double _dragGrabOffset;
    private Point _pointerDown;
    private bool _moved;
    private double? _hoverTime;

    private enum DragMode { None, Scrub, MoveMark, MarkStart, MarkEnd, TrimIn, TrimOut }

    internal RecordingTimeline(IRecordingTimelineHost host)
    {
        _host = host;
        Focusable = false;
        ClipToBounds = true;
        SizeChanged += (_, _) => { if (_fitted) Fit(); else InvalidateVisual(); };
        _host.Filmstrip.Updated += (_, _) => InvalidateVisual();
    }

    /// <summary>Raised when zoom or scroll changes (the zoom slider follows it).</summary>
    internal event EventHandler? ViewChanged;

    private double Duration => _host.State.Timeline.Duration;
    private double TrackWidth => Math.Max(1, ActualWidth - SidePadding * 2);
    private double MinimumPixelsPerSecond => TrackWidth / Math.Max(0.1, Duration);

    internal double PixelsPerSecond => _pixelsPerSecond;

    // ---------- Lane geometry (shared with the label column) ----------

    internal static double AudioTop(int lane) => VideoTop + VideoHeight + LaneGap + lane * (AudioHeight + LaneGap);
    internal static double EventsTop(int audioLanes) => AudioTop(audioLanes);
    internal static double MarksTop(int audioLanes) => EventsTop(audioLanes) + EventsHeight + LaneGap;

    /// <summary>Total height that shows every lane and <paramref name="markRows"/> rows of marks.</summary>
    internal static double ContentHeight(int audioLanes, int markRows) => MarksTop(audioLanes) + Math.Max(1, markRows) * (RowHeight + RowGap) + 6;

    private int AudioLaneCount => _host.AudioLanes.Count;
    private double MarksTopY => MarksTop(AudioLaneCount);

    // ---------- Zoom and scroll ----------

    internal void Fit()
    {
        _fitted = true;
        _viewStart = 0;
        _pixelsPerSecond = MinimumPixelsPerSecond;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ZoomBy(double factor, double? anchorX = null) => ZoomTo(_pixelsPerSecond * factor, anchorX);

    /// <summary>0 = the whole recording fits; 1 = the closest zoom. Logarithmic, like the zoom slider.</summary>
    internal double ZoomFraction
    {
        get
        {
            double minimum = MinimumPixelsPerSecond;
            if (MaximumPixelsPerSecond <= minimum * 1.0001) return 0;
            return Math.Clamp(Math.Log(_pixelsPerSecond / minimum) / Math.Log(MaximumPixelsPerSecond / minimum), 0, 1);
        }
        set
        {
            double minimum = MinimumPixelsPerSecond;
            if (MaximumPixelsPerSecond <= minimum) { Fit(); return; }
            ZoomTo(minimum * Math.Pow(MaximumPixelsPerSecond / minimum, Math.Clamp(value, 0, 1)), null);
        }
    }

    private void ZoomTo(double pixelsPerSecond, double? anchorX)
    {
        double anchor = anchorX ?? Math.Clamp(TimeToX(_host.Playhead), SidePadding, ActualWidth - SidePadding);
        double anchorTime = XToTime(anchor);
        double minimum = MinimumPixelsPerSecond;
        _pixelsPerSecond = Math.Clamp(pixelsPerSecond, minimum, Math.Max(minimum, MaximumPixelsPerSecond));
        _fitted = _pixelsPerSecond <= minimum * 1.0001;
        _viewStart = anchorTime - (anchor - SidePadding) / _pixelsPerSecond;
        ClampView();
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Keeps the playhead on screen while playing by paging the view.</summary>
    internal void FollowPlayhead()
    {
        double x = TimeToX(_host.Playhead);
        if (x < SidePadding || x > ActualWidth - SidePadding)
        {
            _viewStart = _host.Playhead - TrackWidth * 0.1 / _pixelsPerSecond;
            ClampView();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
        InvalidateVisual();
    }

    internal double TimeToX(double time) => SidePadding + (time - _viewStart) * _pixelsPerSecond;
    internal double XToTime(double x) => Math.Clamp(_viewStart + (x - SidePadding) / _pixelsPerSecond, 0, Duration);

    private void ClampView()
    {
        double visible = TrackWidth / _pixelsPerSecond;
        _viewStart = Math.Clamp(_viewStart, 0, Math.Max(0, Duration - visible));
    }

    // ---------- Drawing ----------

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Duration <= 0) return;
        DrawRuler(dc);
        DrawVideo(dc);
        DrawAudioLanes(dc);
        DrawEvents(dc);
        DrawMarks(dc);
        if (_host.SplitMode && _hoverTime is double hover)
        {
            double x = TimeToX(hover);
            dc.DrawLine(SplitHoverPen, new Point(x, VideoTop - 2), new Point(x, VideoTop + VideoHeight + 2));
        }
        DrawPlayhead(dc);
    }

    private void DrawRuler(DrawingContext dc)
    {
        double step = TickSteps.FirstOrDefault(candidate => candidate * _pixelsPerSecond >= 84, TickSteps[^1]);
        double minor = step / 4;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double first = Math.Floor(_viewStart / minor) * minor;
        for (double time = first; time <= Duration + 1e-9; time += minor)
        {
            double x = TimeToX(time);
            if (x > ActualWidth) break;
            if (x < 0) continue;
            bool major = Math.Abs(time / step - Math.Round(time / step)) < 1e-6;
            if (!major) { dc.DrawLine(MinorTickPen, new Point(x, RulerHeight - 4), new Point(x, RulerHeight)); continue; }
            dc.DrawLine(TickPen, new Point(x, RulerHeight - 8), new Point(x, RulerHeight));
            if (time < 1e-9) continue; // The playhead bubble sits at 0:00.
            var label = new FormattedText(FormatTick(time, step), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoFace, 11, RulerText, dpi);
            dc.DrawText(label, new Point(x - label.Width / 2, 6));
        }
    }

    private void DrawVideo(DrawingContext dc)
    {
        EditTimeline timeline = _host.State.Timeline;
        var whole = new Rect(TimeToX(0), VideoTop, Math.Max(1, TimeToX(Duration) - TimeToX(0)), VideoHeight);
        dc.PushClip(new RectangleGeometry(whole, 6, 6));
        DrawFilmstrip(dc, whole);
        dc.Pop();
        double inTime = timeline.EffectiveIn, outTime = timeline.EffectiveOut;
        // Outside the in/out points, and cut stretches, stay visible but dimmed so they can be put back.
        if (inTime > 0) dc.DrawRectangle(Dimmed, null, new Rect(new Point(whole.Left, VideoTop), new Point(TimeToX(inTime), VideoTop + VideoHeight)));
        if (outTime < Duration) dc.DrawRectangle(Dimmed, null, new Rect(new Point(TimeToX(outTime), VideoTop), new Point(whole.Right, VideoTop + VideoHeight)));
        for (int index = 0; index < timeline.Segments.Count; index++)
        {
            TimelineSegment segment = timeline.Segments[index];
            double start = Math.Max(segment.Start, inTime), end = Math.Min(segment.End, outTime);
            if (segment.Kept || end <= start) continue;
            var gap = new Rect(new Point(TimeToX(start), VideoTop), new Point(TimeToX(end), VideoTop + VideoHeight));
            dc.DrawRectangle(Dimmed, null, gap);
            dc.DrawRectangle(CutHatch, null, gap);
            if (gap.Width > 36)
            {
                var label = new FormattedText("Cut", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(label, new Point(gap.X + 6, gap.Y + 4));
            }
            if (_host.SelectedSegmentIndex == index) dc.DrawRectangle(null, SelectedPen, gap);
        }
        DrawKeptClips(dc, timeline, inTime, outTime);
    }

    /// <summary>Yellow frames around each kept stretch; the outer ends are the trim handles, inner boundaries are splits.</summary>
    private void DrawKeptClips(DrawingContext dc, EditTimeline timeline, double inTime, double outTime)
    {
        List<(double Start, double End, int Index)> kept = timeline.Segments
            .Select((segment, index) => (Start: Math.Max(segment.Start, inTime), End: Math.Min(segment.End, outTime), Index: index, segment.Kept))
            .Where(item => item.Kept && item.End > item.Start)
            .Select(item => (item.Start, item.End, item.Index)).ToList();
        // Touching kept segments form one clip (a split, not a cut).
        var clips = new List<(double Start, double End)>();
        foreach ((double start, double end, _) in kept)
        {
            if (clips.Count > 0 && start - clips[^1].End < 1e-6) clips[^1] = (clips[^1].Start, end);
            else clips.Add((start, end));
        }
        foreach ((double start, double end) in clips)
        {
            var outer = new Rect(new Point(TimeToX(start) - (Math.Abs(start - inTime) < 1e-9 ? HandleWidth : 2), VideoTop - 2),
                new Point(TimeToX(end) + (Math.Abs(end - outTime) < 1e-9 ? HandleWidth : 2), VideoTop + VideoHeight + 2));
            var frame = new GeometryGroup { FillRule = FillRule.EvenOdd };
            frame.Children.Add(new RectangleGeometry(outer, 7, 7));
            var inner = new Rect(new Point(TimeToX(start), VideoTop + 1), new Point(TimeToX(end), VideoTop + VideoHeight - 1));
            if (inner.Width > 0) frame.Children.Add(new RectangleGeometry(inner, 3, 3));
            dc.DrawGeometry(ClipYellow, null, frame);
            if (Math.Abs(start - inTime) < 1e-9) DrawGrip(dc, outer.Left + HandleWidth / 2);
            if (Math.Abs(end - outTime) < 1e-9) DrawGrip(dc, outer.Right - HandleWidth / 2);
        }
        foreach ((double start, double end, int index) in kept)
        {
            if (start > inTime + 1e-9 && kept.Any(other => Math.Abs(other.End - start) < 1e-6))
                dc.DrawLine(SplitLine, new Point(TimeToX(start), VideoTop + 1), new Point(TimeToX(start), VideoTop + VideoHeight - 1));
            if (_host.SelectedSegmentIndex == index)
                dc.DrawRoundedRectangle(null, SelectedPen, new Rect(new Point(TimeToX(start) + 2, VideoTop + 3), new Point(Math.Max(TimeToX(start) + 3, TimeToX(end) - 2), VideoTop + VideoHeight - 3)), 3, 3);
        }
    }

    private static void DrawGrip(DrawingContext dc, double x) =>
        dc.DrawRoundedRectangle(HandleGrip, null, new Rect(x - 1.5, VideoTop + VideoHeight / 2 - 9, 3, 18), 1.5, 1.5);

    private void DrawFilmstrip(DrawingContext dc, Rect lane)
    {
        dc.DrawRectangle(EmptyClip, null, lane);
        double tileWidth = Math.Max(24, VideoHeight * Math.Clamp(_host.VideoAspect, 0.3, 4));
        int step = FilmstripLayout.Step(_pixelsPerSecond, tileWidth);
        double tileSeconds = FilmstripLayout.TileSeconds(step);
        double visibleStart = XToTime(0), visibleEnd = XToTime(ActualWidth) + tileSeconds;
        foreach (int index in FilmstripLayout.VisibleTiles(visibleStart, visibleEnd, Duration, tileSeconds))
        {
            var tile = new Rect(TimeToX(index * tileSeconds), lane.Top, Math.Max(1, tileSeconds * _pixelsPerSecond), lane.Height);
            if (_host.Filmstrip.Get(step, index) is not BitmapSource image) continue;
            // Fill the tile height and center-crop horizontally so the picture is never squashed.
            double drawnWidth = lane.Height * image.PixelWidth / Math.Max(1.0, image.PixelHeight);
            dc.PushClip(new RectangleGeometry(tile));
            dc.DrawImage(image, new Rect(tile.X + (tile.Width - drawnWidth) / 2, tile.Y, drawnWidth, tile.Height));
            dc.Pop();
        }
    }

    private void DrawAudioLanes(DrawingContext dc)
    {
        IReadOnlyList<TimelineAudioLane> lanes = _host.AudioLanes;
        EditTimeline timeline = _host.State.Timeline;
        double left = Math.Max(0, TimeToX(0)), right = Math.Min(ActualWidth, TimeToX(Duration));
        for (int lane = 0; lane < lanes.Count; lane++)
        {
            double top = AudioTop(lane);
            var box = new Rect(TimeToX(0), top, Math.Max(1, TimeToX(Duration) - TimeToX(0)), AudioHeight);
            dc.DrawRoundedRectangle(LaneFill, null, box, 6, 6);
            if (lanes[lane].Peaks is not WaveformPeaks peaks) continue;
            double middle = top + AudioHeight / 2, maximum = AudioHeight / 2 - 4;
            for (double x = left + 4; x < right - 4; x += BarStep)
            {
                double start = XToTime(x), end = XToTime(x + BarStep);
                bool audible = timeline.SourceToOutput((start + end) / 2) is not null;
                Brush brush = lanes[lane].Muted ? WaveMutedBrush : audible ? WaveBrush : WaveDimBrush;
                double half = WaveformEnvelope.BarFraction(peaks.PeakBetween(start, end)) * maximum;
                if (half < 0.75) dc.DrawRectangle(brush, null, new Rect(x, middle - 0.5, 1, 1)); // Silence: a dotted line.
                else dc.DrawRectangle(brush, null, new Rect(x, middle - half, 1.5, half * 2));
            }
        }
    }

    private void DrawEvents(DrawingContext dc)
    {
        double y = EventsTop(AudioLaneCount) + EventsHeight / 2;
        foreach (RecordingEvent item in _host.Events)
        {
            double x = TimeToX(item.Time);
            if (x < 0 || x > ActualWidth) continue;
            if (item.Kind == RecordingEvent.ClickKind) dc.DrawEllipse(ClickBrush, null, new Point(x, y), 3.5, 3.5);
            else if (item.Kind == RecordingEvent.KeyKind) dc.DrawRoundedRectangle(KeyBrush, null, new Rect(x - 3, y - 3, 6, 6), 1.5, 1.5);
        }
    }

    private void DrawMarks(DrawingContext dc)
    {
        RecordingEditState state = _host.State;
        IReadOnlyDictionary<Guid, int> rows = MarkRows.Assign(state.Marks, _host.MarksOnOwnRows);
        dc.PushClip(new RectangleGeometry(new Rect(0, MarksTopY - 2, ActualWidth, Math.Max(0, ActualHeight - MarksTopY + 2))));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (VideoMark mark in state.Marks.OrderBy(mark => mark.Id == _host.SelectedMarkId))
        {
            Rect bar = MarkRect(mark, rows[mark.Id]);
            if (bar.Right < 0 || bar.Left > ActualWidth || bar.Bottom < MarksTopY || bar.Top > ActualHeight) continue;
            bool selected = mark.Id == _host.SelectedMarkId;
            var fill = new SolidColorBrush(Color.FromArgb((byte)(state.HideMarkup && !mark.IsRedaction ? 90 : 220), mark.BarColor.R, mark.BarColor.G, mark.BarColor.B));
            dc.DrawRoundedRectangle(fill, selected ? MarkSelectedPen : null, bar, 5, 5);
            Color textColor = Editing.GeometryUtil.Contrast(mark.BarColor);
            var label = new FormattedText(mark.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11, new SolidColorBrush(textColor), dpi)
            {
                MaxTextWidth = Math.Max(1, bar.Width - 10), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis
            };
            if (bar.Width > 18) dc.DrawText(label, new Point(bar.X + 6, bar.Y + 3));
            foreach (TrackKeyframe key in mark.Keyframes) DrawDiamond(dc, TimeToX(key.Time), bar.Y + bar.Height / 2, textColor);
            if (mark.LostAt is double lost)
            {
                double x = TimeToX(lost);
                dc.DrawRectangle(LostBrush, null, new Rect(x - 1, bar.Y - 1, 2.5, bar.Height + 2));
            }
        }
        dc.Pop();
    }

    private void DrawPlayhead(DrawingContext dc)
    {
        double x = TimeToX(_host.Playhead);
        if (x < -2 || x > ActualWidth + 2) return;
        dc.DrawLine(PlayheadPen, new Point(x, 6), new Point(x, ActualHeight));
        var head = new StreamGeometry();
        using (StreamGeometryContext context = head.Open())
        {
            context.BeginFigure(new Point(x - 5, 2), true, true);
            context.LineTo(new Point(x + 5, 2), true, false);
            context.LineTo(new Point(x, 9), true, false);
        }
        head.Freeze();
        dc.DrawGeometry(PlayheadPen.Brush, null, head);
        var text = new FormattedText(RecordingTimeText.Tenths(_host.Playhead), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoFace, 11.5, Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double width = text.Width + 12;
        double left = x + 6 + width > ActualWidth ? x - 6 - width : x + 6;
        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(left, 3, width, 19), 4, 4);
        dc.DrawText(text, new Point(left + 6, 3 + (19 - text.Height) / 2));
    }

    private static void DrawDiamond(DrawingContext dc, double x, double y, Color color)
    {
        var diamond = new StreamGeometry();
        using (StreamGeometryContext context = diamond.Open())
        {
            context.BeginFigure(new Point(x, y - 4), true, true);
            context.LineTo(new Point(x + 4, y), true, false);
            context.LineTo(new Point(x, y + 4), true, false);
            context.LineTo(new Point(x - 4, y), true, false);
        }
        diamond.Freeze();
        dc.DrawGeometry(new SolidColorBrush(color), null, diamond);
    }

    private Rect MarkRect(VideoMark mark, int row)
    {
        double left = TimeToX(mark.Start), right = TimeToX(mark.End);
        return new Rect(left, MarksTopY + row * (RowHeight + RowGap) - _marksScroll, Math.Max(4, right - left), RowHeight);
    }

    // ---------- Pointer ----------

    private (VideoMark Mark, DragMode Mode)? HitMark(Point point)
    {
        if (point.Y < MarksTopY - 2) return null;
        IReadOnlyDictionary<Guid, int> rows = MarkRows.Assign(_host.State.Marks, _host.MarksOnOwnRows);
        foreach (VideoMark mark in _host.State.Marks.OrderByDescending(mark => mark.Id == _host.SelectedMarkId).ThenByDescending(mark => mark.Start))
        {
            Rect bar = MarkRect(mark, rows[mark.Id]);
            if (!bar.Contains(point)) continue;
            if (point.X - bar.Left <= EdgeGrip) return (mark, DragMode.MarkStart);
            if (bar.Right - point.X <= EdgeGrip) return (mark, DragMode.MarkEnd);
            return (mark, DragMode.MoveMark);
        }
        return null;
    }

    private bool IsOnVideoTrack(Point point) => point.Y >= VideoTop - 2 && point.Y <= VideoTop + VideoHeight + 2;

    /// <summary>The in or out handle under the pointer: the yellow tab just outside each end of the kept selection.</summary>
    private DragMode HitTrimHandle(Point point)
    {
        if (!IsOnVideoTrack(point)) return DragMode.None;
        EditTimeline timeline = _host.State.Timeline;
        double inX = TimeToX(timeline.EffectiveIn), outX = TimeToX(timeline.EffectiveOut);
        if (point.X >= inX - HandleWidth - 2 && point.X <= inX + 3) return DragMode.TrimIn;
        if (point.X >= outX - 3 && point.X <= outX + HandleWidth + 2) return DragMode.TrimOut;
        return DragMode.None;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point point = e.GetPosition(this);
        _pointerDown = point;
        _moved = false;
        double time = XToTime(point.X);
        e.Handled = true;
        if (HitMark(point) is (VideoMark mark, DragMode mode))
        {
            _host.TimelineSelectMark(mark.Id);
            _drag = mode;
            _dragMark = mark.Id;
            _dragGrabOffset = time - mark.Start;
            CaptureMouse();
            return;
        }
        if (!_host.SplitMode && HitTrimHandle(point) is DragMode trim and not DragMode.None)
        {
            _drag = trim;
            CaptureMouse();
            return;
        }
        if (IsOnVideoTrack(point))
        {
            if (_host.SplitMode) { _host.TimelineSplit(SnapTime(time, null)); return; }
            _host.TimelineSelectSegment(_host.State.Timeline.IndexAt(time));
        }
        else _host.TimelineClearSelection();
        _drag = DragMode.Scrub;
        _host.TimelineSeek(time);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);
        if (Math.Abs(point.X - _pointerDown.X) > 2) _moved = true;
        double time = XToTime(point.X);
        _hoverTime = _host.SplitMode && IsOnVideoTrack(point) ? SnapTime(time, null) : null;
        UpdateCursor(point);
        if (_drag == DragMode.None || e.LeftButton != MouseButtonState.Pressed) { if (_host.SplitMode) InvalidateVisual(); return; }
        switch (_drag)
        {
            case DragMode.Scrub: _host.TimelineSeek(time); return;
            case DragMode.TrimIn:
            case DragMode.TrimOut:
                if (_moved) _host.TimelineMoveTrimHandle(_drag == DragMode.TrimIn, SnapTime(time, null));
                return;
        }
        if (!_moved || _host.State.Find(_dragMark) is not VideoMark mark) return;
        switch (_drag)
        {
            case DragMode.MoveMark:
            {
                double start = SnapTime(time - _dragGrabOffset, mark.Id);
                double end = SnapTime(start + mark.Duration, mark.Id);
                start = Math.Abs(end - (start + mark.Duration)) > 1e-9 ? end - mark.Duration : start;
                _host.TimelineMoveMark(mark.Id, start, mark.Duration);
                break;
            }
            case DragMode.MarkStart:
            {
                double start = Math.Min(SnapTime(time, mark.Id), mark.End - VideoMark.MinimumDuration);
                _host.TimelineMoveMark(mark.Id, start, mark.End - start);
                break;
            }
            case DragMode.MarkEnd:
            {
                double end = Math.Max(SnapTime(time, mark.Id), mark.Start + VideoMark.MinimumDuration);
                _host.TimelineMoveMark(mark.Id, mark.Start, end - mark.Start);
                break;
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        DragMode finished = _drag;
        _drag = DragMode.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (finished is DragMode.MoveMark or DragMode.MarkStart or DragMode.MarkEnd)
        {
            if (!_moved) _host.TimelineSeek(XToTime(e.GetPosition(this).X));
            _host.TimelineDragFinished();
        }
        else if (finished is DragMode.TrimIn or DragMode.TrimOut) _host.TimelineDragFinished();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverTime = null;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        Point point = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { ZoomBy(e.Delta > 0 ? 1.25 : 0.8, point.X); return; }
        if (point.Y >= MarksTopY && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && MarksOverflow > 0)
        {
            _marksScroll = Math.Clamp(_marksScroll - e.Delta / 4.0, 0, MarksOverflow);
            InvalidateVisual();
            return;
        }
        _viewStart -= e.Delta / 120.0 * 60 / _pixelsPerSecond;
        ClampView();
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private double MarksOverflow
    {
        get
        {
            int rows = MarkRows.RowCount(_host.State.Marks, _host.MarksOnOwnRows);
            return Math.Max(0, MarksTopY + rows * (RowHeight + RowGap) - ActualHeight + 4);
        }
    }

    private void UpdateCursor(Point point)
    {
        if (_drag is DragMode.MarkStart or DragMode.MarkEnd or DragMode.TrimIn or DragMode.TrimOut) return;
        if (HitMark(point) is (_, DragMode mode)) { Cursor = mode == DragMode.MoveMark ? Cursors.SizeAll : Cursors.SizeWE; return; }
        if (_host.SplitMode && IsOnVideoTrack(point)) { Cursor = Cursors.Cross; return; }
        Cursor = HitTrimHandle(point) != DragMode.None ? Cursors.SizeWE : Cursors.Arrow;
    }

    /// <summary>Snaps to the playhead, cut edges, in/out points, and other marks' ends when snapping is on.</summary>
    private double SnapTime(double time, Guid? excluding)
    {
        if (!_host.Snapping) return Math.Clamp(time, 0, Duration);
        IEnumerable<double> targets = _host.State.Timeline.Edges()
            .Append(_host.Playhead)
            .Concat(_host.State.Marks.Where(mark => mark.Id != excluding).SelectMany(mark => new[] { mark.Start, mark.End }));
        return Math.Clamp(TimelineSnapping.Snap(time, targets, SnapPixels / _pixelsPerSecond), 0, Duration);
    }

    private static string FormatTick(double time, double step)
    {
        var span = TimeSpan.FromSeconds(time);
        string whole = span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
        return step < 1 ? $"{whole}.{span.Milliseconds / 100}" : whole;
    }

    private static Brush Brush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Brush CreateHatch()
    {
        var drawing = new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(110, 0xFF, 0x6B, 0x6B)), 1.5), Geometry.Parse("M0,8 L8,0"));
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 8, 8), ViewboxUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}
