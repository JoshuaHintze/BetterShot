using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Editing;

/// <summary>Shared render inputs for annotations that sample capture pixels.</summary>
internal sealed class AnnotationRenderContext
{
    private readonly CaptureDocument _document;
    private BitmapSource? _redactedBase;

    internal AnnotationRenderContext(CaptureDocument document) => _document = document;

    internal Rect CanvasRect => _document.CanvasRect;
    internal PixelBuffer Pixels => _document.Pixels;

    /// <summary>The original with every redaction burned in. Magnifiers sample this so they can never reveal hidden pixels.</summary>
    internal BitmapSource RedactedBase => _redactedBase ??= DocumentRenderer.RenderRedactedBase(_document, this);
}

/// <summary>Dims everything outside the union of all spotlights. The renderer draws one shared dim layer.</summary>
internal sealed record SpotlightAnnotation(Rect Bounds, double CornerRadius) : Annotation(Colors.White, 2)
{
    internal const double DimOpacity = .55;
    internal override Rect Extent => Bounds;
    protected override bool SupportsStyleEffects => false;
    internal Geometry Shape => new RectangleGeometry(Bounds, Math.Max(0, CornerRadius), Math.Max(0, CornerRadius));

    protected override void DrawContent(DrawingContext dc, AnnotationRenderContext context) { }
    protected override void DrawSelection(DrawingContext dc) => dc.DrawGeometry(null, GeometryUtil.SelectionPen, Shape);

    internal void DrawOutline(DrawingContext dc, double displayScale)
    {
        var pen = new Pen(Brushes.White, 1.5 / Math.Max(displayScale, .01)) { DashStyle = DashStyles.Dash };
        dc.DrawGeometry(null, pen, Shape);
    }

    internal override bool Hit(Point p, double t) => GeometryUtil.NearRect(GeometryUtil.Inflate(Bounds, t), p, t * 2 + 2);
    internal override Annotation Move(Vector d) => this with { Bounds = new Rect(Bounds.TopLeft + d, Bounds.Size) };
    internal override Annotation DeepCopy() => this with { };
    internal override ToolStyle CaptureStyle(ToolStyle defaults) => defaults with { CornerRadius = CornerRadius };
    internal override Annotation WithStyle(ToolStyle style) => this with { CornerRadius = style.CornerRadius };

    internal static void DrawDimLayer(DrawingContext dc, IReadOnlyCollection<SpotlightAnnotation> spotlights, Rect canvas)
    {
        if (spotlights.Count == 0) return;
        var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (SpotlightAnnotation spotlight in spotlights) holes.Children.Add(spotlight.Shape);
        var dim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(canvas), holes);
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * DimOpacity), 0, 0, 0)), null, dim);
    }
}

/// <summary>A circular loupe that shows enlarged, already-redacted capture pixels under its center.</summary>
internal sealed record MagnifyAnnotation(Point Center, double Radius, double Zoom, Color Color, double Width) : Annotation(Color, Width)
{
    internal override Rect Extent => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

    protected override void DrawContent(DrawingContext dc, AnnotationRenderContext context)
    {
        double radius = Math.Max(1, Radius), zoom = Math.Clamp(double.IsFinite(Zoom) ? Zoom : 2, 1, 8);
        dc.PushClip(new EllipseGeometry(Center, radius, radius));
        dc.DrawEllipse(Brushes.Black, null, Center, radius, radius);
        dc.PushTransform(new MatrixTransform(zoom, 0, 0, zoom, Center.X * (1 - zoom), Center.Y * (1 - zoom)));
        dc.DrawImage(context.RedactedBase, context.CanvasRect);
        dc.Pop();
        dc.Pop();
        if (Width > 0) dc.DrawEllipse(null, Pen(), Center, radius, radius);
    }

    protected override void DrawSelection(DrawingContext dc) => dc.DrawEllipse(null, GeometryUtil.SelectionPen, Center, Radius + Width / 2 + 2, Radius + Width / 2 + 2);
    internal override bool Hit(Point p, double t) => (p - Center).Length <= Radius + Width / 2 + t;
    internal override Annotation Move(Vector d) => this with { Center = Center + d };
    internal override Annotation DeepCopy() => this with { };
    internal override ToolStyle CaptureStyle(ToolStyle defaults) => base.CaptureStyle(defaults) with { Fill = null, MagnifyZoom = Zoom, MagnifyDiameter = Radius * 2 };
    internal override Annotation WithStyle(ToolStyle style) =>
        (MagnifyAnnotation)ApplyCommonStyle(style, false) with { Zoom = style.MagnifyZoom, Radius = Math.Max(4, style.MagnifyDiameter / 2) };
}

/// <summary>Draws opaque redactions. Pixelate and blur use only block averages, so no source pixel is copied to the output.</summary>
internal static class RedactionRenderer
{
    internal static int BlockSize(RedactionStyle style, double strength) => style == RedactionStyle.Blur
        ? (int)Math.Max(10, Math.Round(strength * 4))
        : (int)Math.Max(8, Math.Round(strength * 3));

    internal static void Draw(DrawingContext dc, Rect bounds, RedactionStyle style, Color color, double strength, AnnotationRenderContext context)
    {
        if (style == RedactionStyle.Solid || !Enum.IsDefined(style))
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(255, color.R, color.G, color.B)), null, bounds);
            return;
        }
        Int32Rect region = context.Pixels.Clip(bounds);
        if (region.Width <= 0 || region.Height <= 0) return;
        int block = BlockSize(style, strength);
        var target = new Rect(region.X, region.Y, region.Width, region.Height);
        dc.PushClip(new RectangleGeometry(target));
        dc.DrawRectangle(new SolidColorBrush(context.Pixels.Average(region)), null, target);
        if (style == RedactionStyle.Pixelate)
        {
            dc.DrawImage(context.Pixels.Pixelate(region, block), target);
        }
        else
        {
            BitmapSource coarse = context.Pixels.Downsample(region, block);
            var group = new DrawingGroup();
            RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.Linear);
            group.Children.Add(new ImageDrawing(coarse, new Rect(region.X, region.Y, coarse.PixelWidth * block, coarse.PixelHeight * block)));
            dc.DrawDrawing(group);
        }
        dc.Pop();
    }
}

/// <summary>Opaque BGRA copy of a capture used for redaction sampling.</summary>
internal sealed class PixelBuffer
{
    private readonly byte[] _pixels;

    internal PixelBuffer(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        Width = converted.PixelWidth;
        Height = converted.PixelHeight;
        _pixels = new byte[Width * Height * 4];
        converted.CopyPixels(_pixels, Width * 4, 0);
    }

    internal int Width { get; }
    internal int Height { get; }

    internal Int32Rect Clip(Rect bounds)
    {
        if (bounds.IsEmpty) return Int32Rect.Empty;
        int left = Math.Clamp((int)Math.Floor(bounds.Left), 0, Width), top = Math.Clamp((int)Math.Floor(bounds.Top), 0, Height);
        int right = Math.Clamp((int)Math.Ceiling(bounds.Right), 0, Width), bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom), 0, Height);
        return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    internal Color Average(Int32Rect region) => AverageCell(region.X, region.Y, region.Width, region.Height);

    /// <summary>One opaque pixel per block, each the mean of the block.</summary>
    internal BitmapSource Downsample(Int32Rect region, int block)
    {
        int columns = (region.Width + block - 1) / block, rows = (region.Height + block - 1) / block;
        byte[] output = new byte[columns * rows * 4];
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
                Write(output, (row * columns + column) * 4, CellAverage(region, block, column, row));
        return Freeze(BitmapSource.Create(columns, rows, 96, 96, PixelFormats.Bgra32, null, output, columns * 4));
    }

    /// <summary>Full-resolution mosaic of the region so block edges stay crisp regardless of bitmap scaling.</summary>
    internal BitmapSource Pixelate(Int32Rect region, int block)
    {
        int columns = (region.Width + block - 1) / block, rows = (region.Height + block - 1) / block;
        int stride = region.Width * 4;
        byte[] output = new byte[stride * region.Height];
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                Color average = CellAverage(region, block, column, row);
                int yEnd = Math.Min(region.Height, (row + 1) * block), xEnd = Math.Min(region.Width, (column + 1) * block);
                for (int y = row * block; y < yEnd; y++)
                    for (int x = column * block; x < xEnd; x++)
                        Write(output, y * stride + x * 4, average);
            }
        return Freeze(BitmapSource.Create(region.Width, region.Height, 96, 96, PixelFormats.Bgra32, null, output, stride));
    }

    private Color CellAverage(Int32Rect region, int block, int column, int row)
    {
        int x = region.X + column * block, y = region.Y + row * block;
        return AverageCell(x, y, Math.Min(block, region.X + region.Width - x), Math.Min(block, region.Y + region.Height - y));
    }

    private Color AverageCell(int x, int y, int width, int height)
    {
        long blue = 0, green = 0, red = 0, count = 0;
        for (int row = y; row < y + height; row++)
        {
            int index = (row * Width + x) * 4;
            for (int column = 0; column < width; column++, index += 4)
            {
                blue += _pixels[index]; green += _pixels[index + 1]; red += _pixels[index + 2]; count++;
            }
        }
        if (count == 0) return Colors.Black;
        return Color.FromArgb(255, (byte)(red / count), (byte)(green / count), (byte)(blue / count));
    }

    private static void Write(byte[] output, int index, Color color)
    {
        output[index] = color.B; output[index + 1] = color.G; output[index + 2] = color.R; output[index + 3] = 255;
    }

    private static BitmapSource Freeze(BitmapSource bitmap) { bitmap.Freeze(); return bitmap; }
}

/// <summary>Soft drop shadow for vector annotations, rendered as offset translucent silhouettes.</summary>
internal static class AnnotationShadow
{
    internal static void Draw(DrawingContext dc, double strokeWidth, Action<DrawingContext> drawContent)
    {
        var drawing = new DrawingGroup();
        using (DrawingContext content = drawing.Open()) drawContent(content);
        Rect bounds = drawing.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
        var mask = new DrawingBrush(drawing)
        {
            Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds,
            ViewportUnits = BrushMappingMode.Absolute, Viewport = bounds
        };
        double offset = Math.Clamp(strokeWidth * .6, 2, 8);
        foreach ((double distance, byte alpha) in new[] { (offset, (byte)72), (offset * 1.8, (byte)34) })
        {
            dc.PushTransform(new TranslateTransform(distance * .35, distance));
            dc.PushOpacityMask(mask);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)), null, bounds);
            dc.Pop();
            dc.Pop();
        }
    }
}
