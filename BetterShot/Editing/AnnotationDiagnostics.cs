using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Editing;

/// <summary>Self-test checks for phase 1 annotations, crop, background composition, redaction burn-in, and style storage.</summary>
internal static class AnnotationDiagnostics
{
    private static readonly Color Gray = Color.FromRgb(100, 100, 100);
    private static readonly Color Red = ToolStyle.DefaultStroke;

    internal static void Verify()
    {
        VerifyShapes();
        VerifyStyles();
        VerifySpotlight();
        VerifyMagnify();
        VerifyRedactionBurnIn();
        VerifyCrop();
        VerifyBackground();
        VerifyHistoryOperations();
        VerifyStyleStore();
    }

    private static void VerifyShapes()
    {
        BitmapSource source = Solid(240, 160, Gray);
        BitmapSource ellipse = RenderSingle(source, new EllipseAnnotation(new Rect(20, 20, 60, 40), Red, 4));
        Check(IsRed(Pixel(ellipse, 20, 40)) && Near(Pixel(ellipse, 50, 40), Gray, 3), "Ellipse did not render its outline without fill.");
        BitmapSource filled = RenderSingle(source, new EllipseAnnotation(new Rect(100, 20, 40, 40), Red, 4) { Fill = Colors.Blue });
        Check(Near(Pixel(filled, 120, 40), Colors.Blue, 4), "Ellipse fill did not render.");

        BitmapSource line = RenderSingle(source, new LineAnnotation(new Point(10, 100), new Point(110, 100), Color.FromRgb(32, 195, 108), 6));
        Color linePixel = Pixel(line, 60, 100);
        Check(linePixel.G > 170 && linePixel.R < 70, "Line did not render.");

        BitmapSource marker = RenderSingle(source, new HighlighterAnnotation([new Point(10, 125), new Point(110, 125)], Color.FromRgb(255, 212, 0), 8));
        Color markerPixel = Pixel(marker, 60, 125);
        Check(markerPixel.R is > 140 and < 195 && markerPixel.B is > 35 and < 85, "Highlighter was not a translucent marker over the capture.");

        var callout = new TextAnnotation(new Point(160, 20), "Hi", Red, 20) { Tail = new Point(150, 110) };
        BitmapSource calloutImage = RenderSingle(source, callout);
        Rect bubble = callout.BubbleBounds;
        Check(IsRed(Pixel(calloutImage, (int)bubble.Left + 2, (int)(bubble.Top + bubble.Height / 2))), "Callout bubble did not render.");
        Point center = bubble.TopLeft + (Vector)bubble.Size / 2;
        Point alongTail = center + (callout.Tail!.Value - center) * .7;
        Check(IsRed(Pixel(calloutImage, (int)alongTail.X, (int)alongTail.Y)), "Callout tail did not render.");
        Check(callout.Hit(new Point(151, 106), 2) && !callout.Hit(new Point(20, 150), 2), "Callout hit testing ignored its tail or bubble.");

        var label = new TextAnnotation(new Point(20, 60), "Label", Colors.White, 20) { Background = true };
        BitmapSource labelImage = RenderSingle(source, label);
        Color labelPixel = Pixel(labelImage, (int)label.BubbleBounds.Left + 2, (int)(label.BubbleBounds.Top + label.BubbleBounds.Height / 2));
        Check(labelPixel.R < 60, "Text background did not render behind light text.");

        Check(new EllipseAnnotation(new Rect(20, 20, 60, 40), Red, 4).Hit(new Point(20, 40), 2) && new LineAnnotation(new Point(0, 0), new Point(50, 0), Red, 2).Hit(new Point(25, 1), 1),
            "Ellipse or line hit testing failed.");
    }

    private static void VerifyStyles()
    {
        BitmapSource source = Solid(200, 120, Gray);
        BitmapSource dashed = RenderSingle(source, new RectangleAnnotation(new Rect(20, 20, 160, 80), Red, 4) { Dashed = true, CornerRadius = 0 });
        int red = Enumerable.Range(30, 140).Count(x => IsRed(Pixel(dashed, x, 20)));
        Check(red is > 40 and < 126, "Dashed stroke was not dashed.");

        BitmapSource translucent = RenderSingle(source, new RectangleAnnotation(new Rect(20, 20, 100, 60), Red, 8) { Opacity = .5, CornerRadius = 0 });
        Color half = Pixel(translucent, 20, 50);
        Check(half.R is > 150 and < 205 && half.G is > 60 and < 100, "Opacity did not blend the annotation.");

        BitmapSource shadow = RenderSingle(source, new EllipseAnnotation(new Rect(40, 40, 80, 40), Red, 4) { Shadow = true });
        Color below = Pixel(shadow, 80, 84);
        Check(below.R < 96 && below.G < 96, "Shadow did not darken beneath the annotation.");

        var rectangle = new RectangleAnnotation(new Rect(10, 10, 50, 30), Red, 4);
        ToolStyle captured = rectangle.CaptureStyle(ToolStyle.DefaultFor(EditorTool.Rectangle));
        var restyled = (RectangleAnnotation)rectangle.WithStyle(captured with { Stroke = Colors.Blue, Width = 14, Dashed = true, CornerRadius = 6, Opacity = .5, Fill = Colors.Yellow });
        Check(restyled.Color == Colors.Blue && restyled.Ink == Colors.Blue && restyled.Width == 14 && restyled.Dashed && restyled.CornerRadius == 6 && restyled.Fill == Colors.Yellow && restyled.Opacity == .5,
            "Restyling did not apply every rectangle option.");
        var redaction = (RectangleAnnotation)new RectangleAnnotation(new Rect(0, 0, 10, 10), Colors.Black, 4, true).WithStyle(new ToolStyle { Opacity = .2, Shadow = true, Redaction = RedactionStyle.Blur });
        Check(redaction.Opacity == 1 && !redaction.Shadow && redaction.RedactionMode == RedactionStyle.Blur, "Redactions accepted transparency or lost their style.");
    }

    private static void VerifySpotlight()
    {
        Color light = Color.FromRgb(200, 200, 200);
        var document = new CaptureDocument(Solid(240, 160, light));
        document.Add(new RectangleAnnotation(new Rect(150, 20, 40, 30), Red, 4) { CornerRadius = 0 });
        document.Add(new SpotlightAnnotation(new Rect(20, 20, 60, 40), 0));
        document.Add(new SpotlightAnnotation(new Rect(60, 30, 60, 40), 0));
        document.Add(new RectangleAnnotation(new Rect(150, 100, 40, 30), Red, 4) { CornerRadius = 0 });
        BitmapSource rendered = document.Render();
        Color outside = Pixel(rendered, 220, 150);
        Check(outside.R is > 80 and < 100, "Spotlights did not share one dim layer of about 55%.");
        Check(Near(Pixel(rendered, 30, 30), light, 2) && Near(Pixel(rendered, 70, 45), light, 2), "Spotlight areas or their overlap were dimmed.");
        Check(Pixel(rendered, 150, 35).R < 160, "Annotations beneath the spotlight layer were not dimmed.");
        Check(IsRed(Pixel(rendered, 150, 115)), "Annotations added after the spotlight were dimmed.");
    }

    private static void VerifyMagnify()
    {
        var document = new CaptureDocument(HorizontalGradient(256, 160));
        document.Add(new MagnifyAnnotation(new Point(128, 80), 40, 2, Colors.White, 4));
        BitmapSource rendered = document.Render();
        Check(Math.Abs(Pixel(rendered, 128, 80).R - 128) <= 3, "Magnifier moved the pixel under its center.");
        Check(Math.Abs(Pixel(rendered, 148, 80).R - 138) <= 3, "Magnifier did not enlarge source pixels by its zoom.");
        Check(Math.Abs(Pixel(rendered, 200, 80).R - 200) <= 2, "Magnifier changed pixels outside its loupe.");
        Check(Near(Pixel(rendered, 168, 80), Colors.White, 40), "Magnifier ring did not render.");
    }

    private static void VerifyRedactionBurnIn()
    {
        var document = new CaptureDocument(Checker(120, 80));
        document.Add(new RectangleAnnotation(new Rect(10, 10, 50, 40), Colors.Black, 4, true) { RedactionMode = RedactionStyle.Pixelate });
        document.Add(new RectangleAnnotation(new Rect(70, 10, 40, 40), Colors.Black, 4, true) { RedactionMode = RedactionStyle.Blur });
        document.Add(new RectangleAnnotation(new Rect(10, 55, 40, 20), Colors.Black, 4, true));
        // Magnifiers over redactions must sample the burned-in result, never the original pixels.
        document.Add(new MagnifyAnnotation(new Point(35, 30), 15, 2, Colors.White, 0));
        document.Add(new MagnifyAnnotation(new Point(90, 30), 15, 3, Colors.White, 0));
        BitmapSource rendered = document.Render();
        foreach ((Int32Rect area, string name) in new[] { (new Int32Rect(11, 11, 48, 38), "Pixelate"), (new Int32Rect(71, 11, 38, 38), "Blur") })
            for (int y = area.Y; y < area.Y + area.Height; y++)
                for (int x = area.X; x < area.X + area.Width; x++)
                {
                    Color pixel = Pixel(rendered, x, y);
                    if (pixel.R is < 40 or > 215 || pixel.G is < 40 or > 215)
                        throw new InvalidOperationException($"{name} redaction exposed original pixels at {x},{y}.");
                }
        Check(Pixel(rendered, 12, 12) == Pixel(rendered, 20, 20), "Pixelate redaction did not use uniform blocks.");
        Check(Near(Pixel(rendered, 30, 65), Colors.Black, 1), "Solid redaction was not opaque.");
        Color exported = Pixel(DragPng(document), 30, 30);
        Check(exported.R is >= 40 and <= 215 && exported.G is >= 40 and <= 215, "Drag export did not burn in pixelation.");
    }

    private static void VerifyCrop()
    {
        var document = new CaptureDocument(EditorDiagnostics.MakeSource(96, 64, 144));
        document.Add(new RectangleAnnotation(new Rect(12, 10, 30, 20), Red, 4));
        BitmapSource full = document.Render();
        document.SetCrop(new Rect(10.4, 8.2, 40, 30));
        Check(document.Crop == new Rect(10, 8, 40, 30), "Crop was not snapped to whole pixels.");
        BitmapSource cropped = document.Render();
        Check(cropped.PixelWidth == 40 && cropped.PixelHeight == 30, "Crop did not set export dimensions.");
        Check(Near(Pixel(cropped, 0, 0), Pixel(full, 10, 8), 2) && Near(Pixel(cropped, 39, 29), Pixel(full, 49, 37), 2), "Crop exported the wrong region.");
        Check(document.Items.Count == 1 && document.Original.PixelWidth == 96, "Crop was destructive.");
        document.Undo();
        Check(document.Crop is null && document.Render().PixelWidth == 96, "Crop was not undoable.");
        document.Redo();
        Check(document.Crop is not null && document.Render().PixelWidth == 40, "Crop redo failed.");
        document.SetCrop(document.CanvasRect);
        Check(document.Crop is null, "A full-image crop was stored.");
        document.SetCrop(new Rect(-10, -10, 20, 20));
        Check(document.Crop == new Rect(0, 0, 10, 10), "Crop was not clipped to the capture.");
    }

    private static void VerifyBackground()
    {
        var document = new CaptureDocument(EditorDiagnostics.MakeSource(96, 64, 144));
        BitmapSource plain = document.Render();
        document.SetBackground(new BackgroundStyle("purple", 48, 18, true));
        BitmapSource framed = document.Render();
        Check(framed.PixelWidth == 96 + 96 && framed.PixelHeight == 64 + 96, "Background padding was not included in export dimensions.");
        Check(Near(Pixel(framed, 0, 0), Color.FromRgb(0x7F, 0x5A, 0xF0), 14), "Background preset did not fill the padding.");
        Check(Near(Pixel(framed, 48 + 40, 48 + 30), Pixel(plain, 40, 30), 2), "Background shifted or altered the capture.");
        Check(Math.Abs(Pixel(framed, 48, 48).B - Pixel(plain, 0, 0).B) > 40, "Background corner radius was not applied.");
        document.SetCrop(new Rect(10, 8, 40, 30));
        BitmapSource both = document.Render();
        Check(both.PixelWidth == 136 && both.PixelHeight == 126, "Crop and background dimensions did not combine.");
        BitmapSource dragged = DragPng(document);
        Check(dragged.PixelWidth == 136 && dragged.PixelHeight == 126, "Drag export did not use the composed dimensions.");
        document.SetBackground(document.Background with { Padding = 0 });
        Check(document.Render().PixelWidth == 40, "Zero padding changed dimensions.");
        document.Undo(); document.Undo(); document.Undo();
        Check(document.Render().PixelWidth == 96 && !document.Background.IsEnabled && document.Crop is null, "Background was not undoable.");
    }

    private static void VerifyHistoryOperations()
    {
        var document = new CaptureDocument(Solid(40, 40, Gray));
        var first = new RectangleAnnotation(new Rect(1, 1, 5, 5), Red, 2);
        var second = new EllipseAnnotation(new Rect(10, 10, 5, 5), Red, 2);
        var third = new LineAnnotation(new Point(0, 0), new Point(9, 9), Red, 2);
        document.Add(first); document.Add(second); document.Add(third);
        document.BringToFront(first);
        Check(document.Items.SequenceEqual(new Annotation[] { second, third, first }), "Bring to front failed.");
        document.SendToBack(first);
        Check(document.Items.SequenceEqual(new Annotation[] { first, second, third }), "Send to back failed.");
        document.Undo();
        Check(ReferenceEquals(document.Items[^1], first) || document.Items[^1] == first, "Reordering was not undoable.");
        document.Undo();
        Annotation thinner = third.WithStyle(new ToolStyle { Width = 4 });
        Annotation thicker = thinner.WithStyle(new ToolStyle { Width = 8 });
        document.Replace(third, thinner, "slider");
        document.Replace(thinner, thicker, "slider");
        document.Undo();
        Check(document.Items[^1] == third, "Style slider changes were not merged into one undo step.");
    }

    private static void VerifyStyleStore()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BetterShot-style-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "editor-styles.json");
        try
        {
            var store = new EditorStyleStore(path);
            Check(store.Get(EditorTool.Rectangle).Stroke == Color.FromRgb(0xFF, 0x42, 0x45) && store.Get(EditorTool.Rectangle).Width == 4, "Default style is not #FF4245 at 4 px.");
            ToolStyle custom = store.Get(EditorTool.Rectangle) with { Stroke = Colors.Blue, Width = 8, CornerRadius = 6, Dashed = true, Fill = Colors.Yellow, Opacity = .6 };
            store.Set(EditorTool.Rectangle, custom);
            store.AddPreset(EditorTool.Rectangle, custom);
            store.Set(EditorTool.Text, store.Get(EditorTool.Text) with { FontSize = 40, TextBackground = true });
            store.Flush();
            var reloaded = new EditorStyleStore(path);
            Check(reloaded.Get(EditorTool.Rectangle) == custom.Normalized() && reloaded.Presets(EditorTool.Rectangle).SequenceEqual([custom.Normalized()]), "Per-tool style or preset did not persist.");
            Check(reloaded.Get(EditorTool.Text).FontSize == 40 && reloaded.Get(EditorTool.Text).TextBackground && reloaded.Get(EditorTool.Ellipse) == ToolStyle.DefaultFor(EditorTool.Ellipse), "Tools did not keep separate styles.");
            File.WriteAllText(path, "{ not json");
            Check(new EditorStyleStore(path).Get(EditorTool.Rectangle) == ToolStyle.DefaultFor(EditorTool.Rectangle), "A corrupt style file was not ignored.");
            File.WriteAllText(path, "{\"Defaults\":{\"Rectangle\":{\"Width\":-5,\"Opacity\":7,\"Stroke\":\"bad\"},\"Bogus\":{}},\"Presets\":{\"Rectangle\":\"oops\"}}");
            var sanitized = new EditorStyleStore(path);
            ToolStyle clamped = sanitized.Get(EditorTool.Rectangle);
            Check(clamped.Width == 1 && clamped.Opacity == 1 && clamped.Stroke == ToolStyle.DefaultStroke && sanitized.Presets(EditorTool.Rectangle).Count == 0, "Invalid style values were not sanitized.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>Decodes the PNG offered by a real drag payload and removes its temporary file.</summary>
    private static BitmapSource DragPng(CaptureDocument document)
    {
        DataObject data = Presentation.CaptureDragData.CreateData(document);
        try
        {
            if (data.GetData("PNG", false) is not Stream stream) throw new InvalidOperationException("Drag export has no PNG.");
            stream.Position = 0;
            return new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        }
        finally
        {
            if (data.GetData(DataFormats.FileDrop, false) is string[] files) foreach (string file in files) File.Delete(file);
        }
    }

    private static BitmapSource RenderSingle(BitmapSource source, Annotation annotation)
    {
        var document = new CaptureDocument(source);
        document.Add(annotation);
        return document.Render();
    }

    private static BitmapSource Solid(int width, int height, Color color) => Create(width, height, (_, _) => color);
    private static BitmapSource HorizontalGradient(int width, int height) => Create(width, height, (x, _) => Color.FromRgb((byte)Math.Min(255, x), 60, 60));
    private static BitmapSource Checker(int width, int height) => Create(width, height, (x, y) => (x + y) % 2 == 0 ? Colors.White : Colors.Black);

    private static BitmapSource Create(int width, int height, Func<int, int, Color> color)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                Color value = color(x, y);
                int index = y * stride + x * 4;
                pixels[index] = value.B; pixels[index + 1] = value.G; pixels[index + 2] = value.R; pixels[index + 3] = 255;
            }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color Pixel(BitmapSource image, int x, int y) => EditorDiagnostics.Pixel(image, x, y);
    private static bool IsRed(Color color) => color.R > 200 && color.G < 120 && color.B < 120;
    private static bool Near(Color actual, Color expected, int tolerance) =>
        Math.Abs(actual.R - expected.R) <= tolerance && Math.Abs(actual.G - expected.G) <= tolerance && Math.Abs(actual.B - expected.B) <= tolerance;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
