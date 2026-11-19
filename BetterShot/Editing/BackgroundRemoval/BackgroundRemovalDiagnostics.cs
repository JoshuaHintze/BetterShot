using BetterShot.Export;
using BetterShot.Persistence;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Editing.BackgroundRemoval;

/// <summary>
/// Background removal checks: tensor preprocessing, mask post-processing and feathering, SHA-256 verification,
/// mask compositing, undo/redo, history persistence, and JPEG flattening. Model inference runs only when a verified
/// model is already installed; this diagnostic never downloads it.
/// </summary>
internal static class BackgroundRemovalDiagnostics
{
    internal static async Task<string> RunAsync(string fixtureRoot)
    {
        VerifyPreprocessing();
        VerifyResizeAndNormalize();
        VerifyPostprocessing();
        VerifyFeather();
        VerifyHashVerification(Path.Combine(fixtureRoot, "model"));
        VerifyMaskCompositing();
        VerifyDocumentUndoAndHistory(Path.Combine(fixtureRoot, "history"));
        VerifyJpegFlattening();
        string inference = await VerifyInferenceIfInstalledAsync();
        return "U²-Netp preprocessing, min-max/resize/feather postprocessing, SHA-256 model verification, mask compositing, crop embedding, undo/redo, history persistence, JPEG flattening on white, " + inference;
    }

    private static void VerifyPreprocessing()
    {
        const int width = 10, height = 6;
        var pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4) { pixels[index] = 0; pixels[index + 1] = 128; pixels[index + 2] = 255; pixels[index + 3] = 255; }
        float[] tensor = BackgroundRemovalMath.Preprocess(pixels, width, height);
        int plane = BackgroundRemovalMath.ModelSize * BackgroundRemovalMath.ModelSize;
        Check(tensor.Length == 3 * plane, "The input tensor is not 3×320×320.");
        Check(Near(tensor[0], (1f - 0.485f) / 0.229f), "The red plane was not first or not normalized by the image maximum and ImageNet mean/std.");
        Check(Near(tensor[plane + 77], (128f / 255f - 0.456f) / 0.224f), "The green plane is wrong.");
        Check(Near(tensor[2 * plane + plane - 1], (0f - 0.406f) / 0.225f), "The blue plane is wrong.");
    }

    private static void VerifyResizeAndNormalize()
    {
        float[] identity = BackgroundRemovalMath.ResizeBilinear([1, 2, 3, 4], 2, 2, 2, 2);
        Check(identity.SequenceEqual(new float[] { 1, 2, 3, 4 }), "Same-size resampling changed values.");
        float[] constant = BackgroundRemovalMath.ResizeBilinear(Enumerable.Repeat(7f, 9).ToArray(), 3, 3, 17, 5);
        Check(constant.All(value => Near(value, 7)), "Resampling a constant image changed it.");
        float[] upscaled = BackgroundRemovalMath.ResizeBilinear([0, 1], 2, 1, 4, 1);
        Check(Near(upscaled[0], 0) && Near(upscaled[1], 0.25f) && Near(upscaled[2], 0.75f) && Near(upscaled[3], 1), "Bilinear upscaling is not pixel-center aligned.");
        float[] normalized = BackgroundRemovalMath.NormalizeMinMax(new float[] { 2, 4, 6 });
        Check(Near(normalized[0], 0) && Near(normalized[1], 0.5f) && Near(normalized[2], 1), "Min-max normalization is wrong.");
        Check(BackgroundRemovalMath.NormalizeMinMax(new float[] { 3, 3 }).All(value => value == 0), "A constant prediction did not normalize to zero.");
    }

    private static void VerifyPostprocessing()
    {
        int size = BackgroundRemovalMath.ModelSize;
        var prediction = new float[size * size];
        for (int row = 0; row < size; row++)
        for (int column = size / 2; column < size; column++) prediction[row * size + column] = 0.9f;
        byte[] mask = BackgroundRemovalMath.Postprocess(prediction, 640, 100, featherRadius: 2);
        Check(mask.Length == 640 * 100, "The mask was not resized to the image.");
        Check(mask[50 * 640 + 10] == 0 && mask[50 * 640 + 630] == 255, "Background and subject were not mapped to 0 and 255.");
        byte edge = mask[50 * 640 + 320];
        Check(edge is > 0 and < 255, "The mask edge was not softened.");
    }

    private static void VerifyFeather()
    {
        var flat = Enumerable.Repeat((byte)200, 30).ToArray();
        Check(BackgroundRemovalMath.Feather(flat, 6, 5, 2).All(value => value == 200), "Feathering changed a uniform mask.");
        var step = new byte[20];
        for (int column = 10; column < 20; column++) step[column] = 255;
        byte[] feathered = BackgroundRemovalMath.Feather(step, 20, 1, 2);
        Check(feathered[0] == 0 && feathered[19] == 255 && feathered[9] is > 0 and < 255 && feathered[10] is > 0 and < 255, "Feathering did not soften a hard edge locally.");
        Check(BackgroundRemovalMath.FeatherRadiusFor(400, 300) == 1 && BackgroundRemovalMath.FeatherRadiusFor(4000, 3000) == 4, "Feather radius scaling changed.");
    }

    private static void VerifyHashVerification(string root)
    {
        using (var known = new MemoryStream(Encoding.ASCII.GetBytes("abc")))
        {
            Check(BackgroundRemovalModel.HashMatches(known, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"), "SHA-256 verification rejected a correct hash.");
            Check(!BackgroundRemovalModel.HashMatches(known, BackgroundRemovalModel.Sha256), "SHA-256 verification accepted a wrong hash.");
        }
        Check(BackgroundRemovalModel.Sha256.Length == 64 && BackgroundRemovalModel.Sha256.All(Uri.IsHexDigit), "The pinned model hash is malformed.");
        Check(BackgroundRemovalModel.DownloadUrl.StartsWith("https://github.com/danielgatis/rembg/releases/download/", StringComparison.Ordinal), "The model URL is not pinned to the rembg release.");
        var model = new BackgroundRemovalModel(root);
        Directory.CreateDirectory(root);
        File.WriteAllText(model.ModelPath, "not a model");
        Check(!model.IsInstalled() && !File.Exists(model.ModelPath), "A corrupted model was accepted or kept.");
    }

    private static void VerifyMaskCompositing()
    {
        var pixels = new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 200 };
        AlphaMask.ApplyToBgra(pixels, new byte[] { 255, 0, 128 });
        Check(pixels[3] == 255 && pixels[7] == 0 && pixels[11] == 100 && pixels[2] == 255, "Mask compositing changed colors or multiplied alpha incorrectly.");
        BitmapSource image = Solid(4, 2, Colors.Red);
        var mask = new AlphaMask(4, 2, [255, 255, 0, 0, 255, 255, 0, 0]);
        BitmapSource applied = mask.ApplyTo(image);
        Check(Alpha(applied, 0, 0) == 255 && Alpha(applied, 3, 1) == 0, "The mask was not applied to the image.");
        AlphaMask roundTrip = AlphaMask.DecodePng(new MemoryStream(mask.EncodePng()));
        Check(roundTrip.Values.SequenceEqual(mask.Values) && roundTrip.Width == 4 && roundTrip.Height == 2, "The mask PNG did not round-trip.");
        AlphaMask embedded = AlphaMask.Embed(new AlphaMask(2, 1, [200, 100]), new Int32Rect(1, 1, 2, 1), 4, 2);
        Check(embedded.Values.ToArray().SequenceEqual(new byte[] { 0, 0, 0, 0, 0, 200, 100, 0 }), "A cropped-area mask was embedded at the wrong place.");
    }

    private static void VerifyDocumentUndoAndHistory(string root)
    {
        BitmapSource image = Solid(40, 30, Colors.SteelBlue);
        var document = new CaptureDocument(image);
        var values = new byte[40 * 30];
        for (int index = 0; index < values.Length; index++) values[index] = (byte)(index % 40 < 20 ? 255 : 0);
        var mask = new AlphaMask(40, 30, values);
        int changes = 0;
        document.Changed += (_, _) => changes++;
        document.SetBackgroundMask(mask);
        Check(changes == 1 && document.CanUndo && ReferenceEquals(document.BackgroundMask, mask), "Removing the background was not one undoable change.");
        BitmapSource rendered = document.Render();
        Check(Alpha(rendered, 5, 5) == 255 && Alpha(rendered, 35, 5) == 0, "The rendered document did not keep transparency.");
        Check(ReferenceEquals(document.Original, image), "Background removal replaced the original pixels.");
        document.Undo();
        Check(document.BackgroundMask is null && Alpha(document.Render(), 35, 5) == 255, "Undo did not restore the background.");
        document.Redo();
        Check(ReferenceEquals(document.BackgroundMask, mask), "Redo did not remove the background again.");
        bool rejected = false;
        try { document.SetBackgroundMask(new AlphaMask(2, 2, new byte[4])); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "A mask of the wrong size was accepted.");

        var store = new CaptureHistoryStore(root);
        store.Save(document);
        CaptureDocument loaded = new CaptureHistoryStore(root).Load(DateTime.UtcNow).Single();
        Check(loaded.BackgroundMask is { } stored && stored.Values.SequenceEqual(mask.Values), "The background mask was not stored with the capture.");
        Check(Alpha(loaded.Render(), 35, 5) == 0 && Alpha(loaded.Original, 35, 5) == 255, "The reloaded capture lost its transparency or its original.");
    }

    private static void VerifyJpegFlattening()
    {
        BitmapSource transparent = new AlphaMask(8, 8, new byte[64]).ApplyTo(Solid(8, 8, Colors.Black));
        using var buffer = new MemoryStream();
        ImageExporter.Encode(transparent, CaptureImageFormat.Jpeg, 95, buffer);
        buffer.Position = 0;
        BitmapSource decoded = new JpegBitmapDecoder(buffer, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(4, 4, 1, 1), pixel, 4, 0);
        Check(pixel[0] > 240 && pixel[1] > 240 && pixel[2] > 240, "JPEG export did not flatten transparency onto white.");
    }

    private static async Task<string> VerifyInferenceIfInstalledAsync()
    {
        var model = new BackgroundRemovalModel();
        if (!model.IsInstalled()) return "model inference skipped (model not installed; never downloaded by diagnostics)";
        const int width = 240, height = 180;
        var pixels = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
        for (int column = 0; column < width; column++)
        {
            int offset = (row * width + column) * 4;
            bool subject = Math.Pow(column - 120, 2) + Math.Pow(row - 90, 2) < 55 * 55;
            pixels[offset] = subject ? (byte)40 : (byte)235;
            pixels[offset + 1] = subject ? (byte)90 : (byte)235;
            pixels[offset + 2] = subject ? (byte)230 : (byte)235;
            pixels[offset + 3] = 255;
        }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        AlphaMask mask = await BackgroundRemover.ComputeMaskAsync(image, model.ModelPath, CancellationToken.None);
        byte center = mask.Values[90 * width + 120], corner = mask.Values[5 * width + 5];
        Check(center > corner + 100, $"Inference did not separate the subject (center {center}, corner {corner}).");
        return $"model inference (center {center}, corner {corner})";
    }

    private static BitmapSource Solid(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4) { pixels[index] = color.B; pixels[index + 1] = color.G; pixels[index + 2] = color.R; pixels[index + 3] = 255; }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte Alpha(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) < 1e-4f;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Background removal: " + message);
    }
}
