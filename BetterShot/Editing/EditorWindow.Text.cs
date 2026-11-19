using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BetterShot.Editing;

public sealed partial class EditorWindow
{
    private TextBox? _textEditor;
    private TextAnnotation? _editingText;
    private TextAnnotation? _textTemplate;
    private Point _textPosition;
    private Thumb? _textGestureThumb;
    private TextAnnotation? _textGestureStart;
    private Vector _textGestureDelta;
    private TextCorner _textGestureCorner;
    private bool _cancelingTextGesture;
    private bool _textGestureRendered;

    internal TextAnnotation? SelectedTextForTest => _selected as TextAnnotation;
    internal TextBox? TextEditorForTest => _textEditor;
    internal Canvas TextOverlayForTest => _imageOverlay;
    internal void BeginTextForTest(Point p, TextAnnotation? existing = null) => BeginTextEdit(p, existing);
    internal void BeginCalloutForTest(Point target, Point bubble) => BeginCallout(target, bubble);
    internal void SetTextForTest(string value) { if (_textEditor is not null) _textEditor.Text = value; }
    internal void CommitTextForTest() => CommitTextEdit();
    internal void CancelTextForTest() => CancelTextEdit();
    internal void SetZoomForTest(double value) => SetZoom(value);

    private TextAnnotation NewTextTemplate(Point position)
    {
        ToolStyle style = _styles.Get(EditorTool.Text);
        return (TextAnnotation)new TextAnnotation(position, "", style.Stroke, style.FontSize).WithStyle(style);
    }

    private TextAnnotation CalloutTemplate(Point position, Point tail, ToolStyle style) =>
        (TextAnnotation)new TextAnnotation(position, "", style.Stroke, style.FontSize).WithStyle(style) with { Tail = tail, Background = false };

    /// <summary>Press on the target and release where the bubble goes; a click places the bubble with a default tail.</summary>
    private void BeginCallout(Point target, Point bubble)
    {
        ToolStyle style = _styles.Get(EditorTool.Callout);
        bool dragged = (bubble - target).Length >= 12;
        Point position = dragged ? bubble : target;
        Point tail = dragged ? target : target + new Vector(-style.FontSize * 1.1, style.FontSize * 2.6);
        BeginTextEdit(position, null, CalloutTemplate(position, tail, style));
    }

    private void BeginTextEdit(Point position, TextAnnotation? existing, TextAnnotation? template = null)
    {
        CommitTextEdit();
        _editingText = existing;
        _selected = existing;
        _textPosition = position;
        _textTemplate = existing ?? template ?? NewTextTemplate(position);
        bool bubble = _textTemplate.IsCallout || _textTemplate.Background;
        var textBrush = new SolidColorBrush(_textTemplate.TextColor);
        var box = new TextBox
        {
            Text = existing?.Text ?? "",
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = _textTemplate.FontSize * _zoom,
            Foreground = textBrush,
            CaretBrush = bubble ? textBrush : Brushes.White,
            Background = bubble ? new SolidColorBrush(_textTemplate.BubbleColor) : Brush("#D9171819"),
            BorderBrush = Brush("#FFD400"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 3),
            MinWidth = 48,
            MinHeight = 28,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _textEditor = box;
        box.PreviewKeyDown += TextEditor_KeyDown;
        box.TextChanged += (_, _) => SizeTextEditor();
        _imageOverlay.Children.Clear();
        _imageOverlay.Children.Add(box);
        PositionTextEditor();
        SizeTextEditor();
        Refresh();
        Dispatcher.BeginInvoke(() => { box.Focus(); box.CaretIndex = box.Text.Length; });
    }

    private void TextEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CancelTextEdit(); e.Handled = true; return; }
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CommitTextEdit(); e.Handled = true; }
    }

    private void SizeTextEditor()
    {
        if (_textEditor is null) return;
        string value = _textEditor.Text.Length == 0 ? " " : _textEditor.Text;
        var formatted = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(_textEditor.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            _textEditor.FontSize, _textEditor.Foreground, 1);
        // Explicit dimensions include border/padding and a small caret allowance. This keeps
        // the final line and descenders visible at every zoom instead of clipping to one line.
        _textEditor.Width = Math.Max(48, formatted.WidthIncludingTrailingWhitespace + 13);
        _textEditor.Height = Math.Max(28, formatted.Height + 9);
    }

    private void PositionTextEditor()
    {
        if (_textEditor is null) return;
        Canvas.SetLeft(_textEditor, (_textPosition.X + ViewOffset.X) * _zoom - 5);
        Canvas.SetTop(_textEditor, (_textPosition.Y + ViewOffset.Y) * _zoom - 3);
    }

    private void CommitTextEdit()
    {
        if (_textEditor is null) return;
        TextBox box = _textEditor;
        TextAnnotation? old = _editingText;
        TextAnnotation basis = old ?? _textTemplate ?? NewTextTemplate(_textPosition);
        string text = box.Text;
        EndTextEditor();
        if (string.IsNullOrWhiteSpace(text))
        {
            _selected = null;
            if (old is not null) _document.Remove(old);
            Refresh();
            return;
        }
        var value = basis with { Position = _textPosition, Text = text };
        if (old is not null && value == old) { _selected = old; Refresh(); return; }
        _selected = value;
        if (old is null) { _document.Add(value); SelectTool(EditorTool.Select); }
        else _document.Replace(old, value);
        Refresh();
    }

    private void CancelTextEdit()
    {
        if (_textEditor is null) return;
        TextAnnotation? old = _editingText;
        EndTextEditor();
        _selected = old;
        Refresh();
    }

    private void EndTextEditor()
    {
        _textEditor = null;
        _editingText = null;
        _textTemplate = null;
        _imageOverlay.Children.Clear();
        _image.Focus();
    }

    private void Scroller_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_textEditor is null || IsInside(e.OriginalSource as DependencyObject, _textEditor)) return;
        CommitTextEdit();
    }

    private static bool IsInside(DependencyObject? child, DependencyObject parent)
    {
        for (DependencyObject? current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, parent)) return true;
        return false;
    }

    private void RefreshTextOverlay()
    {
        if (_textEditor is not null)
        {
            _textEditor.FontSize = (_textTemplate?.FontSize ?? _styles.Get(EditorTool.Text).FontSize) * _zoom;
            PositionTextEditor();
            SizeTextEditor();
            return;
        }
        if (_textGestureThumb is not null && _preview is TextAnnotation gesturePreview)
        {
            UpdateTextGestureOutline(gesturePreview);
            return;
        }
        _imageOverlay.Children.Clear();
        if (_selected is not null && !_document.Items.Contains(_selected)) _selected = null;
        TextAnnotation? text = _preview as TextAnnotation ?? _selected as TextAnnotation;
        if (_tool != EditorTool.Select || text is null) return;
        Rect r = TextTransform.ToDisplay(text.Bounds, _zoom, ViewOffset);
        var outline = new Rectangle { Width = r.Width, Height = r.Height, Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false };
        Canvas.SetLeft(outline, r.Left); Canvas.SetTop(outline, r.Top); _imageOverlay.Children.Add(outline);
        AddTextThumb(r, TextCorner.Move, Cursors.SizeAll, r.Left, r.Top, r.Width, r.Height, .01);
        const double size = 10;
        if (text.Tail is Point tail)
        {
            Point tailDisplay = TextTransform.ToDisplay(tail, _zoom, ViewOffset);
            AddTextThumb(r, TextCorner.Tail, Cursors.Hand, tailDisplay.X - size / 2, tailDisplay.Y - size / 2, size, size);
        }
        AddTextThumb(r, TextCorner.TopLeft, Cursors.SizeNWSE, r.Left-size/2, r.Top-size/2, size, size);
        AddTextThumb(r, TextCorner.TopRight, Cursors.SizeNESW, r.Right-size/2, r.Top-size/2, size, size);
        AddTextThumb(r, TextCorner.BottomLeft, Cursors.SizeNESW, r.Left-size/2, r.Bottom-size/2, size, size);
        AddTextThumb(r, TextCorner.BottomRight, Cursors.SizeNWSE, r.Right-size/2, r.Bottom-size/2, size, size);
    }

    private void AddTextThumb(Rect bounds, TextCorner corner, Cursor cursor, double left, double top, double width, double height, double opacity = 1)
    {
        var thumb = new Thumb { Tag = corner, Width = width, Height = height, Cursor = cursor, Background = Brushes.White, Opacity = opacity };
        Canvas.SetLeft(thumb, left); Canvas.SetTop(thumb, top);
        thumb.DragStarted += (_, _) => { _textGestureThumb = thumb; _textGestureStart = _selected as TextAnnotation; _textGestureDelta = default; _textGestureCorner = corner; _textGestureRendered = false; };
        thumb.DragDelta += TextThumb_DragDelta;
        thumb.DragCompleted += TextThumb_DragCompleted;
        if (corner == TextCorner.Move) thumb.MouseDoubleClick += (_, e) => { if (_selected is TextAnnotation text) { EditText(text); e.Handled = true; } };
        if (corner != TextCorner.Move) Panel.SetZIndex(thumb, 2);
        _imageOverlay.Children.Add(thumb);
    }

    private void TextThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_textGestureStart is null || !ReferenceEquals(sender, _textGestureThumb)) return;
        _textGestureDelta += new Vector(e.HorizontalChange / _zoom, e.VerticalChange / _zoom);
        _preview = _textGestureCorner == TextCorner.Move
            ? _textGestureStart.Move(_textGestureDelta)
            : TextTransform.Resize(_textGestureStart, _textGestureCorner, _textGestureDelta);
        // Keep the active Thumb (and its mouse capture) alive for the whole gesture.
        // Rebuilding the overlay here would terminate the drag after its first delta.
        // The bitmap is re-rendered once to hide the original; later deltas only redraw the vector preview.
        if (!_textGestureRendered) { _image.Source = RenderPreview(); _textGestureRendered = true; }
        _previewLayer.Show(_preview, false);
        if (_preview is TextAnnotation preview) UpdateTextGestureOutline(preview);
    }

    private void UpdateTextGestureOutline(TextAnnotation text)
    {
        Rect bounds = TextTransform.ToDisplay(text.Bounds, _zoom, ViewOffset);
        foreach (FrameworkElement element in _imageOverlay.Children)
        {
            double left = bounds.Left, top = bounds.Top;
            if (element is Thumb tailThumb && tailThumb.Tag is TextCorner.Tail)
            {
                if (text.Tail is Point tail)
                {
                    Point display = TextTransform.ToDisplay(tail, _zoom, ViewOffset);
                    Canvas.SetLeft(tailThumb, display.X - tailThumb.Width / 2); Canvas.SetTop(tailThumb, display.Y - tailThumb.Height / 2);
                }
                continue;
            }
            if (element is Thumb thumb && thumb.Tag is TextCorner corner && corner != TextCorner.Move)
            {
                left = (corner is TextCorner.TopRight or TextCorner.BottomRight ? bounds.Right : bounds.Left) - thumb.Width / 2;
                top = (corner is TextCorner.BottomLeft or TextCorner.BottomRight ? bounds.Bottom : bounds.Top) - thumb.Height / 2;
            }
            else { element.Width = bounds.Width; element.Height = bounds.Height; }
            Canvas.SetLeft(element, left); Canvas.SetTop(element, top);
        }
    }

    private void TextThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        TextAnnotation? old = _textGestureStart;
        TextAnnotation? value = _preview as TextAnnotation;
        _preview = null; _textGestureStart = null; _textGestureThumb = null;
        _previewLayer.Show(null, false);
        bool canceled = e.Canceled || _cancelingTextGesture;
        _cancelingTextGesture = false;
        if (!canceled && old is not null && value is not null && value != old) { _selected = value; _document.Replace(old, value); }
        Refresh();
    }

    private void CancelTextGesture()
    {
        _cancelingTextGesture = true;
        _preview = null;
        _textGestureThumb?.CancelDrag();
        if (_textGestureStart is not null) { _textGestureStart = null; _textGestureThumb = null; Refresh(); }
    }
}

internal enum TextCorner { Move, TopLeft, TopRight, BottomLeft, BottomRight, Tail }

internal static class TextTransform
{
    internal static Rect ToDisplay(Rect nativeBounds, double zoom, Vector offset = default) =>
        new((nativeBounds.X + offset.X) * zoom, (nativeBounds.Y + offset.Y) * zoom, nativeBounds.Width * zoom, nativeBounds.Height * zoom);

    internal static Point ToDisplay(Point nativePoint, double zoom, Vector offset = default) =>
        new((nativePoint.X + offset.X) * zoom, (nativePoint.Y + offset.Y) * zoom);

    internal static TextAnnotation Resize(TextAnnotation source, TextCorner corner, Vector delta)
    {
        if (corner == TextCorner.Move) return (TextAnnotation)source.Move(delta);
        if (corner == TextCorner.Tail) return source with { Tail = source.Tail is Point tail ? tail + delta : null };
        Rect b = source.Bounds;
        double sx = corner is TextCorner.TopLeft or TextCorner.BottomLeft ? -delta.X : delta.X;
        double sy = corner is TextCorner.TopLeft or TextCorner.TopRight ? -delta.Y : delta.Y;
        double scale = Math.Max(6 / source.FontSize, Math.Max(.1, Math.Max((b.Width + sx) / Math.Max(1, b.Width), (b.Height + sy) / Math.Max(1, b.Height))));
        var resized = source with { FontSize = Math.Clamp(source.FontSize * scale, 6, 512) };
        Rect rb = resized.Bounds;
        Point anchor = corner switch { TextCorner.TopLeft => b.BottomRight, TextCorner.TopRight => b.BottomLeft, TextCorner.BottomLeft => b.TopRight, _ => b.TopLeft };
        Point position = corner switch
        {
            TextCorner.TopLeft => new Point(anchor.X - rb.Width, anchor.Y - rb.Height),
            TextCorner.TopRight => new Point(anchor.X, anchor.Y - rb.Height),
            TextCorner.BottomLeft => new Point(anchor.X - rb.Width, anchor.Y),
            _ => anchor
        };
        return resized with { Position = position };
    }
}
