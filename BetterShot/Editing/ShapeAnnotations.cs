using System.Windows.Media;

namespace BetterShot.Editing;

internal sealed record EllipseAnnotation(Rect Bounds, Color Color, double Width) : Annotation(Color, Width)
{
    internal override Rect Extent => Bounds;
    private Point Center => new(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2);

    protected override void DrawContent(DrawingContext dc, AnnotationRenderContext context) =>
        dc.DrawEllipse(FillBrush(), Pen(), Center, Bounds.Width / 2, Bounds.Height / 2);

    internal override bool Hit(Point p, double t)
    {
        double radiusX = Math.Max(.5, Bounds.Width / 2), radiusY = Math.Max(.5, Bounds.Height / 2);
        Vector offset = p - Center;
        double normalized = Math.Sqrt(offset.X * offset.X / (radiusX * radiusX) + offset.Y * offset.Y / (radiusY * radiusY));
        if (Fill is not null && normalized <= 1) return true;
        return Math.Abs(normalized - 1) * Math.Min(radiusX, radiusY) <= t + Width;
    }

    internal override Annotation Move(Vector d) => this with { Bounds = new Rect(Bounds.TopLeft + d, Bounds.Size) };
    internal override Annotation DeepCopy() => this with { };
    internal override Annotation WithStyle(ToolStyle style) => ApplyCommonStyle(style, true);
}

internal sealed record LineAnnotation(Point Start, Point End, Color Color, double Width) : Annotation(Color, Width)
{
    internal override Rect Extent => new(Start, End);
    protected override void DrawContent(DrawingContext dc, AnnotationRenderContext context) => dc.DrawLine(Pen(), Start, End);
    internal override bool Hit(Point p, double t) => GeometryUtil.DistanceToSegment(p, Start, End) <= t + Width;
    internal override Annotation Move(Vector d) => this with { Start = Start + d, End = End + d };
    internal override Annotation DeepCopy() => this with { };
    internal override Annotation WithStyle(ToolStyle style) => ApplyCommonStyle(style, false);
}

/// <summary>A wide translucent marker. One geometry is stroked so overlapping segments never double-darken.</summary>
internal sealed record HighlighterAnnotation(IReadOnlyList<Point> Points, Color Color, double Width) : Annotation(Color, Width)
{
    internal const double InkOpacity = .42;
    internal double MarkerWidth => Width * 2 + 6;
    internal override Rect Extent => GeometryUtil.Inflate(GeometryUtil.Bounds(Points), MarkerWidth / 2);

    protected override void DrawContent(DrawingContext dc, AnnotationRenderContext context)
    {
        if (Points.Count < 2) return;
        var pen = new Pen(new SolidColorBrush(Color), MarkerWidth) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Round };
        dc.PushOpacity(InkOpacity);
        dc.DrawGeometry(null, pen, GeometryUtil.Polyline(Points));
        dc.Pop();
    }

    internal override bool Hit(Point p, double t) => GeometryUtil.NearPolyline(Points, p, t + MarkerWidth / 2);
    internal override Annotation Move(Vector d) => this with { Points = Points.Select(x => x + d).ToArray() };
    internal override Annotation DeepCopy() => this with { Points = Points.ToArray() };
    internal override ToolStyle CaptureStyle(ToolStyle defaults) => base.CaptureStyle(defaults) with { Dashed = false, Shadow = false, Fill = null };
    internal override Annotation WithStyle(ToolStyle style) => ApplyCommonStyle(style with { Dashed = false, Shadow = false }, false);
}
