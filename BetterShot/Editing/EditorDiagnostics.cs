using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Editing;

public static class EditorDiagnostics
{
    public static void RunSelfTest()
    {
        BitmapSource source = MakeSource(96, 64, 144);
        var document = new CaptureDocument(source);
        int changes = 0; document.Changed += (_, _) => changes++;
        document.Add(new RectangleAnnotation(new Rect(4, 5, 30, 22), Colors.Red, 4));
        document.Add(new RectangleAnnotation(new Rect(40, 10, 20, 15), Color.FromArgb(20, 0, 0, 0), 2, true));
        document.Add(new PenAnnotation([new Point(5, 50), new Point(30, 42), new Point(60, 54)], Colors.Yellow, 3));
        if (document.Items.Count != 3 || changes != 3) throw new InvalidOperationException("Annotations were not retained or change events were lost.");

        BitmapSource rendered = document.Render();
        if (rendered.PixelWidth != 96 || rendered.PixelHeight != 64) throw new InvalidOperationException("Render changed native pixel dimensions.");
        Color redact = Pixel(rendered, 45, 14);
        if (redact.A != 255 || redact.R > 5 || redact.G > 5 || redact.B > 5) throw new InvalidOperationException("Redaction was not opaque.");
        Color sentinel = Pixel(rendered, 95, 63);
        if (sentinel.R < 180 || sentinel.G < 180) throw new InvalidOperationException("High-DPI source was scaled or cropped during rendering.");

        document.Undo(); if (document.Items.Count != 2) throw new InvalidOperationException("Undo failed.");
        document.Redo(); if (document.Items.Count != 3 || document.Items.OfType<PenAnnotation>().Single().Points.Count != 3) throw new InvalidOperationException("Redo/deep copy failed.");
        VerifyDragExport(document);
    }

    private static void VerifyDragExport(CaptureDocument document)
    {
        var imageData = Presentation.CaptureDragData.CreateData(document);
        if (!imageData.GetDataPresent(DataFormats.Bitmap, false) || !imageData.GetDataPresent(DataFormats.FileDrop, false) || imageData.GetDataPresent(DataFormats.Text) || imageData.GetDataPresent(DataFormats.UnicodeText))
            throw new InvalidOperationException("Default drag must offer image data and a PNG attachment without text paths.");
        if (imageData.GetData("PNG", false) is not System.IO.Stream pngStream)
            throw new InvalidOperationException("Image drag is missing its PNG representation.");
        pngStream.Position = 0;
        var imageDecoder = new PngBitmapDecoder(pngStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (Pixel(imageDecoder.Frames[0], 45, 14) != Colors.Black || imageDecoder.Frames[0].PixelWidth != document.Original.PixelWidth)
            throw new InvalidOperationException("Image drag lost redaction or native dimensions.");
        pngStream.Position = 0;
        var paths = new List<string>((string[])imageData.GetData(DataFormats.FileDrop, false));
        VerifyNativeFileDrop(imageData, paths.Single());
        try
        {
            string Export()
            {
                var data = Presentation.CaptureDragData.CreateData(document, asFile: true);
                if (!data.GetDataPresent(DataFormats.FileDrop, false) ||
                    data.GetData(DataFormats.FileDrop, false) is not string[] files || files.Length != 1)
                    throw new InvalidOperationException("Drag does not provide a native file-drop payload.");
                if (data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Text) || data.GetDataPresent(DataFormats.UnicodeText))
                    throw new InvalidOperationException("File mode should offer only a file attachment.");
                VerifyNativeFileDrop(data, files[0]);
                paths.Add(files[0]);
                if (!System.IO.Path.IsPathFullyQualified(files[0]) || !System.IO.File.Exists(files[0]))
                    throw new InvalidOperationException("Drag file is not available to the receiving app.");
                return files[0];
            }
            BitmapSource Read(string path)
            {
                using var stream = System.IO.File.OpenRead(path);
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return decoder.Frames[0];
            }
            var compatible = Read(paths[0]);
            if (compatible.PixelWidth != document.Original.PixelWidth || Pixel(compatible, 45, 14) != Colors.Black)
                throw new InvalidOperationException("Compatible PNG attachment lost dimensions or redaction.");
            string firstPath = Export();
            var first = Read(firstPath);
            if (first.PixelWidth != document.Original.PixelWidth || first.PixelHeight != document.Original.PixelHeight ||
                Pixel(first, 45, 14) != Colors.Black)
                throw new InvalidOperationException("Dropped PNG lost original dimensions or opaque redaction.");
            document.Add(new RectangleAnnotation(new Rect(70, 10, 15, 15), Colors.Blue, 1, true));
            string secondPath = Export();
            if (firstPath == secondPath || Pixel(Read(secondPath), 75, 15) != Colors.Blue ||
                Pixel(Read(firstPath), 75, 15) == Colors.Blue)
                throw new InvalidOperationException("Drag exports must reflect current edits without changing an earlier drop.");
        }
        finally
        {
            foreach (string path in paths) System.IO.File.Delete(path);
        }
    }

    private static void VerifyNativeFileDrop(DataObject data, string expectedPath)
    {
        var format = new System.Runtime.InteropServices.ComTypes.FORMATETC
        {
            cfFormat = 15,
            dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
        };
        ((System.Runtime.InteropServices.ComTypes.IDataObject)data).GetData(ref format, out var medium);
        try
        {
            var path = new System.Text.StringBuilder(32768);
            if (DragQueryFile(medium.unionmember, uint.MaxValue, null, 0) != 1 || DragQueryFile(medium.unionmember, 0, path, (uint)path.Capacity) == 0 || path.ToString() != expectedPath)
                throw new InvalidOperationException("OLE did not expose a valid native PNG file drop to external applications.");
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint index, System.Text.StringBuilder? path, uint length);
    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium);

    public static CaptureDocument CreateSampleDocument()
    {
        var document = new CaptureDocument(MakeSource(920, 560, 96));
        document.Add(new ArrowAnnotation(new Point(160, 130), new Point(335, 205), Color.FromRgb(255, 67, 74), 8));
        document.Add(new RectangleAnnotation(new Rect(405, 105, 300, 170), Color.FromRgb(255, 212, 0), 7));
        document.Add(new TextAnnotation(new Point(120, 365), "Edit, explain, and share.", Colors.White, 38));
        document.Add(new StepAnnotation(new Point(765, 390), 1, Color.FromRgb(44, 137, 255), 28));
        document.Add(new RectangleAnnotation(new Rect(530, 450, 230, 42), Colors.Black, 1, true));
        return document;
    }

    private static BitmapSource MakeSource(int width, int height, double dpi)
    {
        int stride = width * 4; byte[] pixels = new byte[stride * height];
        for (int y=0;y<height;y++) for(int x=0;x<width;x++) { int i=y*stride+x*4; byte shade=(byte)(30 + 42.0*x/width + 24.0*y/height); pixels[i]=shade;pixels[i+1]=shade;pixels[i+2]=shade;pixels[i+3]=255; }
        int s=(height-1)*stride+(width-1)*4; pixels[s]=20;pixels[s+1]=230;pixels[s+2]=230;pixels[s+3]=255;
        var bitmap=BitmapSource.Create(width,height,dpi,dpi,PixelFormats.Bgra32,null,pixels,stride); bitmap.Freeze(); return bitmap;
    }

    private static Color Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); byte[] p=new byte[4]; converted.CopyPixels(new Int32Rect(x,y,1,1),p,4,0); return Color.FromArgb(p[3],p[2],p[1],p[0]);
    }
}
