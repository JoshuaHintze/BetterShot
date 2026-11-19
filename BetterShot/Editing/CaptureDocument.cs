using BetterShot.Editing.BackgroundRemoval;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Editing;

public sealed class CaptureDocument
{
    private readonly List<Annotation> _items = [];
    private readonly Stack<DocumentSnapshot> _undo = [];
    private readonly Stack<DocumentSnapshot> _redo = [];
    private Rect? _crop;
    private BackgroundStyle _background = BackgroundStyle.None;
    private string? _coalesceKey;
    private PixelBuffer? _pixels;
    private AnnotationRenderContext? _renderContext;
    private AlphaMask? _backgroundMask;
    private BitmapSource? _baseImage;

    public CaptureDocument(BitmapSource original, Guid? id = null, DateTime? createdUtc = null)
    {
        Original = original ?? throw new ArgumentNullException(nameof(original));
        if (!Original.IsFrozen && Original.CanFreeze) Original.Freeze();
        Id = id ?? Guid.NewGuid();
        CreatedUtc = (createdUtc ?? DateTime.UtcNow).ToUniversalTime();
    }

    public BitmapSource Original { get; }
    public Guid Id { get; }
    public DateTime CreatedUtc { get; }
    public event EventHandler? Changed;
    internal IReadOnlyList<Annotation> Items => _items;
    internal bool CanUndo => _undo.Count > 0;
    internal bool CanRedo => _redo.Count > 0;

    /// <summary>Non-destructive crop in original pixel coordinates; null shows the whole capture.</summary>
    internal Rect? Crop => _crop;
    internal BackgroundStyle Background => _background;
    internal Rect CanvasRect => new(0, 0, Original.PixelWidth, Original.PixelHeight);
    /// <summary>Opacity mask from "Remove the background"; null keeps the capture opaque. Original pixels never change.</summary>
    internal AlphaMask? BackgroundMask => _backgroundMask;
    /// <summary>The image every render starts from: the original, with the background mask applied when present.</summary>
    internal BitmapSource BaseImage => _backgroundMask is null ? Original : _baseImage ??= _backgroundMask.ApplyTo(Original);
    internal PixelBuffer Pixels => _pixels ??= new PixelBuffer(BaseImage);
    internal AnnotationRenderContext RenderContext => _renderContext ??= new AnnotationRenderContext(this);
    internal CompositionLayout Layout(bool compose = true) => DocumentRenderer.Layout(this, compose);

    internal void Add(Annotation annotation)
    {
        SaveUndo();
        _items.Add(annotation);
        RaiseChanged();
    }

    /// <summary>Adds several annotations as one undo step.</summary>
    internal void AddRange(IReadOnlyCollection<Annotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        if (annotations.Count == 0) return;
        SaveUndo();
        _items.AddRange(annotations);
        RaiseChanged();
    }

    internal void Replace(Annotation oldValue, Annotation newValue, string? coalesceKey = null)
    {
        int index = _items.IndexOf(oldValue);
        if (index < 0) return;
        SaveUndo(coalesceKey);
        _items[index] = newValue;
        RaiseChanged();
    }

    internal void Remove(Annotation annotation)
    {
        if (!_items.Contains(annotation)) return;
        SaveUndo();
        _items.Remove(annotation);
        RaiseChanged();
    }

    internal void BringToFront(Annotation annotation) => MoveToIndex(annotation, toFront: true);
    internal void SendToBack(Annotation annotation) => MoveToIndex(annotation, toFront: false);

    internal void SetCrop(Rect? crop)
    {
        Rect? normalized = NormalizeCrop(crop);
        if (Nullable.Equals(normalized, _crop)) return;
        SaveUndo();
        _crop = normalized;
        RaiseChanged();
    }

    internal void SetBackground(BackgroundStyle background, string? coalesceKey = null)
    {
        ArgumentNullException.ThrowIfNull(background);
        BackgroundStyle normalized = background.Normalized();
        if (normalized == _background) return;
        SaveUndo(coalesceKey);
        _background = normalized;
        RaiseChanged();
    }

    /// <summary>Applies or clears the background mask as one undoable edit.</summary>
    internal void SetBackgroundMask(AlphaMask? mask)
    {
        if (mask is not null && !mask.Fits(Original)) throw new ArgumentException("The mask does not match the capture size.", nameof(mask));
        if (ReferenceEquals(mask, _backgroundMask)) return;
        SaveUndo();
        ApplyBackgroundMask(mask);
        RaiseChanged();
    }

    /// <summary>Loads a stored mask without creating an undo step.</summary>
    internal void RestoreBackgroundMask(AlphaMask? mask)
    {
        if (mask is not null && !mask.Fits(Original)) throw new ArgumentException("The mask does not match the capture size.", nameof(mask));
        ApplyBackgroundMask(mask);
        _renderContext = null;
    }

    private void ApplyBackgroundMask(AlphaMask? mask)
    {
        if (ReferenceEquals(mask, _backgroundMask)) return;
        _backgroundMask = mask;
        _baseImage = null;
        _pixels = null;
    }

    internal Rect? NormalizeCrop(Rect? crop)
    {
        if (crop is not Rect requested || requested.IsEmpty) return null;
        if (!double.IsFinite(requested.X) || !double.IsFinite(requested.Y) || !double.IsFinite(requested.Width) || !double.IsFinite(requested.Height)) return null;
        Rect clipped = Rect.Intersect(requested, CanvasRect);
        if (clipped.IsEmpty) return null;
        double left = Math.Round(clipped.Left), top = Math.Round(clipped.Top);
        double right = Math.Round(clipped.Right), bottom = Math.Round(clipped.Bottom);
        if (right - left < 1 || bottom - top < 1) return null;
        var rounded = new Rect(left, top, right - left, bottom - top);
        return rounded == CanvasRect ? null : rounded;
    }

    internal void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
    }

    internal void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
    }

    internal void RestoreAnnotations(IEnumerable<Annotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        _items.Clear();
        _items.AddRange(Clone(annotations));
        _undo.Clear();
        _redo.Clear();
        _coalesceKey = null;
        _renderContext = null;
    }

    internal void RestoreState(IEnumerable<Annotation> annotations, Rect? crop, BackgroundStyle? background)
    {
        RestoreAnnotations(annotations);
        _crop = NormalizeCrop(crop);
        _background = (background ?? BackgroundStyle.None).Normalized();
    }

    /// <summary>Renders the finished composition (crop, annotations, and background) used by every export.</summary>
    public BitmapSource Render() => DocumentRenderer.Render(this, _items, null, compose: true);

    private void MoveToIndex(Annotation annotation, bool toFront)
    {
        int index = _items.IndexOf(annotation);
        int target = toFront ? _items.Count - 1 : 0;
        if (index < 0 || index == target) return;
        SaveUndo();
        _items.RemoveAt(index);
        _items.Insert(target, annotation);
        RaiseChanged();
    }

    private void SaveUndo(string? coalesceKey = null)
    {
        if (coalesceKey is not null && coalesceKey == _coalesceKey && _undo.Count > 0) return;
        _undo.Push(Snapshot());
        _redo.Clear();
        _coalesceKey = coalesceKey;
    }

    private DocumentSnapshot Snapshot() => new(Clone(_items), _crop, _background, _backgroundMask);

    private void Restore(DocumentSnapshot state)
    {
        _items.Clear();
        _items.AddRange(Clone(state.Items));
        _crop = state.Crop;
        _background = state.Background;
        ApplyBackgroundMask(state.BackgroundMask);
        _coalesceKey = null;
        RaiseChanged();
    }

    private static List<Annotation> Clone(IEnumerable<Annotation> source) => source.Select(x => x.DeepCopy()).ToList();
    private void RaiseChanged()
    {
        _renderContext = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed record DocumentSnapshot(List<Annotation> Items, Rect? Crop, BackgroundStyle Background, AlphaMask? BackgroundMask);
}

internal abstract record Annotation(Color Color, double Width)
{
    public double Opacity { get; init; } = 1;
    public bool Dashed { get; init; }
    public bool Shadow { get; init; }
    public Color? Fill { get; init; }

    /// <summary>Selection and hit bounds in original pixel coordinates.</summary>
    internal abstract Rect Extent { get; }
    protected virtual bool SupportsStyleEffects => true;

    internal void Draw(DrawingContext dc, bool selected, AnnotationRenderContext context)
    {
        bool effects = SupportsStyleEffects;
        double opacity = effects && double.IsFinite(Opacity) ? Math.Clamp(Opacity, .1, 1) : 1;
        if (opacity < 1) dc.PushOpacity(opacity);
        if (effects && Shadow) AnnotationShadow.Draw(dc, Width, drawing => DrawContent(drawing, context));
        DrawContent(dc, context);
        if (opacity < 1) dc.Pop();
        if (selected) DrawSelection(dc);
    }

    protected abstract void DrawContent(DrawingContext dc, AnnotationRenderContext context);
    protected virtual void DrawSelection(DrawingContext dc) => dc.DrawRectangle(null, GeometryUtil.SelectionPen, Extent);
    internal abstract bool Hit(Point p, double tolerance);
    internal abstract Annotation Move(Vector delta);
    internal abstract Annotation DeepCopy();
    internal abstract Annotation WithStyle(ToolStyle style);

    internal virtual ToolStyle CaptureStyle(ToolStyle defaults) => defaults with
    {
        Stroke = Color, Width = Width, Opacity = Opacity, Dashed = Dashed, Shadow = Shadow, Fill = Fill
    };

    protected Annotation ApplyCommonStyle(ToolStyle style, bool allowFill) => this with
    {
        Color = style.Stroke, Width = style.Width, Opacity = style.Opacity, Dashed = style.Dashed,
        Shadow = style.Shadow, Fill = allowFill ? style.Fill : null
    };

    protected Pen Pen() => Pen(Width);

    protected Pen Pen(double thickness)
    {
        var pen = new Pen(new SolidColorBrush(Color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (Dashed) { pen.DashStyle = new DashStyle([2.2, 2.0], 0); pen.DashCap = PenLineCap.Flat; }
        return pen;
    }

    protected Brush? FillBrush() => Fill is Color fill ? new SolidColorBrush(fill) : null;
}

internal sealed record ArrowAnnotation(Point Start, Point End, Color Ink, double Stroke) : Annotation(Ink, Stroke)
{
    internal override Rect Extent => new(Start, End);
    protected override void DrawContent(DrawingContext dc, AnnotationRenderContext context)
    {
        Vector d = Start - End;
        if (d.Length < 1) { dc.DrawLine(Pen(), Start, End); return; }
        d.Normalize();
        Vector n = new(-d.Y, d.X); double size = Math.Max(10, Width * 4.2);
        // Stop the shaft inside the head so dashed or translucent strokes do not show through the tip.
        dc.DrawLine(Pen(), Start, End + d * Math.Min(size * .6, (Start - End).Length));
        var figure = new StreamGeometry();
        using (var c = figure.Open()) { c.BeginFigure(End, true, true); c.LineTo(End + d * size + n * size * .48, true, false); c.LineTo(End + d * size - n * size * .48, true, false); }
        figure.Freeze(); dc.DrawGeometry(new SolidColorBrush(Color), null, figure);
    }
    internal override bool Hit(Point p, double t) => GeometryUtil.DistanceToSegment(p, Start, End) <= t + Width;
    internal override Annotation Move(Vector d) => this with { Start = Start + d, End = End + d };
    internal override Annotation DeepCopy() => this with { };
    internal override Annotation WithStyle(ToolStyle style) => (ArrowAnnotation)ApplyCommonStyle(style, false) with { Ink = style.Stroke, Stroke = style.Width };
}

internal sealed record RectangleAnnotation(Rect Bounds, Color Ink, double Stroke, bool Redaction = false) : Annotation(Ink, Stroke)
{
    /// <summary>Null keeps the radius used before styles existed.</summary>
    public double? CornerRadius { get; init; }
    public RedactionStyle RedactionMode { get; init; } = RedactionStyle.Solid;
    internal double EffectiveCornerRadius => Math.Max(0, CornerRadius ?? Math.Min(8, Width * 1.3));
    internal override Rect Extent => Bounds;
    protected override bool SupportsStyleEffects => !Redaction;

    protected override void DrawContent(DrawingContext dc, AnnotationRenderContext context)
    {
        if (Redaction) RedactionRenderer.Draw(dc, Bounds, RedactionMode, Color, Width, context);
        else dc.DrawRoundedRectangle(FillBrush(), Pen(), Bounds, EffectiveCornerRadius, EffectiveCornerRadius);
    }
    internal override bool Hit(Point p, double t) => Redaction || Fill is not null ? GeometryUtil.Inflate(Bounds, t).Contains(p) : GeometryUtil.NearRect(Bounds, p, t + Width);
    internal override Annotation Move(Vector d) => this with { Bounds = new Rect(Bounds.TopLeft + d, Bounds.Size) };
    internal override Annotation DeepCopy() => this with { };

    internal override ToolStyle CaptureStyle(ToolStyle defaults) => Redaction
        ? defaults with { Stroke = Color, Width = Width, Redaction = RedactionMode }
        : base.CaptureStyle(defaults) with { CornerRadius = EffectiveCornerRadius };

    internal override Annotation WithStyle(ToolStyle style) => Redaction
        ? this with { Ink = style.Stroke, Stroke = style.Width, Color = style.Stroke, Width = style.Width, RedactionMode = style.Redaction }
        : (RectangleAnnotation)ApplyCommonStyle(style, true) with { Ink = style.Stroke, Stroke = style.Width, CornerRadius = style.CornerRadius };
}

internal sealed record PenAnnotation(IReadOnlyList<Point> Points, Color Ink, double Stroke) : Annotation(Ink, Stroke)
{
    internal override Rect Extent => GeometryUtil.Bounds(Points);
    protected override void DrawContent(DrawingContext dc, AnnotationRenderContext context)
    {
        if (Points.Count < 2) return;
        dc.DrawGeometry(null, Pen(), GeometryUtil.Polyline(Points));
    }
    internal override bool Hit(Point p, double t) => GeometryUtil.NearPolyline(Points, p, t + Width);
    internal override Annotation Move(Vector d) => this with { Points = Points.Select(x => x + d).ToArray() };
    internal override Annotation DeepCopy() => this with { Points = Points.ToArray() };
    internal override Annotation WithStyle(ToolStyle style) => (PenAnnotation)ApplyCommonStyle(style, false) with { Ink = style.Stroke, Stroke = style.Width };
}

/// <summary>
/// Plain text, or a callout bubble when <see cref="Tail"/> is set. Callouts draw bold, contrast-aware text in a filled
/// rounded bubble whose width is <see cref="WrapWidth"/> (text wraps; the height follows the text) and a tapered tail
/// from the bubble edge to <see cref="Tail"/>. Callouts created before wrapping existed keep a null width and size to their text.
/// </summary>
internal sealed record TextAnnotation(Point Position, string Text, Color Ink, double FontSize) : Annotation(Ink, Math.Max(2, FontSize / 12))
{
    internal const double MinimumWrapWidth = 24;
    public Point? Tail { get; init; }
    public bool Background { get; init; }
    /// <summary>Text column width in capture pixels; null keeps text on the lines the user typed.</summary>
    public double? WrapWidth { get; init; }
    internal bool IsCallout => Tail is not null;
    internal double? EffectiveWrapWidth => WrapWidth is double width && double.IsFinite(width) ? Math.Max(MinimumWrapWidth, width) : null;

    internal static Typeface TextTypeface(bool callout) =>
        new(new FontFamily("Segoe UI"), FontStyles.Normal, callout ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);

    private FormattedText Format()
    {
        var formatted = new FormattedText(Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            TextTypeface(IsCallout), FontSize, new SolidColorBrush(TextColor), 1);
        if (EffectiveWrapWidth is double width) formatted.MaxTextWidth = width;
        return formatted;
    }

    internal Rect Bounds
    {
        get
        {
            FormattedText formatted = Format();
            double width = EffectiveWrapWidth ?? Math.Max(1, formatted.WidthIncludingTrailingWhitespace);
            return new Rect(Position, new Size(width, Math.Max(FontSize, formatted.Height)));
        }
    }

    internal Color BubbleColor => Fill ?? (IsCallout ? Color : GeometryUtil.Contrast(Color, dark: Color.FromArgb(220, 18, 18, 20)));
    internal Color TextColor => IsCallout ? GeometryUtil.Contrast(BubbleColor) : Color;
    /// <summary>Horizontal and vertical space between the text and the bubble edge.</summary>
    internal Vector BubblePadding => IsCallout ? new Vector(FontSize * .62, FontSize * .42) : new Vector(FontSize * .45, FontSize * .22);
    internal double BubbleRadius
    {
        get
        {
            Rect box = BubbleBounds;
            return Math.Min(IsCallout ? FontSize * .55 : FontSize * .45, Math.Min(box.Width, box.Height) / 2);
        }
    }

    internal Rect BubbleBounds
    {
        get
        {
            Rect bounds = Bounds;
            if (!IsCallout && !Background) return bounds;
            Vector padding = BubblePadding;
            bounds.Inflate(padding.X, padding.Y);
            return bounds;
        }
    }

    internal override Rect Extent { get { Rect r = BubbleBounds; if (Tail is Point tail) r.Union(tail); return r; } }

    protected override void DrawContent(DrawingContext dc, AnnotationRenderContext context)
    {
        if (IsCallout || Background) dc.DrawGeometry(new SolidColorBrush(BubbleColor), null, BubbleGeometry());
        dc.DrawText(Format(), Position);
    }

    /// <summary>The bubble and its tail as one shape, so translucent callouts never darken where they overlap.</summary>
    internal Geometry BubbleGeometry()
    {
        Rect box = BubbleBounds;
        double radius = BubbleRadius;
        Geometry bubble = new RectangleGeometry(box, radius, radius);
        if (Tail is not Point tail || box.Contains(tail)) return bubble;
        Geometry combined = new CombinedGeometry(GeometryCombineMode.Union, bubble, TailGeometry(box, tail));
        combined.Freeze();
        return combined;
    }

    /// <summary>A tapered wedge whose wide base sits just inside the bubble edge facing the tail point.</summary>
    private Geometry TailGeometry(Rect box, Point tail)
    {
        Point center = new(box.X + box.Width / 2, box.Y + box.Height / 2);
        Vector direction = tail - center;
        var geometry = new StreamGeometry();
        if (direction.Length < 1) return geometry;
        direction.Normalize();
        Point edge = EdgePoint(box, center, direction);
        double half = Math.Max(4, Math.Min(FontSize * .5, Math.Min(box.Width, box.Height) / 2.6));
        Point baseCenter = edge - direction * Math.Min(half * 1.4, (edge - center).Length);
        Vector normal = new(-direction.Y, direction.X);
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(baseCenter + normal * half, true, true);
            context.LineTo(tail, true, true);
            context.LineTo(baseCenter - normal * half, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Point EdgePoint(Rect box, Point center, Vector direction)
    {
        double scaleX = Math.Abs(direction.X) < 1e-6 ? double.PositiveInfinity : box.Width / 2 / Math.Abs(direction.X);
        double scaleY = Math.Abs(direction.Y) < 1e-6 ? double.PositiveInfinity : box.Height / 2 / Math.Abs(direction.Y);
        return center + direction * Math.Min(scaleX, scaleY);
    }

    internal override bool Hit(Point p, double t) =>
        GeometryUtil.Inflate(BubbleBounds, t).Contains(p) ||
        (Tail is Point tail && GeometryUtil.DistanceToSegment(p, BubbleBounds.TopLeft + (Vector)BubbleBounds.Size / 2, tail) <= t + FontSize * .2);
    internal override Annotation Move(Vector d) => this with { Position = Position + d, Tail = Tail is Point tail ? tail + d : null };
    internal override Annotation DeepCopy() => this with { };

    internal override ToolStyle CaptureStyle(ToolStyle defaults) => defaults with
    {
        Stroke = Color, FontSize = FontSize, Fill = Fill, TextBackground = Background, Opacity = Opacity, Shadow = Shadow
    };

    internal override Annotation WithStyle(ToolStyle style) => this with
    {
        Ink = style.Stroke, Color = style.Stroke, FontSize = style.FontSize, Width = Math.Max(2, style.FontSize / 12),
        WrapWidth = WrapWidth is double width ? width * style.FontSize / Math.Max(1, FontSize) : null,
        Fill = style.Fill, Background = !IsCallout && style.TextBackground, Opacity = style.Opacity, Shadow = style.Shadow, Dashed = false
    };
}

internal sealed record StepAnnotation(Point Center, int Number, Color Ink, double Radius) : Annotation(Ink, 2)
{
    internal override Rect Extent => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);
    protected override void DrawContent(DrawingContext dc, AnnotationRenderContext context)
    {
        dc.DrawEllipse(new SolidColorBrush(Color), null, Center, Radius, Radius);
        var f = new FormattedText(Number.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), Math.Max(1, Radius * 1.25), new SolidColorBrush(GeometryUtil.Contrast(Color)), 1);
        dc.DrawText(f, new Point(Center.X - f.Width / 2, Center.Y - f.Height / 2));
    }
    protected override void DrawSelection(DrawingContext dc) => dc.DrawEllipse(null, GeometryUtil.SelectionPen, Center, Radius + 2, Radius + 2);
    internal override bool Hit(Point p, double t) => (p - Center).Length <= Radius + t;
    internal override Annotation Move(Vector d) => this with { Center = Center + d };
    internal override Annotation DeepCopy() => this with { };
    internal static double RadiusFor(double width) => Math.Max(15, width * 4);
    internal override ToolStyle CaptureStyle(ToolStyle defaults) => defaults with { Stroke = Color, Width = Radius / 4, Opacity = Opacity, Shadow = Shadow };
    internal override Annotation WithStyle(ToolStyle style) => this with
    {
        Ink = style.Stroke, Color = style.Stroke, Radius = RadiusFor(style.Width), Opacity = style.Opacity, Shadow = style.Shadow, Dashed = false, Fill = null
    };
}

internal static class GeometryUtil
{
    internal static readonly Pen SelectionPen = CreateSelectionPen();
    internal static double DistanceToSegment(Point p, Point a, Point b) { Vector ab = b - a; if (ab.LengthSquared < .01) return (p - a).Length; double u = Math.Clamp(Vector.Multiply(p - a, ab) / ab.LengthSquared, 0, 1); return (p - (a + ab * u)).Length; }
    internal static bool NearRect(Rect r, Point p, double t) => r.Contains(p) && (Math.Abs(p.X-r.Left)<=t || Math.Abs(p.X-r.Right)<=t || Math.Abs(p.Y-r.Top)<=t || Math.Abs(p.Y-r.Bottom)<=t);
    internal static Rect Bounds(IEnumerable<Point> points) { var a = points.ToArray(); if (a.Length == 0) return Rect.Empty; double l=a.Min(p=>p.X), r=a.Max(p=>p.X), t=a.Min(p=>p.Y), b=a.Max(p=>p.Y); return new Rect(new Point(l,t), new Point(r,b)); }
    internal static Rect Inflate(Rect rect, double amount) { if (rect.IsEmpty) return rect; rect.Inflate(amount, amount); return rect; }

    internal static bool NearPolyline(IReadOnlyList<Point> points, Point p, double tolerance)
    {
        for (int i = 1; i < points.Count; i++) if (DistanceToSegment(p, points[i - 1], points[i]) <= tolerance) return true;
        return false;
    }

    internal static Geometry Polyline(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var c = geometry.Open()) { c.BeginFigure(points[0], false, false); c.PolyLineTo(points.Skip(1).ToList(), true, false); }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Returns white or a dark ink, whichever reads better on <paramref name="background"/>.</summary>
    internal static Color Contrast(Color background, Color? dark = null)
    {
        double luminance = (.2126 * background.R + .7152 * background.G + .0722 * background.B) / 255;
        return luminance > .62 ? dark ?? Color.FromRgb(20, 20, 22) : Colors.White;
    }

    /// <summary>Constrains a drag to 45-degree increments, as Shift does for lines and arrows.</summary>
    internal static Point Snap45(Point start, Point end)
    {
        Vector delta = end - start;
        if (delta.Length < .001) return end;
        double angle = Math.Round(Math.Atan2(delta.Y, delta.X) / (Math.PI / 4)) * (Math.PI / 4);
        return start + new Vector(Math.Cos(angle), Math.Sin(angle)) * delta.Length;
    }

    /// <summary>Constrains a drag to a square, as Shift does for rectangles and ellipses.</summary>
    internal static Point SnapSquare(Point start, Point end)
    {
        Vector delta = end - start;
        double size = Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y));
        return start + new Vector(Math.Sign(delta.X == 0 ? 1 : delta.X) * size, Math.Sign(delta.Y == 0 ? 1 : delta.Y) * size);
    }

    private static Pen CreateSelectionPen()
    {
        var pen = new Pen(Brushes.White, 1) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }
}
