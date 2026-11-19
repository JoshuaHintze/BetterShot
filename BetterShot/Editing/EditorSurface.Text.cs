using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BetterShot.Editing;

public sealed partial class EditorSurface
{
    private TextBox? _textEditor;
    private Border? _textChrome;
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
    internal void BeginCalloutForTest(Point dragStart, Point dragEnd) => BeginCallout(dragStart, dragEnd);
    internal void SetTextForTest(string value) { if (_textEditor is not null) _textEditor.Text = value; }
    internal void CommitTextForTest() => CommitTextEdit();
    internal void CancelTextForTest() => CancelTextEdit();
    internal void SetZoomForTest(double value) => SetZoom(value);

    private TextAnnotation NewTextTemplate(Point position)
    {
        ToolStyle style = _styles.Get(EditorTool.Text);
        return (TextAnnotation)new TextAnnotation(position, "", style.Stroke, style.FontSize).WithStyle(style);
    }

    /// <summary>
    /// A drag defines the bubble (its width wraps the text; the height follows what is typed) and the tail points out from
    /// the corner where the drag started. A click places a default-width bubble above and to the right, pointing at the click.
    /// </summary>
    private TextAnnotation CalloutFromDrag(Point start, Point end, ToolStyle style)
    {
        var template = (TextAnnotation)new TextAnnotation(start, "", style.Stroke, style.FontSize).WithStyle(style) with { Tail = start, Background = false };
        Vector padding = template.BubblePadding;
        Rect content = _layout.Content;
        Vector drag = end - start;
        if (Math.Abs(drag.X) < 12 && Math.Abs(drag.Y) < 12)
        {
            double width = Math.Max(TextAnnotation.MinimumWrapWidth, style.FontSize * 9);
            double left = start.X + style.FontSize * 1.4;
            if (left + width + padding.X > content.Right) left = Math.Max(content.Left + padding.X, start.X - style.FontSize * 1.4 - width);
            double top = start.Y - style.FontSize * 4.2;
            if (top - padding.Y < content.Top) top = start.Y + style.FontSize * 2.6;
            return template with { Position = new Point(left, top), WrapWidth = width, Tail = start };
        }
        var box = new Rect(start, end);
        double wrap = Math.Max(TextAnnotation.MinimumWrapWidth, box.Width - padding.X * 2);
        Point position = new(box.Left + padding.X, box.Top + padding.Y);
        Point center = new(box.Left + box.Width / 2, box.Top + box.Height / 2);
        Vector outward = start - center;
        if (outward.Length < 1) outward = new Vector(-1, 1);
        outward.Normalize();
        double reach = Math.Max(36, style.FontSize * 2.4);
        Point tail = start + outward * reach;
        tail = new Point(Math.Clamp(tail.X, content.Left, content.Right), Math.Clamp(tail.Y, content.Top, content.Bottom));
        return template with { Position = position, WrapWidth = wrap, Tail = tail };
    }

    private void BeginCallout(Point start, Point end)
    {
        TextAnnotation template = CalloutFromDrag(start, end, _styles.Get(EditorTool.Callout));
        BeginTextEdit(template.Position, null, template);
    }

    private void BeginTextEdit(Point position, TextAnnotation? existing, TextAnnotation? template = null)
    {
        CommitTextEdit();
        _editingText = existing;
        _selected = existing;
        _textPosition = position;
        _textTemplate = existing ?? template ?? NewTextTemplate(position);
        bool callout = _textTemplate.IsCallout;
        bool bubble = callout || _textTemplate.Background;
        var textBrush = new SolidColorBrush(_textTemplate.TextColor);
        var box = new TextBox
        {
            Text = existing?.Text ?? "",
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = _textTemplate.EffectiveWrapWidth is null ? TextWrapping.NoWrap : TextWrapping.Wrap,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = callout ? FontWeights.Bold : FontWeights.Normal,
            FontSize = _textTemplate.FontSize * _zoom,
            Foreground = textBrush,
            CaretBrush = bubble ? textBrush : Brushes.White,
            Background = callout ? Brushes.Transparent : bubble ? new SolidColorBrush(_textTemplate.BubbleColor) : Brush("#D9171819"),
            BorderBrush = Brush("#FFD400"),
            BorderThickness = new Thickness(callout ? 0 : 1),
            Padding = callout ? new Thickness(0) : new Thickness(4, 2, 4, 3),
            MinWidth = 48,
            MinHeight = 28,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        System.Windows.Automation.AutomationProperties.SetName(box, callout ? "Callout text" : "Text");
        _textEditor = box;
        box.PreviewKeyDown += TextEditor_KeyDown;
        box.TextChanged += (_, _) => { SizeTextEditor(); if (callout) _previewLayer.Show(TextEditingPreview(), false); };
        _imageOverlay.Children.Clear();
        if (callout)
        {
            // The bubble is drawn around the text box so typing looks like the finished callout; the tail is on the preview layer.
            _textChrome = new Border { Child = box, Background = new SolidColorBrush(_textTemplate.BubbleColor), BorderBrush = Brush("#FFD400"), BorderThickness = new Thickness(1.5) };
            _imageOverlay.Children.Add(_textChrome);
        }
        else _imageOverlay.Children.Add(box);
        PositionTextEditor();
        SizeTextEditor();
        Refresh();
        Dispatcher.BeginInvoke(() => { box.Focus(); box.CaretIndex = box.Text.Length; });
    }

    /// <summary>While a callout is typed, its bubble and tail preview follow the text so the tail meets the growing bubble.</summary>
    private Annotation? TextEditingPreview()
    {
        if (_textEditor is null || _textTemplate is not { IsCallout: true } template) return null;
        return template with { Position = _textPosition, Text = _textEditor.Text.Length == 0 ? " " : _textEditor.Text };
    }

    private void TextEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CancelTextEdit(); e.Handled = true; return; }
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CommitTextEdit(); e.Handled = true; }
    }

    private void SizeTextEditor()
    {
        if (_textEditor is null) return;
        if (_textTemplate?.EffectiveWrapWidth is double wrap)
        {
            // Wrapped (callout) text keeps the bubble width; the height grows with the text like the finished bubble.
            _textEditor.Width = wrap * _zoom + 4;
            _textEditor.ClearValue(HeightProperty);
            _textEditor.MinHeight = Math.Max(16, _textTemplate.FontSize * _zoom * 1.3);
            if (_textChrome is not null)
            {
                Vector padding = _textTemplate.BubblePadding * _zoom;
                _textChrome.Padding = new Thickness(Math.Max(0, padding.X - 3), Math.Max(0, padding.Y - 1.5), Math.Max(0, padding.X - 3), Math.Max(0, padding.Y - 1.5));
                _textChrome.CornerRadius = new CornerRadius(Math.Max(2, _textTemplate.FontSize * .55 * _zoom));
            }
            return;
        }
        string value = _textEditor.Text.Length == 0 ? " " : _textEditor.Text;
        var formatted = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(_textEditor.FontFamily, FontStyles.Normal, _textEditor.FontWeight, FontStretches.Normal),
            _textEditor.FontSize, _textEditor.Foreground, 1);
        // Explicit dimensions include border/padding and a small caret allowance. This keeps
        // the final line and descenders visible at every zoom instead of clipping to one line.
        _textEditor.Width = Math.Max(48, formatted.WidthIncludingTrailingWhitespace + 13);
        _textEditor.Height = Math.Max(28, formatted.Height + 9);
    }

    private void PositionTextEditor()
    {
        if (_textEditor is null) return;
        if (_textChrome is not null && _textTemplate is not null)
        {
            Vector padding = _textTemplate.BubblePadding;
            Canvas.SetLeft(_textChrome, (_textPosition.X - padding.X + ViewOffset.X) * _zoom);
            Canvas.SetTop(_textChrome, (_textPosition.Y - padding.Y + ViewOffset.Y) * _zoom);
            return;
        }
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
        _textChrome = null;
        _editingText = null;
        _textTemplate = null;
        _imageOverlay.Children.Clear();
        _previewLayer.Show(_preview, false);
        _image.Focus();
    }

    private void Scroller_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_textEditor is null || IsInside(e.OriginalSource as DependencyObject, (DependencyObject?)_textChrome ?? _textEditor)) return;
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
        double fontSize = Math.Clamp(source.FontSize * scale, 6, 512);
        // A callout's bubble width scales with its text so a corner drag resizes the whole bubble.
        var resized = source with { FontSize = fontSize, WrapWidth = source.WrapWidth is double wrap ? wrap * fontSize / source.FontSize : null };
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
