using BetterShot.Controls;
using BetterShot.Editing;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static BetterShot.RecordingEditor.RecordingEditorChrome;

namespace BetterShot.RecordingEditor;

internal enum VideoMarkTool { Select, Text, Arrow, Rectangle, Ellipse, Line, Highlighter, Callout, Step, Spotlight, Redact, Pen }

/// <summary>
/// Markup on the frame at the playhead: drawing tools (reusing the screenshot editor's annotation types), select/move/
/// resize, on-canvas text editing, styles, and redactions that follow what they cover.
/// </summary>
internal sealed partial class RecordingEditorWindow
{
    private static readonly (string Name, Color Color)[] Palette =
    [
        ("Red", Color.FromRgb(0xFF, 0x42, 0x45)), ("Yellow", Color.FromRgb(0xFF, 0xD4, 0x00)), ("Green", Color.FromRgb(0x2E, 0xCC, 0x71)),
        ("Blue", Color.FromRgb(0x3B, 0x82, 0xF6)), ("Purple", Color.FromRgb(0xA8, 0x55, 0xF7)), ("White", Colors.White), ("Black", Colors.Black)
    ];
    private static readonly (string Name, double Width)[] Widths = [("Thin", 2), ("Medium", 4), ("Thick", 8), ("Heavy", 14)];
    private static readonly double[] FontSizes = [24, 32, 48, 64];
    private readonly List<Point> _highlighterPoints = [];
    private VideoMarkTool _tool = VideoMarkTool.Arrow;
    private Color _color = Palette[0].Color;
    private double _strokeWidth = 4;
    private double _fontSize = 32;
    private RedactionStyle _redactionStyle = RedactionStyle.Solid;
    private Button? _hideMarkupButton;
    private Point? _drawStart;
    private Annotation? _drawPreview;
    private Annotation? _dragPreview;
    private int _dragHandle = -1;
    private TextBox? _editingText;
    private Guid? _editingMarkId;
    private Point? _editingTail;

    private VideoMark? SelectedMark => _selectedMark is Guid id ? State.Find(id) : null;

    private void SelectTool(VideoMarkTool tool)
    {
        CommitTextEdit();
        CancelDraw();
        _tool = tool;
        if (tool != VideoMarkTool.Select) SetStatus($"{ToolName(tool)} marks start at the playhead and last 3 seconds; drag the ends of the bar to change that.");
        UpdateMarkupToolbar();
    }

    private void UpdateMarkupToolbar()
    {
        UpdateToolPill();
        _markLayer.Cursor = _tool == VideoMarkTool.Select ? Cursors.Arrow : _tool == VideoMarkTool.Text ? Cursors.IBeam : Cursors.Cross;
        if (_hideMarkupButton?.Content is LucideIcon eye)
        {
            eye.Icon = State.HideMarkup ? LucideIconKind.EyeOff : LucideIconKind.Eye;
            _hideMarkupButton.ToolTip = State.HideMarkup ? "Show markup in the player and the export" : "Hide markup in the player and the export (redactions stay covered)";
        }
    }

    private void ToggleHideMarkup()
    {
        Apply(state => state with { HideMarkup = !state.HideMarkup });
        SetStatus(State.HideMarkup ? "Markup is hidden in the player and the export. Redactions still cover what they hide." : "Markup is shown again.");
    }

    // ---------- Pointer ----------

    private void Surface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_info is null || e.ClickCount > 1) return;
        CommitTextEdit();
        Pause();
        Point point = e.GetPosition(_markLayer);
        e.Handled = true;
        _markLayer.Focus();
        switch (_tool)
        {
            case VideoMarkTool.Select: BeginSelectDrag(point); return;
            case VideoMarkTool.Text: BeginTextEdit(point, null, null, string.Empty); return;
            case VideoMarkTool.Step: AddMark(new StepAnnotation(point, NextStepNumber(), _color, StepAnnotation.RadiusFor(_strokeWidth))); return;
        }
        _drawStart = point;
        _highlighterPoints.Clear();
        _highlighterPoints.Add(point);
        _markLayer.CaptureMouse();
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Point point = e.GetPosition(_markLayer);
        if (_tool == VideoMarkTool.Select)
        {
            UpdateSelectDrag(point, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            return;
        }
        if (_drawStart is not Point start) return;
        _highlighterPoints.Add(point);
        _drawPreview = BuildShape(start, point, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        RefreshOverlay();
    }

    private void Surface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Point point = e.GetPosition(_markLayer);
        if (_tool == VideoMarkTool.Select) { EndSelectDrag(); return; }
        if (_drawStart is not Point start) return;
        _drawStart = null;
        _drawPreview = null;
        if (_markLayer.IsMouseCaptured) _markLayer.ReleaseMouseCapture();
        double minimum = 4 / Math.Max(0.01, DisplayScale);
        if (_tool == VideoMarkTool.Callout)
        {
            Point bubble = (point - start).Length < minimum * 4 ? start + new Vector(60, -70) / Math.Max(0.25, DisplayScale) : point;
            BeginTextEdit(bubble, null, start, string.Empty);
            return;
        }
        if ((point - start).Length < minimum && _tool is not (VideoMarkTool.Highlighter or VideoMarkTool.Pen)) { RefreshOverlay(); return; }
        if (BuildShape(start, point, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) is Annotation shape) AddMark(shape);
        else RefreshOverlay();
    }

    private void CancelDraw()
    {
        _drawStart = null;
        _drawPreview = null;
        _dragPreview = null;
        _dragHandle = -1;
        if (_markLayer.IsMouseCaptured) _markLayer.ReleaseMouseCapture();
        RefreshOverlay();
    }

    private Annotation? BuildShape(Point start, Point end, bool constrain)
    {
        Point square = constrain ? GeometryUtil.SnapSquare(start, end) : end;
        Point angled = constrain ? GeometryUtil.Snap45(start, end) : end;
        return _tool switch
        {
            VideoMarkTool.Arrow => new ArrowAnnotation(start, angled, _color, _strokeWidth),
            VideoMarkTool.Line => new LineAnnotation(start, angled, _color, _strokeWidth),
            VideoMarkTool.Rectangle => new RectangleAnnotation(new Rect(start, square), _color, _strokeWidth) { CornerRadius = 3 },
            VideoMarkTool.Ellipse => new EllipseAnnotation(new Rect(start, square), _color, _strokeWidth),
            VideoMarkTool.Spotlight => new SpotlightAnnotation(new Rect(start, square), 8),
            VideoMarkTool.Redact => new RectangleAnnotation(new Rect(start, square), Colors.Black, _strokeWidth, Redaction: true) { RedactionMode = _redactionStyle },
            VideoMarkTool.Highlighter when _highlighterPoints.Count > 1 => new HighlighterAnnotation(_highlighterPoints.ToArray(), _color, _strokeWidth * 2),
            VideoMarkTool.Pen when _highlighterPoints.Count > 1 => new PenAnnotation(_highlighterPoints.ToArray(), _color, _strokeWidth),
            VideoMarkTool.Callout => null,
            _ => null
        };
    }

    private int NextStepNumber() => State.Marks.Select(mark => mark.Shape).OfType<StepAnnotation>().Select(step => step.Number).DefaultIfEmpty(0).Max() + 1;

    private void AddMark(Annotation shape)
    {
        VideoMark mark = VideoMark.CreateAt(shape, _playhead, State.Timeline.Duration);
        if (Apply(state => state.AddMark(mark)))
        {
            _selectedMark = mark.Id;
            _selectedSegment = null;
            SetStatus($"{mark.Kind} added at {FormatTime(mark.Start)} for {mark.Duration:0.#} s.");
        }
        RefreshSelectionUi();
    }

    // ---------- Select, move, resize ----------

    private void BeginSelectDrag(Point point)
    {
        double tolerance = 6 / Math.Max(0.01, DisplayScale);
        if (SelectedMark is VideoMark selected && selected.IsActiveAt(_playhead))
        {
            int handle = Handles(selected.ShapeAt(_playhead)).Select((position, index) => (position, index))
                .Where(item => (item.position - point).Length <= tolerance * 1.6).Select(item => item.index).DefaultIfEmpty(-1).First();
            if (handle >= 0 || selected.ShapeAt(_playhead).Hit(point, tolerance))
            {
                StartDrag(point, handle);
                return;
            }
        }
        VideoMark? hit = State.VisibleMarksAt(_playhead).Reverse().FirstOrDefault(mark => mark.ShapeAt(_playhead).Hit(point, tolerance));
        if (hit is null)
        {
            TimelineClearSelection();
            return;
        }
        _selectedMark = hit.Id;
        _selectedSegment = null;
        StartDrag(point, -1);
        RefreshSelectionUi();
    }

    private void StartDrag(Point point, int handle)
    {
        _drawStart = point;
        _dragHandle = handle;
        _markLayer.CaptureMouse();
    }

    private void UpdateSelectDrag(Point point, bool constrain)
    {
        if (_drawStart is not Point start || SelectedMark is not VideoMark mark) return;
        Annotation shape = mark.ShapeAt(_playhead);
        _dragPreview = _dragHandle >= 0 ? Resize(shape, _dragHandle, point, constrain) : shape.Move(point - start);
        RefreshOverlay();
    }

    private void EndSelectDrag()
    {
        Annotation? preview = _dragPreview;
        _drawStart = null;
        _dragPreview = null;
        _dragHandle = -1;
        if (_markLayer.IsMouseCaptured) _markLayer.ReleaseMouseCapture();
        if (preview is null || SelectedMark is not VideoMark mark) { RefreshOverlay(); return; }
        Apply(state => state.WithMark(WithShapeAtPlayhead(mark, preview)));
    }

    /// <summary>
    /// A moved/resized followed redaction gets a keyframe at the playhead (this is how a lost box is dragged back);
    /// any other mark simply takes the new shape.
    /// </summary>
    private VideoMark WithShapeAtPlayhead(VideoMark mark, Annotation shape)
    {
        if (!mark.Tracked || shape is not RectangleAnnotation rectangle) return mark with { Shape = shape };
        var keyframes = mark.Keyframes.Where(key => Math.Abs(key.Time - _playhead) > FrameDuration / 2).Append(new TrackKeyframe(_playhead, rectangle.Bounds)).OrderBy(key => key.Time).ToArray();
        return mark with { Keyframes = keyframes, LostAt = mark.LostAt is double lost && lost <= _playhead ? null : mark.LostAt };
    }

    private static IReadOnlyList<Point> Handles(Annotation shape) => shape switch
    {
        RectangleAnnotation rectangle => Corners(rectangle.Bounds),
        EllipseAnnotation ellipse => Corners(ellipse.Bounds),
        SpotlightAnnotation spotlight => Corners(spotlight.Bounds),
        ArrowAnnotation arrow => [arrow.Start, arrow.End],
        LineAnnotation line => [line.Start, line.End],
        TextAnnotation { Tail: Point tail } => [tail],
        _ => []
    };

    private static Point[] Corners(Rect bounds) => [bounds.TopLeft, bounds.TopRight, bounds.BottomRight, bounds.BottomLeft];

    private static Annotation Resize(Annotation shape, int handle, Point point, bool constrain)
    {
        Rect ResizeBox(Rect bounds)
        {
            Point[] corners = Corners(bounds);
            Point anchor = corners[(handle + 2) % 4];
            return new Rect(anchor, constrain ? GeometryUtil.SnapSquare(anchor, point) : point);
        }
        return shape switch
        {
            RectangleAnnotation rectangle => rectangle with { Bounds = ResizeBox(rectangle.Bounds) },
            EllipseAnnotation ellipse => ellipse with { Bounds = ResizeBox(ellipse.Bounds) },
            SpotlightAnnotation spotlight => spotlight with { Bounds = ResizeBox(spotlight.Bounds) },
            ArrowAnnotation arrow => handle == 0 ? arrow with { Start = point } : arrow with { End = point },
            LineAnnotation line => handle == 0 ? line with { Start = point } : line with { End = point },
            TextAnnotation text when text.Tail is not null => text with { Tail = point },
            _ => shape
        };
    }

    private (Rect? Selection, IReadOnlyList<Point> Handles) SelectionGeometry(IReadOnlyList<VideoMark> visible, double time)
    {
        if (_selectedMark is not Guid id || visible.FirstOrDefault(mark => mark.Id == id) is not VideoMark mark || _editingMarkId == id) return (null, []);
        Annotation shape = _dragPreview ?? mark.ShapeAt(time);
        return (shape.Extent, Handles(shape));
    }

    // ---------- Text ----------

    private void EditSelectedText()
    {
        if (_tool != VideoMarkTool.Select || SelectedMark is not { Shape: TextAnnotation text } mark || !mark.IsActiveAt(_playhead)) return;
        CancelDraw();
        BeginTextEdit(text.Position, mark.Id, text.Tail, text.Text);
    }

    private void BeginTextEdit(Point position, Guid? markId, Point? tail, string text)
    {
        CommitTextEdit();
        Color color = markId is Guid id && State.Find(id)?.Shape is TextAnnotation existing ? existing.Color : _color;
        double size = markId is Guid existingId && State.Find(existingId)?.Shape is TextAnnotation sized ? sized.FontSize : _fontSize;
        var box = new TextBox
        {
            Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = size, AcceptsReturn = true, MinWidth = size * 4,
            Foreground = new SolidColorBrush(tail is null ? color : GeometryUtil.Contrast(color)),
            Background = tail is null ? new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)) : new SolidColorBrush(color),
            CaretBrush = Brushes.White, BorderBrush = Brushes.White, BorderThickness = new Thickness(Math.Max(1, size / 24)), Padding = new Thickness(0)
        };
        Canvas.SetLeft(box, position.X);
        Canvas.SetTop(box, position.Y);
        box.PreviewKeyDown += TextBox_PreviewKeyDown;
        box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(CommitTextEdit);
        _editCanvas.Children.Add(box);
        _editingText = box;
        _editingMarkId = markId;
        _editingTail = tail;
        RefreshOverlay();
        Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
        SetStatus("Type the text. Enter finishes, Shift+Enter starts a new line, Esc cancels.");
    }

    private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; EndTextEdit(commit: false); }
        else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; EndTextEdit(commit: true); }
    }

    private void CommitTextEdit() => EndTextEdit(commit: true);

    private void EndTextEdit(bool commit)
    {
        if (_editingText is not TextBox box) return;
        _editingText = null;
        Guid? markId = _editingMarkId;
        Point? tail = _editingTail;
        _editingMarkId = null;
        _editingTail = null;
        _editCanvas.Children.Remove(box);
        string text = box.Text.TrimEnd();
        var position = new Point(Canvas.GetLeft(box), Canvas.GetTop(box));
        if (commit)
        {
            if (markId is Guid id && State.Find(id) is VideoMark existing && existing.Shape is TextAnnotation shape)
            {
                if (text.Length == 0) Apply(state => state.RemoveMark(id));
                else Apply(state => state.WithMark(existing with { Shape = shape with { Text = text } }));
            }
            else if (text.Length > 0)
            {
                var annotation = new TextAnnotation(position, text, _color, _fontSize) { Tail = tail };
                AddMark(annotation);
            }
        }
        _markLayer.Focus();
        RefreshSelectionUi();
    }

    // ---------- Style ----------

    private void OpenStyleMenu(FrameworkElement target)
    {
        ContextMenu menu = Menu();
        menu.Items.Add(MenuHeader("Color"));
        foreach ((string name, Color color) in Palette)
        {
            Color chosen = color;
            MenuItem item = MenuItem(name, (_, _) => Restyle(color: chosen), isChecked: _color == color);
            item.Icon = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(color), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuHeader("Line width"));
        foreach ((string name, double width) in Widths)
        {
            double chosen = width;
            menu.Items.Add(MenuItem(name, (_, _) => Restyle(width: chosen), isChecked: Math.Abs(_strokeWidth - width) < 1e-9));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuHeader("Text size"));
        foreach (double size in FontSizes)
        {
            double chosen = size;
            menu.Items.Add(MenuItem($"{size:0} px", (_, _) => Restyle(fontSize: chosen), isChecked: Math.Abs(_fontSize - size) < 1e-9));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuHeader("Redaction"));
        foreach ((string name, RedactionStyle style) in new[] { ("Solid", RedactionStyle.Solid), ("Pixelate", RedactionStyle.Pixelate), ("Blur", RedactionStyle.Blur) })
        {
            RedactionStyle chosen = style;
            menu.Items.Add(MenuItem(name, (_, _) => Restyle(redaction: chosen), isChecked: _redactionStyle == style));
        }
        OpenBelow(menu, target);
    }

    /// <summary>Changes the defaults for new marks and restyles the selected mark, as one undo step.</summary>
    private void Restyle(Color? color = null, double? width = null, double? fontSize = null, RedactionStyle? redaction = null)
    {
        if (color is Color newColor) _color = newColor;
        if (width is double newWidth) _strokeWidth = newWidth;
        if (fontSize is double newSize) _fontSize = newSize;
        if (redaction is RedactionStyle newRedaction) _redactionStyle = newRedaction;
        UpdateToolPill();
        if (SelectedMark is not VideoMark mark) return;
        ToolStyle current = mark.Shape.CaptureStyle(new ToolStyle());
        ToolStyle style = current with
        {
            Stroke = mark.IsRedaction ? current.Stroke : color ?? current.Stroke,
            Width = width ?? current.Width,
            FontSize = fontSize ?? current.FontSize,
            Redaction = redaction ?? current.Redaction
        };
        if (mark.Shape is SpotlightAnnotation) return;
        Apply(state => state.WithMark(mark with { Shape = mark.Shape.WithStyle(style) }));
    }

    // ---------- Following (tracked redaction) ----------

    private void OpenFollowMenu(FrameworkElement target)
    {
        if (SelectedMark is not { IsRedaction: true } mark) { SetStatus("Select a redaction to make it follow what it covers."); return; }
        ContextMenu menu = Menu();
        menu.Items.Add(MenuItem("Follow what it covers", async (_, _) => await FollowAsync(mark.Id, mark.Start), LucideIconKind.Crosshair,
            tip: "Moves the redaction with what it covers as the recording plays."));
        menu.Items.Add(MenuItem("Follow again from the playhead", async (_, _) => await FollowAsync(mark.Id, _playhead), LucideIconKind.Play,
            tip: "Drag the box back over what it should cover first, then follow from here."));
        menu.Items.Add(MenuItem("Stop following", (_, _) => Apply(state => state.WithMark(mark with { Tracked = false, Keyframes = [], LostAt = null })), LucideIconKind.X));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuHeader("When it loses the field"));
        menu.Items.Add(MenuItem("Stop following", (_, _) => SetLostPolicy(mark.Id, LostFieldPolicy.StopFollowing), isChecked: mark.WhenLost == LostFieldPolicy.StopFollowing,
            tip: "The box stays where it last saw what it covers."));
        menu.Items.Add(MenuItem("Blur the whole picture", (_, _) => SetLostPolicy(mark.Id, LostFieldPolicy.BlurWholePicture), isChecked: mark.WhenLost == LostFieldPolicy.BlurWholePicture,
            tip: "While lost, the whole picture is blurred, in the player and the export."));
        OpenBelow(menu, target);
    }

    private void SetLostPolicy(Guid id, LostFieldPolicy policy)
    {
        if (State.Find(id) is VideoMark mark) Apply(state => state.WithMark(mark with { WhenLost = policy }));
    }

    /// <summary>Tracks the redaction from <paramref name="from"/> to the end of its bar at 15 samples per second.</summary>
    private async Task FollowAsync(Guid id, double from)
    {
        if (_reader is null || _info is null || _operation is not null) return;
        if (State.Find(id) is not { IsRedaction: true } mark) return;
        double start = Math.Clamp(from, mark.Start, Math.Max(mark.Start, mark.End - FrameDuration));
        if (mark.BoundsAt(start) is not Rect startBounds) return;
        Pause();
        (int analysisWidth, int analysisHeight) = ExportPlanning.Even(Math.Min(480, _info.Width), (int)Math.Round(_info.Height * Math.Min(480, _info.Width) / (double)_info.Width));
        double scale = analysisWidth / (double)_info.Width;
        const double samplesPerSecond = 15;
        var times = new List<double>();
        for (double time = start; time < mark.End - 1e-6; time += 1 / samplesPerSecond) times.Add(time);
        using OperationScope operation = BeginOperation("Following the redaction…");
        try
        {
            (List<TrackKeyframe> samples, double? lostAt) = await TrackAsync(times, startBounds, scale, analysisWidth, analysisHeight, operation.Token);
            if (State.Find(id) is not VideoMark current) return;
            IReadOnlyList<TrackKeyframe> path = TrackPath.Simplify(samples, Math.Max(1.5, 1 / scale));
            VideoMark updated = current with
            {
                Tracked = true,
                Keyframes = current.Keyframes.Where(key => key.Time < start - 1e-6).Concat(path).ToArray(),
                LostAt = lostAt
            };
            Apply(state => state.WithMark(updated));
            SetStatus(lostAt is double lost
                ? $"Lost the field at {FormatTime(lost)}. Drag the box back over it at the playhead and choose Follow again from the playhead."
                : "The redaction now follows what it covers. The diamonds on its bar are where it changed course.", lostAt is not null);
        }
        catch (OperationCanceledException) { SetStatus("Following stopped. The redaction was not changed."); }
        catch (Exception error) { SetStatus($"Could not follow the redaction: {error.Message}", true); }
    }

    private async Task<(List<TrackKeyframe> Samples, double? LostAt)> TrackAsync(IReadOnlyList<double> times, Rect startBounds, double scale, int width, int height, System.Threading.CancellationToken token)
    {
        var samples = new List<TrackKeyframe> { new(times.Count > 0 ? times[0] : _playhead, startBounds) };
        RedactionTracker? tracker = null;
        int radius = Math.Max(12, (int)(width * 0.06));
        for (int offset = 0; offset < times.Count; offset += 15)
        {
            double[] batch = times.Skip(offset).Take(15).ToArray();
            IReadOnlyList<DecodedFrame> frames = await _reader!.ReadAsync(batch, width, height, token);
            for (int index = 0; index < frames.Count; index++)
            {
                GrayFrame gray = await Task.Run(() => GrayFrame.FromBgra(frames[index].Bgra, width, height, frames[index].Stride), token);
                if (tracker is null)
                {
                    var box = new TrackBox((int)Math.Round(startBounds.X * scale), (int)Math.Round(startBounds.Y * scale), (int)Math.Round(startBounds.Width * scale), (int)Math.Round(startBounds.Height * scale));
                    tracker = new RedactionTracker(gray, box, radius);
                    continue;
                }
                TrackStep step = await Task.Run(() => tracker.Step(gray), token);
                if (step.Lost) return (samples, batch[index]);
                samples.Add(new TrackKeyframe(batch[index], new Rect(step.Box.X / scale, step.Box.Y / scale, startBounds.Width, startBounds.Height)));
            }
            ReportProgress((offset + batch.Length) / (double)times.Count);
        }
        return (samples, null);
    }
}
