using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Editing;

/// <summary>
/// Output geometry of a composition. Annotation coordinates are original capture pixels; adding
/// <see cref="Offset"/> converts them to output pixels after crop and background padding.
/// </summary>
internal readonly record struct CompositionLayout(Rect Content, int Padding, BackgroundStyle Background, int OutputWidth, int OutputHeight)
{
    internal Vector Offset => new(Padding - Content.X, Padding - Content.Y);
    internal Rect ContentInOutput => new(Padding, Padding, Content.Width, Content.Height);
}

/// <summary>The single composition path shared by the editor preview, Copy, Save, drag export, and thumbnails.</summary>
internal static class DocumentRenderer
{
    private const int ShadowLayers = 10;

    internal static CompositionLayout Layout(CaptureDocument document, bool compose)
    {
        Rect content = compose && document.Crop is Rect crop ? crop : document.CanvasRect;
        BackgroundStyle background = compose ? document.Background : BackgroundStyle.None;
        int padding = background.PaddingPixels;
        int width = Math.Max(1, (int)Math.Round(content.Width)) + padding * 2;
        int height = Math.Max(1, (int)Math.Round(content.Height)) + padding * 2;
        return new CompositionLayout(content, padding, background, width, height);
    }

    internal static BitmapSource Render(CaptureDocument document, IEnumerable<Annotation> items, Func<Annotation, bool>? isSelected, bool compose)
    {
        CompositionLayout layout = Layout(document, compose);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen()) Compose(dc, document, layout, items, isSelected);
        var bitmap = new RenderTargetBitmap(layout.OutputWidth, layout.OutputHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    internal static BitmapSource RenderRedactedBase(CaptureDocument document, AnnotationRenderContext context)
    {
        Rect canvas = document.CanvasRect;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(document.Original, canvas);
            foreach (RectangleAnnotation redaction in document.Items.OfType<RectangleAnnotation>().Where(item => item.Redaction))
                redaction.Draw(dc, false, context);
        }
        var bitmap = new RenderTargetBitmap((int)canvas.Width, (int)canvas.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Compose(DrawingContext dc, CaptureDocument document, CompositionLayout layout, IEnumerable<Annotation> items, Func<Annotation, bool>? isSelected)
    {
        Rect target = layout.ContentInOutput;
        double radius = 0;
        if (layout.Background.IsEnabled)
        {
            radius = Math.Min(layout.Background.EffectiveCornerRadius, Math.Min(target.Width, target.Height) / 2);
            DrawBackground(dc, layout, target, radius);
        }
        dc.PushClip(new RectangleGeometry(target, radius, radius));
        dc.PushTransform(new TranslateTransform(layout.Offset.X, layout.Offset.Y));
        dc.DrawImage(document.Original, document.CanvasRect);
        DrawAnnotations(dc, items, isSelected, document.RenderContext);
        dc.Pop();
        dc.Pop();
    }

    private static void DrawBackground(DrawingContext dc, CompositionLayout layout, Rect target, double radius)
    {
        BackgroundPreset preset = BackgroundPresets.Find(layout.Background.Preset)!;
        dc.DrawRectangle(preset.CreateBrush(), null, new Rect(0, 0, layout.OutputWidth, layout.OutputHeight));
        if (!layout.Background.Shadow) return;
        double reach = Math.Min(22, layout.Padding * .6);
        if (reach < 1) return;
        var shadow = new SolidColorBrush(Color.FromArgb(9, 0, 0, 0));
        shadow.Freeze();
        for (int layer = 1; layer <= ShadowLayers; layer++)
        {
            double spread = reach * layer / ShadowLayers;
            Rect rect = target;
            rect.Inflate(spread, spread);
            rect.Offset(0, reach * .35);
            dc.DrawRoundedRectangle(shadow, null, rect, radius + spread, radius + spread);
        }
    }

    /// <summary>Draws annotations in order. All spotlights share one dim layer at the position of the first spotlight.</summary>
    internal static void DrawAnnotations(DrawingContext dc, IEnumerable<Annotation> items, Func<Annotation, bool>? isSelected, AnnotationRenderContext context)
    {
        List<Annotation> ordered = items.ToList();
        SpotlightAnnotation[] spotlights = ordered.OfType<SpotlightAnnotation>().ToArray();
        bool dimDrawn = false;
        foreach (Annotation item in ordered)
        {
            if (item is SpotlightAnnotation && !dimDrawn)
            {
                SpotlightAnnotation.DrawDimLayer(dc, spotlights, context.CanvasRect);
                dimDrawn = true;
            }
            item.Draw(dc, isSelected?.Invoke(item) ?? false, context);
        }
    }
}
