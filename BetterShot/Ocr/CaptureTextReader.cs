using BetterShot.Editing;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Ocr;

/// <summary>
/// Reads text from a capture's original pixels with every redaction masked out first, so text under a
/// redaction can never be copied, indexed, or searched. Results are cached per document until its
/// redactions change. Call from the UI thread; recognition itself runs in the background.
/// </summary>
internal static class CaptureTextReader
{
    /// <summary>Bump when recognition or masking changes so stored index entries are rebuilt.</summary>
    internal const string StampVersion = "ocr1";
    private static readonly ConditionalWeakTable<CaptureDocument, CacheEntry> Cache = new();
    private static CancellationTokenSource _shutdown = new();

    /// <summary>All recognized text in original pixels (whole capture, ignoring crop), with redacted words removed.</summary>
    internal static async Task<RecognizedText> ReadAsync(CaptureDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        Rect[] redactions = Redactions(document);
        string key = RedactionKey(document, redactions);
        if (!Cache.TryGetValue(document, out CacheEntry? entry) || entry.Key != key || entry.Task.IsFaulted || entry.Task.IsCanceled)
        {
            BitmapSource masked = RenderMasked(document, redactions);
            entry = new CacheEntry(key, OcrService.RecognizeAsync(masked, _shutdown.Token));
            Cache.AddOrUpdate(document, entry);
        }
        RecognizedText text = await entry.Task.WaitAsync(cancellationToken);
        // Redactions added while recognition was running must still hide their words.
        return text.ExcludingRegions(Redactions(document));
    }

    /// <summary>Text the user can currently see: inside the crop when one is applied.</summary>
    internal static async Task<RecognizedText> ReadVisibleAsync(CaptureDocument document, CancellationToken cancellationToken)
    {
        RecognizedText text = await ReadAsync(document, cancellationToken);
        return text.Within(document.Crop ?? document.CanvasRect);
    }

    /// <summary>Identifies the inputs that affect searchable text (size, crop, redactions) for the persisted index.</summary>
    internal static string IndexStamp(CaptureDocument document)
    {
        string crop = document.Crop is Rect visible ? FormatRect(visible) : "full";
        return StampVersion + ":" + Hash(RedactionKey(document, Redactions(document)) + "|" + crop);
    }

    /// <summary>Cancels in-flight recognition (application shutdown).</summary>
    internal static void CancelAll()
    {
        CancellationTokenSource previous = _shutdown;
        _shutdown = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
    }

    internal static Rect[] Redactions(CaptureDocument document) => document.Items.OfType<RectangleAnnotation>()
        .Where(item => item.Redaction && !item.Bounds.IsEmpty)
        .Select(item => item.Bounds)
        .ToArray();

    /// <summary>
    /// The original capture with each redaction replaced by solid black. Every redaction style is masked the
    /// same way so pixelated or blurred text can't be partially recognized.
    /// </summary>
    internal static BitmapSource RenderMasked(CaptureDocument document, IReadOnlyCollection<Rect> redactions)
    {
        Rect canvas = document.CanvasRect;
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawImage(document.Original, canvas);
            foreach (Rect region in redactions)
            {
                Rect expanded = region;
                expanded.Inflate(1, 1);
                context.DrawRectangle(Brushes.Black, null, expanded);
            }
        }
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)canvas.Width), Math.Max(1, (int)canvas.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static string RedactionKey(CaptureDocument document, IEnumerable<Rect> redactions)
    {
        var builder = new StringBuilder();
        builder.Append(document.Original.PixelWidth).Append('x').Append(document.Original.PixelHeight);
        foreach (Rect region in redactions) builder.Append(';').Append(FormatRect(region));
        return builder.ToString();
    }

    private static string FormatRect(Rect rect) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{rect.X:0.##},{rect.Y:0.##},{rect.Width:0.##},{rect.Height:0.##}");

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20].ToLowerInvariant();

    private sealed record CacheEntry(string Key, Task<RecognizedText> Task);
}
