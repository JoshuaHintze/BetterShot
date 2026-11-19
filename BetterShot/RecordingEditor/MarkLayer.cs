using BetterShot.Editing;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace BetterShot.RecordingEditor;

/// <summary>
/// Draws the marks visible at the playhead over the video, in video pixels, with the screenshot editor's annotation
/// renderer. Pixelate/blur redactions sample live video, so they are shown by <see cref="RedactionPreviewLayer"/>
/// instead; exports compute them from decoded frames with the editor's block-average redaction.
/// </summary>
internal sealed class MarkLayer : FrameworkElement
{
    private static readonly Brush HandleFill = CreateBrush(Colors.White);
    private static readonly Brush HandleStroke = CreateBrush(Color.FromRgb(0x1A, 0x1B, 0x1E));
    private AnnotationRenderContext? _context;
    private IReadOnlyList<Annotation> _items = [];
    private Rect? _selection;
    private IReadOnlyList<Point> _handles = [];
    private Annotation? _preview;
    private double _displayScale = 1;

    internal MarkLayer() => ClipToBounds = true;

    /// <summary>Uses a 1-bit placeholder canvas of the video size so spotlights and redactions know the frame bounds without copying video pixels.</summary>
    internal void Configure(int width, int height)
    {
        int stride = (width + 7) / 8;
        BitmapSource placeholder = BitmapSource.Create(width, height, 96, 96, PixelFormats.BlackWhite, null, new byte[stride * height], stride);
        _context = new CaptureDocument(placeholder).RenderContext;
        Width = width;
        Height = height;
    }

    internal void Show(IReadOnlyList<Annotation> items, Rect? selection, IReadOnlyList<Point> handles, Annotation? preview, double displayScale)
    {
        _items = items;
        _selection = selection;
        _handles = handles;
        _preview = preview;
        _displayScale = Math.Max(0.01, displayScale);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_context is null) return;
        IEnumerable<Annotation> drawn = _preview is null ? _items : _items.Append(_preview);
        DocumentRenderer.DrawAnnotations(dc, drawn, null, _context);
        double pixel = 1 / _displayScale;
        if (_selection is Rect selection)
        {
            var outline = new Pen(Brushes.White, 1.5 * pixel) { DashStyle = DashStyles.Dash };
            Rect inflated = selection;
            inflated.Inflate(3 * pixel, 3 * pixel);
            dc.DrawRectangle(null, new Pen(HandleStroke, 3 * pixel), inflated);
            dc.DrawRectangle(null, outline, inflated);
        }
        foreach (Point handle in _handles)
        {
            double size = 9 * pixel;
            dc.DrawRectangle(HandleFill, new Pen(HandleStroke, 1.5 * pixel), new Rect(handle.X - size / 2, handle.Y - size / 2, size, size));
        }
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Live preview of pixelate/blur redactions and the "whole picture blurred while lost" state: each redaction is a
/// clipped box filled with the video itself (a VisualBrush) that is either blurred or rendered at low resolution and
/// scaled up without smoothing (pixelated). Preview only; the export uses true block averages of decoded frames.
/// </summary>
internal sealed class RedactionPreviewLayer : Canvas
{
    private readonly Visual _video;
    private string[] _styles = [];

    internal RedactionPreviewLayer(Visual video)
    {
        _video = video;
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>Shows the redactions; when only their positions changed (a followed redaction while playing) the boxes are moved, not rebuilt.</summary>
    internal void Show(IEnumerable<RectangleAnnotation> redactions)
    {
        RectangleAnnotation[] shown = redactions.Where(redaction => redaction.Bounds.Width >= 1 && redaction.Bounds.Height >= 1).ToArray();
        string[] styles = shown.Select(redaction => $"{redaction.RedactionMode}:{RedactionRenderer.BlockSize(redaction.RedactionMode, redaction.Width)}").ToArray();
        if (styles.SequenceEqual(_styles))
        {
            for (int index = 0; index < shown.Length; index++) Place((Border)Children[index], shown[index].Bounds);
            return;
        }
        _styles = styles;
        Children.Clear();
        foreach (RectangleAnnotation redaction in shown)
        {
            Rect bounds = redaction.Bounds;
            int block = RedactionRenderer.BlockSize(redaction.RedactionMode, redaction.Width);
            var fill = new VisualBrush(_video) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds, Stretch = Stretch.Fill };
            var content = new Rectangle { Width = bounds.Width, Height = bounds.Height, Fill = fill };
            if (redaction.RedactionMode == RedactionStyle.Blur) content.Effect = new BlurEffect { Radius = block * 1.5, KernelType = KernelType.Gaussian };
            else
            {
                content.CacheMode = new BitmapCache(Math.Clamp(1.0 / block, 0.01, 1));
                RenderOptions.SetBitmapScalingMode(content, BitmapScalingMode.NearestNeighbor);
            }
            var box = new Border { ClipToBounds = true, Background = Brushes.DimGray, Child = content };
            Place(box, bounds);
            Children.Add(box);
        }
    }

    private static void Place(Border box, Rect bounds)
    {
        box.Width = bounds.Width;
        box.Height = bounds.Height;
        SetLeft(box, bounds.X);
        SetTop(box, bounds.Y);
        if (box.Child is not Rectangle content) return;
        content.Width = bounds.Width;
        content.Height = bounds.Height;
        if (content.Fill is VisualBrush fill) fill.Viewbox = bounds;
    }
}
