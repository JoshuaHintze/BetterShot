using BetterShot.Editing;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot;

// Synthetic previews exercise real screenshot proportions without reading the desktop.
internal static class ShellPreviewSamples
{
    internal static CaptureDocument Board()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(B("#C93424"), null, new Rect(0, 0, 698, 459));
            for (int i = 0; i < 8; i++)
            {
                double x = 150 + i * 58;
                var trace = new StreamGeometry();
                using (var c = trace.Open()) { c.BeginFigure(new Point(x, 0), false, false); c.LineTo(new Point(x, 90 + i * 17), true, false); c.LineTo(new Point(x + 55, 145 + i * 17), true, false); c.LineTo(new Point(x + 55, 459), true, false); }
                dc.DrawGeometry(null, new Pen(B(i % 2 == 0 ? "#E65327" : "#E93A26"), 21), trace);
            }
            foreach (var p in new[] { new Point(56, 86), new Point(56, 292), new Point(266, 395), new Point(652, 51) })
            {
                dc.DrawEllipse(B("#166F4B"), new Pen(B("#162821"), 5), p, 54, 54);
                dc.DrawEllipse(B("#B7BBA0"), null, p, 37, 37);
                dc.DrawEllipse(B("#309997"), null, p, 25, 25);
            }
            dc.DrawRoundedRectangle(null, new Pen(B("#D4A324"), 13), new Rect(250, 100, 360, 264), 12, 12);
            dc.DrawRoundedRectangle(null, new Pen(B("#FFE142"), 7), new Rect(451, 38, 189, 364), 2, 2);
            Label(dc, "GND", 524, 91, 23, "#FFFFFF");
            Label(dc, "+2.5V", 323, 414, 21, "#FFFFFF");
            Label(dc, "1", 146, 211, 21, "#FFFFFF");
            Label(dc, "2", 551, 63, 17, "#FFFFFF");
        }
        return Finish(visual, 698, 459);
    }

    internal static CaptureDocument Document()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(B("#F2F0EA"), null, new Rect(0, 0, 720, 960));
            Label(dc, "FIELD NOTES", 58, 65, 18, "#6D7369");
            Label(dc, "A clearer perspective", 58, 130, 40, "#27382F");
            dc.DrawRectangle(B("#D1D9CC"), null, new Rect(58, 219, 604, 245));
            dc.DrawEllipse(B("#9BAF96"), null, new Point(370, 460), 280, 170);
            dc.DrawRectangle(B("#F2F0EA"), null, new Rect(0, 465, 720, 495));
            Label(dc, "Project overview", 58, 509, 25, "#27382F");
            for (int i = 0; i < 11; i++) dc.DrawRectangle(B("#BFC4BC"), null, new Rect(58, 563 + i * 24, i % 4 == 3 ? 408 : 604, 6));
        }
        return Finish(visual, 720, 960);
    }

    internal static CaptureDocument Code()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(B("#20242D"), null, new Rect(0, 0, 1000, 600));
            dc.DrawRectangle(B("#292E39"), null, new Rect(0, 0, 1000, 54));
            Label(dc, "capture.cs", 26, 17, 19, "#DEE3EB");
            string[] lines = { "public Capture CreatePreview()", "{", "    var image = CaptureSelectedArea();", "    var document = new Capture(image);", "", "    document.KeepOriginal();", "    thumbnails.Show(document);", "", "    return document;", "}" };
            for (int i = 0; i < lines.Length; i++)
            {
                Label(dc, (i + 1).ToString(), 27, 95 + i * 39, 18, "#69717E");
                Label(dc, lines[i], 76, 95 + i * 39, 22, i % 3 == 0 ? "#98CDA6" : "#C7D4EF");
            }
        }
        return Finish(visual, 1000, 600);
    }

    internal static CaptureDocument Wide()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(B("#EBE8E0"), null, new Rect(0, 0, 1280, 500));
            Label(dc, "STUDIO / JOURNAL", 48, 36, 19, "#73766B");
            Label(dc, "Room to think.", 48, 153, 62, "#27372C");
            Label(dc, "Simple spaces. Considered details.", 51, 245, 24, "#687061");
            dc.DrawRectangle(B("#B9C3AB"), null, new Rect(670, 0, 610, 500));
            dc.DrawRectangle(B("#CFD6BF"), null, new Rect(745, 66, 204, 327));
            dc.DrawRectangle(B("#83947A"), null, new Rect(966, 66, 239, 327));
            dc.DrawEllipse(B("#526E4D"), null, new Point(1052, 401), 171, 90);
            dc.DrawRectangle(B("#D2BA94"), null, new Rect(671, 438, 609, 62));
        }
        return Finish(visual, 1280, 500);
    }

    private static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    private static void Label(DrawingContext dc, string text, double x, double y, double size, string color) => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, B(color), 1), new Point(x, y));
    private static CaptureDocument Finish(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return new CaptureDocument(bitmap);
    }
}