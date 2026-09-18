using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Editing;

public sealed class CaptureDocument
{
    private readonly List<Annotation> _items = [];
    private readonly Stack<List<Annotation>> _undo = [];
    private readonly Stack<List<Annotation>> _redo = [];

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

    internal void Add(Annotation annotation)
    {
        SaveUndo();
        _items.Add(annotation);
        RaiseChanged();
    }

    internal void Replace(Annotation oldValue, Annotation newValue)
    {
        int index = _items.IndexOf(oldValue);
        if (index < 0) return;
        SaveUndo();
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

    internal void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Clone(_items));
        Restore(_undo.Pop());
    }

    internal void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Clone(_items));
        Restore(_redo.Pop());
    }

    internal void RestoreAnnotations(IEnumerable<Annotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        _items.Clear();
        _items.AddRange(Clone(annotations));
        _undo.Clear();
        _redo.Clear();
    }
    public BitmapSource Render()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(Original, new Rect(0, 0, Original.PixelWidth, Original.PixelHeight));
            foreach (Annotation item in _items) item.Draw(dc, false);
        }
        var result = new RenderTargetBitmap(Original.PixelWidth, Original.PixelHeight,
            96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    private void SaveUndo()
    {
        _undo.Push(Clone(_items));
        _redo.Clear();
    }

    private void Restore(List<Annotation> state)
    {
        _items.Clear();
        _items.AddRange(Clone(state));
        RaiseChanged();
    }

    private static List<Annotation> Clone(IEnumerable<Annotation> source) => source.Select(x => x.DeepCopy()).ToList();
    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

internal abstract record Annotation(Color Color, double Width)
{
    internal abstract void Draw(DrawingContext dc, bool selected);
    internal abstract bool Hit(Point p, double tolerance);
    internal abstract Annotation Move(Vector delta);
    internal abstract Annotation DeepCopy();
    protected Pen Pen() => new(new SolidColorBrush(Color), Width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
}

internal sealed record ArrowAnnotation(Point Start, Point End, Color Ink, double Stroke) : Annotation(Ink, Stroke)
{
    internal override void Draw(DrawingContext dc, bool selected)
    {
        var pen = Pen(); dc.DrawLine(pen, Start, End);
        Vector d = Start - End; if (d.Length < 1) return; d.Normalize();
        Vector n = new(-d.Y, d.X); double size = Math.Max(10, Width * 4.2);
        var figure = new StreamGeometry();
        using (var c = figure.Open()) { c.BeginFigure(End, true, true); c.LineTo(End + d * size + n * size * .48, true, false); c.LineTo(End + d * size - n * size * .48, true, false); }
        figure.Freeze(); dc.DrawGeometry(new SolidColorBrush(Color), null, figure);
        DrawSelection(dc, selected, new Rect(Start, End));
    }
    internal override bool Hit(Point p, double t) => GeometryUtil.DistanceToSegment(p, Start, End) <= t + Width;
    internal override Annotation Move(Vector d) => this with { Start = Start + d, End = End + d };
    internal override Annotation DeepCopy() => this with { };
    private static void DrawSelection(DrawingContext dc, bool selected, Rect r) { if (selected) dc.DrawRectangle(null, GeometryUtil.SelectionPen, r); }
}

internal sealed record RectangleAnnotation(Rect Bounds, Color Ink, double Stroke, bool Redaction = false) : Annotation(Ink, Stroke)
{
    internal override void Draw(DrawingContext dc, bool selected)
    {
        if (Redaction) dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, Color.R, Color.G, Color.B)), null, Bounds);
        else dc.DrawRoundedRectangle(null, Pen(), Bounds, Math.Min(8, Width * 1.3), Math.Min(8, Width * 1.3));
        if (selected) dc.DrawRectangle(null, GeometryUtil.SelectionPen, Bounds);
    }
    internal override bool Hit(Point p, double t) => Redaction ? Bounds.Contains(p) : GeometryUtil.NearRect(Bounds, p, t + Width);
    internal override Annotation Move(Vector d) => this with { Bounds = new Rect(Bounds.TopLeft + d, Bounds.Size) };
    internal override Annotation DeepCopy() => this with { };
}

internal sealed record PenAnnotation(IReadOnlyList<Point> Points, Color Ink, double Stroke) : Annotation(Ink, Stroke)
{
    internal override void Draw(DrawingContext dc, bool selected)
    {
        if (Points.Count < 2) return; var geometry = new StreamGeometry();
        using (var c = geometry.Open()) { c.BeginFigure(Points[0], false, false); c.PolyLineTo(Points.Skip(1).ToList(), true, false); }
        geometry.Freeze(); dc.DrawGeometry(null, Pen(), geometry);
        if (selected) dc.DrawRectangle(null, GeometryUtil.SelectionPen, GeometryUtil.Bounds(Points));
    }
    internal override bool Hit(Point p, double t) { for (int i = 1; i < Points.Count; i++) if (GeometryUtil.DistanceToSegment(p, Points[i - 1], Points[i]) <= t + Width) return true; return false; }
    internal override Annotation Move(Vector d) => this with { Points = Points.Select(x => x + d).ToArray() };
    internal override Annotation DeepCopy() => this with { Points = Points.ToArray() };
}

internal sealed record TextAnnotation(Point Position, string Text, Color Ink, double FontSize) : Annotation(Ink, Math.Max(2, FontSize / 12))
{
    private FormattedText Format() => new(Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), FontSize, new SolidColorBrush(Color), 1);
    internal Rect Bounds { get { var f = Format(); return new Rect(Position, new Size(Math.Max(1, f.WidthIncludingTrailingWhitespace), Math.Max(FontSize, f.Height))); } }
    internal override void Draw(DrawingContext dc, bool selected) { var f = Format(); dc.DrawText(f, Position); if (selected) dc.DrawRectangle(null, GeometryUtil.SelectionPen, Bounds); }
    internal override bool Hit(Point p, double t) => Bounds.Contains(p);
    internal override Annotation Move(Vector d) => this with { Position = Position + d };
    internal override Annotation DeepCopy() => this with { };
}

internal sealed record StepAnnotation(Point Center, int Number, Color Ink, double Radius) : Annotation(Ink, 2)
{
    internal override void Draw(DrawingContext dc, bool selected)
    {
        dc.DrawEllipse(new SolidColorBrush(Color), null, Center, Radius, Radius);
        var f = new FormattedText(Number.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), Radius * 1.25, Brushes.White, 1);
        dc.DrawText(f, new Point(Center.X - f.Width / 2, Center.Y - f.Height / 2));
        if (selected) dc.DrawEllipse(null, GeometryUtil.SelectionPen, Center, Radius + 2, Radius + 2);
    }
    internal override bool Hit(Point p, double t) => (p - Center).Length <= Radius + t;
    internal override Annotation Move(Vector d) => this with { Center = Center + d };
    internal override Annotation DeepCopy() => this with { };
}

internal static class GeometryUtil
{
    internal static readonly Pen SelectionPen = new(Brushes.White, 1) { DashStyle = DashStyles.Dash };
    internal static double DistanceToSegment(Point p, Point a, Point b) { Vector ab = b - a; if (ab.LengthSquared < .01) return (p - a).Length; double u = Math.Clamp(Vector.Multiply(p - a, ab) / ab.LengthSquared, 0, 1); return (p - (a + ab * u)).Length; }
    internal static bool NearRect(Rect r, Point p, double t) => r.Contains(p) && (Math.Abs(p.X-r.Left)<=t || Math.Abs(p.X-r.Right)<=t || Math.Abs(p.Y-r.Top)<=t || Math.Abs(p.Y-r.Bottom)<=t);
    internal static Rect Bounds(IEnumerable<Point> points) { var a = points.ToArray(); if (a.Length == 0) return Rect.Empty; double l=a.Min(p=>p.X), r=a.Max(p=>p.X), t=a.Min(p=>p.Y), b=a.Max(p=>p.Y); return new Rect(new Point(l,t), new Point(r,b)); }
}
