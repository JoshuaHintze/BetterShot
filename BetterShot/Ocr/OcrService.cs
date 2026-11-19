using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinGraphics = Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace BetterShot.Ocr;

/// <summary>Thrown when Windows has no OCR language installed (or OCR is unsupported on this PC).</summary>
internal sealed class OcrUnavailableException(string message) : Exception(message)
{
    internal const string NoLanguageMessage =
        "Windows can't read text yet because no text-recognition language is installed. Add a language in Settings > Time & language > Language & region (include its optical character recognition feature), then try again.";
}

/// <summary>
/// On-device text recognition with Windows.Media.Ocr. Images never leave the PC. Recognition runs on the
/// thread pool, one image at a time, and returns word boxes in the source image's pixel coordinates.
/// </summary>
internal static class OcrService
{
    /// <summary>Windows OCR struggles with very small text; tiny captures are enlarged up to this shorter side.</summary>
    private const int MinimumShortSide = 64;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static WinOcr.OcrEngine? _engine;

    /// <summary>The source must be frozen so it can be read off the UI thread.</summary>
    internal static async Task<RecognizedText> RecognizeAsync(BitmapSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsFrozen) throw new ArgumentException("The image must be frozen before text recognition.", nameof(source));
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => RecognizeCoreAsync(source, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Returns null when OCR is ready, otherwise a user-facing reason.</summary>
    internal static string? UnavailableReason()
    {
        try { _ = Engine(); return null; }
        catch (OcrUnavailableException ex) { return ex.Message; }
        catch (Exception ex) when (ex is TypeLoadException or COMException or PlatformNotSupportedException or EntryPointNotFoundException)
        {
            return "Text recognition isn't available on this version of Windows.";
        }
    }

    private static async Task<RecognizedText> RecognizeCoreAsync(BitmapSource source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WinOcr.OcrEngine engine = Engine();
        double scale = OcrScale.Factor(source.PixelWidth, source.PixelHeight, (int)WinOcr.OcrEngine.MaxImageDimension, MinimumShortSide);
        BitmapSource prepared = Math.Abs(scale - 1) < 1e-9 ? source : Resize(source, scale);
        using WinGraphics.SoftwareBitmap bitmap = ToSoftwareBitmap(prepared);
        cancellationToken.ThrowIfCancellationRequested();
        WinOcr.OcrResult result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        return Convert(result).Scaled(1 / scale);
    }

    private static WinOcr.OcrEngine Engine()
    {
        if (_engine is not null) return _engine;
        WinOcr.OcrEngine? engine = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            Windows.Globalization.Language? fallback = WinOcr.OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
            if (fallback is not null) engine = WinOcr.OcrEngine.TryCreateFromLanguage(fallback);
        }
        return _engine = engine ?? throw new OcrUnavailableException(OcrUnavailableException.NoLanguageMessage);
    }

    private static BitmapSource Resize(BitmapSource source, double scale)
    {
        var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        resized.Freeze();
        return resized;
    }

    private static WinGraphics.SoftwareBitmap ToSoftwareBitmap(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight, stride = width * 4;
        byte[] pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        OcrPixels.FlattenOntoWhite(pixels);
        return WinGraphics.SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), WinGraphics.BitmapPixelFormat.Bgra8, width, height, WinGraphics.BitmapAlphaMode.Premultiplied);
    }

    private static RecognizedText Convert(WinOcr.OcrResult result)
    {
        var lines = new List<TextLine>();
        foreach (WinOcr.OcrLine line in result.Lines)
        {
            TextWord[] words = line.Words
                .Select(word => new TextWord(word.Text, new Rect(word.BoundingRect.X, word.BoundingRect.Y, word.BoundingRect.Width, word.BoundingRect.Height)))
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .ToArray();
            if (words.Length > 0) lines.Add(new TextLine(string.IsNullOrWhiteSpace(line.Text) ? RecognizedText.JoinWords(words) : line.Text, words));
        }
        return new RecognizedText(lines);
    }
}

/// <summary>Pure sizing rules for OCR input, kept separate so diagnostics can check them without Windows OCR.</summary>
internal static class OcrScale
{
    /// <summary>Downscales so neither side exceeds <paramref name="maxDimension"/>; enlarges tiny images (2x at most) toward <paramref name="minimumShortSide"/>.</summary>
    internal static double Factor(int width, int height, int maxDimension, int minimumShortSide)
    {
        if (width <= 0 || height <= 0 || maxDimension <= 0) return 1;
        int longSide = Math.Max(width, height), shortSide = Math.Min(width, height);
        if (longSide > maxDimension) return (double)maxDimension / longSide;
        if (shortSide >= minimumShortSide) return 1;
        double enlarge = Math.Min(2, (double)minimumShortSide / shortSide);
        return Math.Min(enlarge, (double)maxDimension / longSide);
    }
}

internal static class OcrPixels
{
    /// <summary>Composites premultiplied BGRA onto white and makes it opaque, so transparent areas read as paper, not black.</summary>
    internal static void FlattenOntoWhite(byte[] premultipliedBgra)
    {
        for (int index = 0; index + 3 < premultipliedBgra.Length; index += 4)
        {
            int transparency = 255 - premultipliedBgra[index + 3];
            if (transparency == 0) continue;
            premultipliedBgra[index] = (byte)Math.Min(255, premultipliedBgra[index] + transparency);
            premultipliedBgra[index + 1] = (byte)Math.Min(255, premultipliedBgra[index + 1] + transparency);
            premultipliedBgra[index + 2] = (byte)Math.Min(255, premultipliedBgra[index + 2] + transparency);
            premultipliedBgra[index + 3] = 255;
        }
    }
}
