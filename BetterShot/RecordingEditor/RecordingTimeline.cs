using BetterShot.Recording;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterShot.RecordingEditor;

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
    void TimelineSeek(double time);
    void TimelineSplit(double time);
    void TimelineSelectSegment(int index);
    void TimelineSelectMark(Guid id);
    void TimelineClearSelection();
    void TimelineMoveMark(Guid id, double start, double duration);
    void TimelineDragFinished();
}

/// <summary>
/// The editing timeline: a time ruler, the clip track (kept stretches, cuts that can be put back, in/out points, and
/// recorded click/shortcut markers), and one bar per mark (on its own row, or folded into one). Drawn in OnRender for
/// speed; scroll with the wheel, zoom with Ctrl+wheel, and "Fit" shows the whole recording.
/// </summary>
internal sealed class RecordingTimeline : FrameworkElement
{
    internal const double RulerHeight = 22, ClipTop = 26, ClipHeight = 42, MarksTop = 76, RowHeight = 20, RowGap = 3, SidePadding = 12;
    private const double EdgeGrip = 6, SnapPixels = 8;
    private static readonly double[] TickSteps = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800];
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush Background = Brush(0x14, 0x15, 0x17);
    private static readonly Brush RulerText = Brush(0x8E, 0x92, 0x9A);
    private static readonly Pen TickPen = new(Brush(0x3A, 0x3C, 0x41), 1);
    private static readonly Brush KeptFill = Brush(0x27, 0x4A, 0x7A);
    private static readonly Pen KeptPen = new(Brush(0x4D, 0x7F, 0xC8), 1);
    private static readonly Brush CutFill = CreateHatch();
    private static readonly Pen CutPen = new(Brush(0x6E, 0x3B, 0x41), 1);
    private static readonly Pen SelectedPen = new(Brush(0xFF, 0xF0, 0x7A), 2);
    private static readonly Brush OutsideSelection = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly Brush InOutBrush = Brush(0xFF, 0xD4, 0x00);
    private static readonly Pen PlayheadPen = new(Brush(0xFF, 0xD4, 0x00), 2);
    private static readonly Pen SplitHoverPen = new(Brushes.White, 1) { DashStyle = DashStyles.Dash };
    private static readonly Brush ClickBrush = Brush(0xF2, 0xF3, 0xF5);
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

    private enum DragMode { None, Scrub, MoveMark, MarkStart, MarkEnd }

    internal RecordingTimeline(IRecordingTimelineHost host)
    {
        _host = host;
        Focusable = false;
        ClipToBounds = true;
        SizeChanged += (_, _) => { if (_fitted) Fit(); else InvalidateVisual(); };
    }

    private double Duration => _host.State.Timeline.Duration;
    private double TrackWidth => Math.Max(1, ActualWidth - SidePadding * 2);

    internal void Fit()
    {
        _fitted = true;
        _viewStart = 0;
        _pixelsPerSecond = TrackWidth / Math.Max(0.1, Duration);
        InvalidateVisual();
    }

    internal void ZoomBy(double factor, double? anchorX = null)
    {
        double anchor = anchorX ?? TimeToX(_host.Playhead);
        double anchorTime = XToTime(anchor);
        double minimum = TrackWidth / Math.Max(0.1, Duration);
        _pixelsPerSecond = Math.Clamp(_pixelsPerSecond * factor, minimum, 2000);
        _fitted = _pixelsPerSecond <= minimum * 1.0001;
        _viewStart = anchorTime - (anchor - SidePadding) / _pixelsPerSecond;
        ClampView();
        InvalidateVisual();
    }

    /// <summary>Keeps the playhead on screen while playing by paging the view.</summary>
    internal void FollowPlayhead()
    {
        double x = TimeToX(_host.Playhead);
        if (x < SidePadding || x > ActualWidth - SidePadding)
        {
            _viewStart = _host.Playhead - TrackWidth * 0.1 / _pixelsPerSecond;
            ClampView();
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

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Background, null, bounds);
        if (Duration <= 0) return;
        DrawRuler(dc);
        DrawClips(dc);
        DrawEvents(dc);
        DrawMarks(dc);
        if (_host.SplitMode && _hoverTime is double hover)
        {
            double x = TimeToX(hover);
            dc.DrawLine(SplitHoverPen, new Point(x, ClipTop - 2), new Point(x, ClipTop + ClipHeight + 2));
        }
        double playhead = TimeToX(_host.Playhead);
        dc.DrawLine(PlayheadPen, new Point(playhead, 0), new Point(playhead, ActualHeight));
        var head = new StreamGeometry();
        using (StreamGeometryContext context = head.Open())
        {
            context.BeginFigure(new Point(playhead - 6, 0), true, true);
            context.LineTo(new Point(playhead + 6, 0), true, false);
            context.LineTo(new Point(playhead, 9), true, false);
        }
        head.Freeze();
        dc.DrawGeometry(PlayheadPen.Brush, null, head);
    }

    private void DrawRuler(DrawingContext dc)
    {
        double step = TickSteps.FirstOrDefault(candidate => candidate * _pixelsPerSecond >= 72, TickSteps[^1]);
        double first = Math.Floor(_viewStart / step) * step;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (double time = first; time <= Duration + 1e-9; time += step)
        {
            double x = TimeToX(time);
            if (x > ActualWidth) break;
            if (x < 0) continue;
            dc.DrawLine(TickPen, new Point(x, RulerHeight - 7), new Point(x, RulerHeight));
            var label = new FormattedText(FormatTick(time, step), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11, RulerText, dpi);
            dc.DrawText(label, new Point(x + 3, 3));
        }
        dc.DrawLine(TickPen, new Point(0, RulerHeight), new Point(ActualWidth, RulerHeight));
    }

    private void DrawClips(DrawingContext dc)
    {
        EditTimeline timeline = _host.State.Timeline;
        for (int index = 0; index < timeline.Segments.Count; index++)
        {
            TimelineSegment segment = timeline.Segments[index];
            Rect rect = SegmentRect(segment);
            if (rect.Right < 0 || rect.Left > ActualWidth) continue;
            dc.DrawRoundedRectangle(segment.Kept ? KeptFill : CutFill, segment.Kept ? KeptPen : CutPen, rect, 4, 4);
            if (!segment.Kept && rect.Width > 36)
            {
                var label = new FormattedText("Cut", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11, RulerText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(label, new Point(rect.X + 6, rect.Y + 4));
            }
            if (_host.SelectedSegmentIndex == index) dc.DrawRoundedRectangle(null, SelectedPen, rect, 4, 4);
        }
        double inX = TimeToX(timeline.EffectiveIn), outX = TimeToX(timeline.EffectiveOut);
        if (timeline.InPoint is not null) dc.DrawRectangle(OutsideSelection, null, new Rect(new Point(TimeToX(0), ClipTop), new Point(Math.Max(TimeToX(0), inX), ClipTop + ClipHeight)));
        if (timeline.OutPoint is not null) dc.DrawRectangle(OutsideSelection, null, new Rect(new Point(outX, ClipTop), new Point(Math.Max(outX, TimeToX(Duration)), ClipTop + ClipHeight)));
        if (timeline.InPoint is not null) DrawBracket(dc, inX, left: true);
        if (timeline.OutPoint is not null) DrawBracket(dc, outX, left: false);
    }

    private static void DrawBracket(DrawingContext dc, double x, bool left)
    {
        double direction = left ? 1 : -1;
        dc.DrawRectangle(InOutBrush, null, new Rect(left ? x : x - 3, ClipTop - 3, 3, ClipHeight + 6));
        dc.DrawRectangle(InOutBrush, null, new Rect(Math.Min(x, x + direction * 9), ClipTop - 3, 9, 3));
        dc.DrawRectangle(InOutBrush, null, new Rect(Math.Min(x, x + direction * 9), ClipTop + ClipHeight, 9, 3));
    }

    private void DrawEvents(DrawingContext dc)
    {
        double y = ClipTop + ClipHeight - 7;
        foreach (RecordingEvent item in _host.Events)
        {
            double x = TimeToX(item.Time);
            if (x < 0 || x > ActualWidth) continue;
            if (item.Kind == RecordingEvent.ClickKind) dc.DrawEllipse(ClickBrush, null, new Point(x, y), 2.5, 2.5);
            else if (item.Kind == RecordingEvent.KeyKind) dc.DrawRectangle(KeyBrush, null, new Rect(x - 1, y - 4, 2, 8));
        }
    }

    private void DrawMarks(DrawingContext dc)
    {
        RecordingEditState state = _host.State;
        IReadOnlyDictionary<Guid, int> rows = MarkRows.Assign(state.Marks, _host.MarksOnOwnRows);
        dc.PushClip(new RectangleGeometry(new Rect(0, MarksTop - 2, ActualWidth, Math.Max(0, ActualHeight - MarksTop + 2))));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (VideoMark mark in state.Marks.OrderBy(mark => mark.Id == _host.SelectedMarkId))
        {
            Rect bar = MarkRect(mark, rows[mark.Id]);
            if (bar.Right < 0 || bar.Left > ActualWidth || bar.Bottom < MarksTop || bar.Top > ActualHeight) continue;
            bool selected = mark.Id == _host.SelectedMarkId;
            var fill = new SolidColorBrush(Color.FromArgb((byte)(state.HideMarkup && !mark.IsRedaction ? 90 : 220), mark.BarColor.R, mark.BarColor.G, mark.BarColor.B));
            dc.DrawRoundedRectangle(fill, selected ? SelectedPen : null, bar, 4, 4);
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

    private Rect SegmentRect(TimelineSegment segment)
    {
        double left = TimeToX(segment.Start), right = TimeToX(segment.End);
        return new Rect(left + 0.5, ClipTop, Math.Max(1, right - left - 1), ClipHeight);
    }

    private Rect MarkRect(VideoMark mark, int row)
    {
        double left = TimeToX(mark.Start), right = TimeToX(mark.End);
        return new Rect(left, MarksTop + row * (RowHeight + RowGap) - _marksScroll, Math.Max(4, right - left), RowHeight);
    }

    private (VideoMark Mark, DragMode Mode)? HitMark(Point point)
    {
        if (point.Y < MarksTop - 2) return null;
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

    private bool IsOnClipTrack(Point point) => point.Y >= ClipTop && point.Y <= ClipTop + ClipHeight;

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
        if (IsOnClipTrack(point))
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
        _hoverTime = _host.SplitMode && IsOnClipTrack(point) ? SnapTime(time, null) : null;
        UpdateCursor(point);
        if (_drag == DragMode.None || e.LeftButton != MouseButtonState.Pressed) { if (_host.SplitMode) InvalidateVisual(); return; }
        if (_drag == DragMode.Scrub) { _host.TimelineSeek(time); return; }
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
        if (point.Y >= MarksTop && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && MarksOverflow > 0)
        {
            _marksScroll = Math.Clamp(_marksScroll - e.Delta / 4.0, 0, MarksOverflow);
            InvalidateVisual();
            return;
        }
        _viewStart -= e.Delta / 120.0 * 60 / _pixelsPerSecond;
        ClampView();
        InvalidateVisual();
    }

    private double MarksOverflow
    {
        get
        {
            int rows = MarkRows.RowCount(_host.State.Marks, _host.MarksOnOwnRows);
            return Math.Max(0, MarksTop + rows * (RowHeight + RowGap) - ActualHeight + 4);
        }
    }

    private void UpdateCursor(Point point)
    {
        if (_drag is DragMode.MarkStart or DragMode.MarkEnd) return;
        Cursor = HitMark(point) is (_, DragMode mode)
            ? mode == DragMode.MoveMark ? Cursors.SizeAll : Cursors.SizeWE
            : _host.SplitMode && IsOnClipTrack(point) ? Cursors.Cross : Cursors.Arrow;
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

    private static Brush CreateHatch()
    {
        var drawing = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x24, 0x1A, 0x1C)), new Pen(new SolidColorBrush(Color.FromRgb(0x4A, 0x2A, 0x2F)), 2),
            Geometry.Parse("M0,8 L8,0"));
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 8, 8), ViewboxUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}
