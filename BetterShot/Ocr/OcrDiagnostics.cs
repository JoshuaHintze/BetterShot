using BetterShot.Editing;
using BetterShot.Persistence;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterShot.Ocr;

/// <summary>
/// Phase 3 text checks: sensitive-text detection (incl. Luhn), selection intersection and line grouping,
/// crop/padding mapping, redaction masking before OCR, OCR sizing, index persistence/corruption/removal,
/// search filtering, the settings field, the library UI, and a real on-device OCR pass when Windows has an
/// OCR language. Uses isolated fixture folders only; never touches the real library or index.
/// </summary>
internal static class OcrDiagnostics
{
    internal static async Task RunAsync(string? output)
    {
        string directory = string.IsNullOrWhiteSpace(output) ? AppContext.BaseDirectory : Path.HasExtension(output) ? Path.GetDirectoryName(Path.GetFullPath(output))! : output;
        Directory.CreateDirectory(directory);
        string runRoot = Path.Combine(directory, "ocr-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        VerifyDetector();
        VerifySelection();
        VerifyMapping();
        VerifyMasking();
        VerifyScaling();
        VerifyIndex(Path.Combine(runRoot, "index"));
        VerifySearch();
        VerifySettings();
        VerifyUndoBatch();
        await VerifyLibraryUiAsync(Path.Combine(runRoot, "library"), directory);
        string ocr = await VerifyRealOcrAsync(directory);
        App.WriteTestResult(Path.Combine(directory, "ocr-test.txt"),
            "PASS: email/phone/card (Luhn)/key/IP detection and negatives, word-to-selection intersection and line grouping, crop and padding mapping, redaction masking before OCR, OCR sizing and alpha flattening, index persistence/corruption/removal/pruning, search filtering, settings field, one-step auto-redaction undo, library search and text-reading toggle. " + ocr);
    }

    private static void VerifyDetector()
    {
        (string Text, SensitiveKind Kind)[] positives =
        [
            ("Email me at jane.doe@example.com today", SensitiveKind.Email),
            ("Call (555) 123-4567 now", SensitiveKind.Phone),
            ("Office +44 20 7946 0958", SensitiveKind.Phone),
            ("Mobile 555-123-4567.", SensitiveKind.Phone),
            ("Card 4111 1111 1111 1111 exp 12/27", SensitiveKind.CardNumber),
            ("Card 5500-0000-0000-0004", SensitiveKind.CardNumber),
            ("OPENAI_API_KEY=sk-proj-abc123DEF456ghi789JKL", SensitiveKind.Key),
            ("token ghp_1234567890abcdefghijABCDEFGHIJ", SensitiveKind.Key),
            ("aws AKIAIOSFODNN7EXAMPLE", SensitiveKind.Key),
            ("slack xoxb-1234567890-abcdefghij", SensitiveKind.Key),
            ("secret: a8F3kq9Zp2LmX7vB4nR6tY1w", SensitiveKind.Key),
            ("server 192.168.1.20 is down", SensitiveKind.IpAddress),
            ("link-local fe80::1ff:fe23:4567:890a", SensitiveKind.IpAddress)
        ];
        foreach ((string text, SensitiveKind kind) in positives)
        {
            IReadOnlyList<SensitiveSpan> spans = SensitiveTextDetector.FindSpans(text);
            Check(spans.Any(span => span.Kind == kind), $"Detector missed {kind} in \"{text}\".");
        }
        string[] negatives =
        [
            "Hello BetterShot 2026",
            "Meeting on 2026-11-19 at 10:30:45",
            "Version 10.0.19041.0",
            "internationalization and localization",
            "BetterShot-2026-release-notes.pdf",
            "Total: $1,234.56 for 3 items",
            "Card 4111 1111 1111 1112",
            "Room 4521, floor 3",
            "std::vector<int> values",
            "Order #20261119 shipped"
        ];
        foreach (string text in negatives)
        {
            IReadOnlyList<SensitiveSpan> spans = SensitiveTextDetector.FindSpans(text);
            Check(spans.Count == 0, $"Detector flagged \"{text}\" ({string.Join(", ", spans.Select(span => $"{span.Kind}: {text.Substring(span.Start, span.Length)}"))}).");
        }
        Check(SensitiveTextDetector.PassesLuhn("4111111111111111") && SensitiveTextDetector.PassesLuhn("79927398713"), "Luhn rejected valid numbers.");
        Check(!SensitiveTextDetector.PassesLuhn("4111111111111112") && !SensitiveTextDetector.PassesLuhn("12a4"), "Luhn accepted invalid numbers.");

        // OCR splits a card number into words; the match must cover exactly the number words as one item.
        TextLine card = Line(0, "Card", "4111", "1111", "1111", "1111");
        IReadOnlyList<SensitiveMatch> matches = SensitiveTextDetector.Find(new RecognizedText([card, Line(1, "Hello", "BetterShot")]));
        Check(matches.Count == 1 && matches[0].Kind == SensitiveKind.CardNumber, "Multi-word card number was not one match.");
        Rect expected = card.Words[1].Bounds; expected.Union(card.Words[4].Bounds);
        Check(matches[0].Bounds == expected, "Card match bounds do not cover exactly the number words.");
        Rect padded = SensitiveTextDetector.PaddedBounds(matches[0].Bounds, new Rect(0, 0, 10_000, 10_000));
        Check(padded.Contains(matches[0].Bounds) && padded.Width > matches[0].Bounds.Width, "Redaction padding is missing.");
        Rect clipped = SensitiveTextDetector.PaddedBounds(new Rect(0, 0, 40, 20), new Rect(0, 0, 100, 100));
        Check(clipped.Left >= 0 && clipped.Top >= 0, "Padding left the capture.");
    }

    private static void VerifySelection()
    {
        var text = new RecognizedText([Line(0, "alpha", "beta", "gamma"), Line(1, "delta", "epsilon"), Line(2, "zeta")]);
        Check(text.FullText == string.Join(Environment.NewLine, "alpha beta gamma", "delta epsilon", "zeta"), "Full text lost its lines.");
        // Covers beta..gamma on line 0 and epsilon on line 1, not zeta.
        Rect selection = new(Column(1) + 5, 2, Column(1) + 10, LineHeight + 10);
        Check(text.TextInside(selection) == "beta gamma" + Environment.NewLine + "epsilon", $"Selection text was \"{text.TextInside(selection)}\".");
        Check(text.TextInside(new Rect(0, LineHeight * 5, 50, 10)).Length == 0, "Empty selection returned text.");
        Check(text.TextInside(new Rect(0, 0, 10_000, 10_000)) == text.FullText, "Select-all did not equal the full text.");
        Check(text.Within(new Rect(0, LineHeight, 10_000, LineHeight - 1)).FullText == "delta epsilon", "Crop filtering kept words outside the crop.");
        var cjk = new RecognizedText([new TextLine("日本語 テキスト", [new TextWord("日本", new Rect(0, 0, 10, 10)), new TextWord("語", new Rect(10, 0, 10, 10)), new TextWord("abc", new Rect(30, 0, 10, 10))])]);
        Check(cjk.TextInside(new Rect(0, 0, 25, 10)) == "日本語", "CJK words were joined with spaces.");
        RecognizedText scaled = new RecognizedText([Line(0, "x1")]).Scaled(2);
        Check(scaled.Lines[0].Words[0].Bounds == new Rect(0, 0, 140, LineHeight * 1.6), "OCR scale mapping is wrong.");
    }

    private static void VerifyMapping()
    {
        var layout = new CompositionLayout(new Rect(100, 50, 400, 300), 48, BackgroundStyle.None, 496, 396);
        Rect source = OcrRegionMapper.DisplayToSource(new Rect(34, 34, 50, 20), .5, layout);
        Check(source == new Rect(120, 70, 100, 40), $"Crop/padding mapping is wrong: {source}.");
        Rect back = OcrRegionMapper.SourceToDisplay(source, .5, layout);
        Check(back == new Rect(34, 34, 50, 20), "Display mapping does not round-trip.");
        Rect clamped = OcrRegionMapper.DisplayToSource(new Rect(0, 0, 1000, 1000), 1, layout);
        Check(clamped == layout.Content, "Selection outside the crop was not clamped to visible content.");

        var document = new CaptureDocument(Solid(800, 600, Colors.White));
        document.SetCrop(new Rect(100, 50, 400, 300));
        CompositionLayout real = document.Layout();
        Rect mapped = OcrRegionMapper.DisplayToSource(new Rect(real.Padding + 20, real.Padding + 20, 40, 40), 1, real);
        Check(mapped == new Rect(120, 70, 40, 40), $"Document crop offset mapping is wrong: {mapped}.");
    }

    private static void VerifyMasking()
    {
        var document = new CaptureDocument(Solid(200, 100, Colors.Red));
        var redaction = new RectangleAnnotation(new Rect(20, 20, 60, 30), Colors.White, 6, true) { RedactionMode = RedactionStyle.Pixelate };
        document.Add(redaction);
        document.Add(new RectangleAnnotation(new Rect(120, 10, 40, 40), Colors.Blue, 4));
        Rect[] hidden = CaptureTextReader.Redactions(document);
        Check(hidden.Length == 1 && hidden[0] == redaction.Bounds, "Only redactions should be masked.");
        BitmapSource masked = CaptureTextReader.RenderMasked(document, hidden);
        Check(masked.PixelWidth == 200 && masked.PixelHeight == 100 && masked.IsFrozen, "Masked image size changed or is not frozen.");
        Color inside = PixelAt(masked, 50, 35), outside = PixelAt(masked, 150, 80), annotation = PixelAt(masked, 140, 30);
        Check(inside is { R: 0, G: 0, B: 0 }, $"Redacted pixels were not masked solid before OCR ({inside}).");
        Check(outside is { R: 255, G: 0, B: 0 } && annotation is { R: 255, G: 0, B: 0 }, "Masking changed pixels outside redactions or drew other markup.");

        var text = new RecognizedText([new TextLine("public secret", [new TextWord("public", new Rect(100, 20, 30, 10)), new TextWord("secret", new Rect(25, 25, 30, 10))])]);
        RecognizedText visible = text.ExcludingRegions(hidden);
        Check(visible.FullText == "public", "Words under a redaction were not removed.");
        string before = CaptureTextReader.IndexStamp(document);
        document.Add(new RectangleAnnotation(new Rect(0, 0, 10, 10), Colors.Black, 4, true));
        Check(CaptureTextReader.IndexStamp(document) != before, "Adding a redaction did not invalidate the text index stamp.");
        string afterRedaction = CaptureTextReader.IndexStamp(document);
        document.Add(new ArrowAnnotation(new Point(1, 1), new Point(50, 50), Colors.Red, 4));
        Check(CaptureTextReader.IndexStamp(document) == afterRedaction, "Ordinary markup invalidated the text index.");
        document.SetCrop(new Rect(10, 10, 100, 50));
        Check(CaptureTextReader.IndexStamp(document) != afterRedaction, "Cropping did not invalidate the text index stamp.");
    }

    private static void VerifyScaling()
    {
        Check(Math.Abs(OcrScale.Factor(5200, 1000, 2600, 64) - .5) < 1e-9, "Large images were not downscaled to the OCR limit.");
        Check(OcrScale.Factor(1920, 1080, 2600, 64) == 1, "Normal captures were resized.");
        Check(OcrScale.Factor(300, 20, 2600, 64) == 2, "Tiny captures were not enlarged (2x at most).");
        Check(OcrScale.Factor(2000, 30, 2600, 64) == 1.3, "Enlarging exceeded the OCR limit.");
        byte[] pixels = [0, 0, 0, 0, 10, 20, 30, 255, 0, 0, 0, 128];
        OcrPixels.FlattenOntoWhite(pixels);
        Check(pixels.SequenceEqual(new byte[] { 255, 255, 255, 255, 10, 20, 30, 255, 127, 127, 127, 255 }), "Transparent pixels were not flattened onto white.");
    }

    private static void VerifyIndex(string root)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "ocr-index.json");
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), third = Guid.NewGuid();
        var index = new OcrTextIndex(path);
        Check(index.Count == 0 && !index.RecoveredFromCorruption, "A missing index did not start empty.");
        index.Set(first, "s1", "Quarterly report\nRevenue 2026");
        index.Set(second, "s2", "Hello BetterShot");
        index.Set(third, "s3", "");
        index.Save();
        Check(File.Exists(path) && Directory.GetFiles(root, "*.tmp").Length == 0, "Index was not written atomically.");
        var reloaded = new OcrTextIndex(path);
        Check(reloaded.Count == 3 && reloaded.TextFor(first) == "Quarterly report\nRevenue 2026" && reloaded.TextFor(third) == "", "Index did not round-trip.");
        Check(reloaded.IsCurrent(second, "s2") && !reloaded.IsCurrent(second, "changed") && !reloaded.IsCurrent(Guid.NewGuid(), "s2"), "Index stamps are not compared.");
        Check(reloaded.Remove(second) && !reloaded.Remove(second), "Index removal failed.");
        Check(reloaded.Prune([first]) == 1, "Pruning did not remove captures that no longer exist.");
        reloaded.Save();
        var afterRemoval = new OcrTextIndex(path);
        Check(afterRemoval.Count == 1 && afterRemoval.TextFor(second) is null && afterRemoval.TextFor(third) is null, "Removed captures came back after reload.");

        File.WriteAllText(path, "{ \"Version\": 1, \"Entries\": { \"not-a-guid\": ");
        var corrupt = new OcrTextIndex(path);
        Check(corrupt.Count == 0 && corrupt.RecoveredFromCorruption, "A corrupt index was not tolerated.");
        corrupt.Set(first, "s1", "again");
        corrupt.Save();
        Check(new OcrTextIndex(path).TextFor(first) == "again", "A recovered index could not be rewritten.");

        File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, Entries = new Dictionary<string, object?> { ["bad"] = new { Stamp = "x", Text = "y" }, [first.ToString()] = new { Stamp = "s1", Text = "kept" }, [second.ToString()] = null } }));
        var partial = new OcrTextIndex(path);
        Check(partial.Count == 1 && partial.TextFor(first) == "kept" && !partial.RecoveredFromCorruption, "Bad entries were not skipped individually.");
        partial.Clear();
        partial.Save();
        Check(!File.Exists(path), "Clearing the index did not delete stored text.");
    }

    private static void VerifySearch()
    {
        string[] fields = ["Hello BetterShot 2026", "3:41 PM", "Quarterly Café report"];
        Check(CaptureSearch.Matches("", fields) && CaptureSearch.Matches("   ", fields), "An empty query hid captures.");
        Check(CaptureSearch.Matches("hello 2026", fields), "Multi-word query across one field failed.");
        Check(CaptureSearch.Matches("bettershot quarterly", fields), "Query terms across fields failed.");
        Check(CaptureSearch.Matches("CAFE", fields), "Search is not case/accent-insensitive.");
        Check(!CaptureSearch.Matches("hello invoice", fields), "A missing term still matched.");
        Check(!CaptureSearch.Matches("hello", [null, " "]), "Empty fields matched.");
        DateTime today = new(2026, 11, 19, 15, 41, 0, DateTimeKind.Local);
        string[] todayLabels = CaptureSearch.DateLabels(today, today).ToArray();
        Check(todayLabels.Contains("Today") && todayLabels.Contains("2026-11-19"), "Date labels are missing Today or ISO dates.");
        Check(CaptureSearch.DateLabels(today.AddDays(-1), today).Contains("Yesterday"), "Yesterday label missing.");
        Check(CaptureSearch.Matches(today.ToString("dddd", CultureInfo.CurrentCulture), todayLabels), "Weekday search failed.");
    }

    private static void VerifySettings()
    {
        Check(CaptureSettings.Default.SearchTextInImages, "Text search should default to on.");
        Check(CaptureSettingsMapper.FromDto(new HistorySettingsDto { RetentionDays = 7 }).SearchTextInImages, "Older settings files should enable text search.");
        Check(!CaptureSettingsMapper.FromDto(new HistorySettingsDto { RetentionDays = 7, SearchTextInImages = false }).SearchTextInImages, "Turning text search off was not loaded.");
        var unknown = new Dictionary<string, JsonElement> { ["futureField"] = JsonDocument.Parse("42").RootElement.Clone() };
        HistorySettingsDto dto = CaptureSettingsMapper.ToDto(CaptureSettings.Default with { SearchTextInImages = false }, unknown);
        string json = JsonSerializer.Serialize(dto);
        Check(dto.SearchTextInImages == false && json.Contains("\"futureField\":42", StringComparison.Ordinal), "Text search setting or unknown fields were not written.");
    }

    private static void VerifyUndoBatch()
    {
        var document = new CaptureDocument(Solid(100, 100, Colors.White));
        document.Add(new ArrowAnnotation(new Point(1, 1), new Point(9, 9), Colors.Red, 4));
        Annotation[] batch = [new RectangleAnnotation(new Rect(1, 1, 5, 5), Colors.Black, 4, true), new RectangleAnnotation(new Rect(20, 20, 5, 5), Colors.Black, 4, true), new RectangleAnnotation(new Rect(40, 40, 5, 5), Colors.Black, 4, true)];
        document.AddRange(batch);
        Check(document.Items.Count == 4, "Batch add lost annotations.");
        document.Undo();
        Check(document.Items.Count == 1 && document.Items[0] is ArrowAnnotation, "Auto-redaction was not a single undo step.");
        document.Redo();
        Check(document.Items.Count == 4, "Auto-redaction redo failed.");
        document.AddRange([]);
        document.Undo();
        Check(document.Items.Count == 1, "An empty batch created an undo step.");
    }

    private static async Task VerifyLibraryUiAsync(string root, string output)
    {
        var store = new CaptureHistoryStore(root);
        store.UpdateSettings(settings => settings with { PlayCaptureSound = false });
        var report = new CaptureDocument(Solid(160, 90, Colors.SteelBlue));
        var notes = new CaptureDocument(Solid(160, 90, Colors.DarkSeaGreen));
        notes.Add(new TextAnnotation(new Point(10, 10), "Invoice draft", Colors.White, 18));
        store.Save(report); store.Save(notes);
        MainWindow? shell = null;
        try
        {
            shell = new MainWindow(enableHistory: true, historyRoot: root);
            Application.Current.MainWindow = shell;
            shell.Show(); shell.SuspendTextIndexingForTest(); await SettleAsync(shell);
            OcrIndexer indexer = shell.TextIndexerForTest ?? throw new InvalidOperationException("The library did not open a text index.");
            Check(shell.SearchTextCheckBox.IsChecked == true, "Search text inside images was not on by default.");
            Check(Path.GetDirectoryName(indexer.Index.Path) == Path.GetFullPath(root), "A test library used the real text index.");
            indexer.Index.Set(report.Id, "test", "Quarterly revenue report");

            shell.SetSearchForTest("quarterly"); await SettleAsync(shell);
            Check(shell.VisibleRecentCountForTest == 1, "Indexed text did not filter the library.");
            shell.SetSearchForTest("invoice"); await SettleAsync(shell);
            Check(shell.VisibleRecentCountForTest == 1, "Typed markup text was not searchable.");
            shell.SetSearchForTest("no such words"); await SettleAsync(shell);
            Check(shell.VisibleRecentCountForTest == 0 && shell.SearchEmptyText.IsVisible && shell.SearchEmptyText.Text.Contains("No captures match", StringComparison.Ordinal), "Empty search state was not shown.");
            Render(shell, Path.Combine(output, "ocr-search-empty.png"));
            shell.SetSearchForTest(""); await SettleAsync(shell);
            Check(shell.VisibleRecentCountForTest == 2 && !shell.SearchEmptyText.IsVisible, "Clearing search did not restore the library.");

            shell.Presentation.RequestDelete(shell.Presentation.Recent.First(document => document.Id == report.Id));
            await SettleAsync(shell);
            Check(indexer.Index.TextFor(report.Id) is null, "Deleting a capture did not remove its indexed text.");

            shell.OpenSettingsForTest(); await SettleAsync(shell);
            shell.SearchTextCheckBox.IsChecked = false; await SettleAsync(shell);
            Check(!new CaptureHistoryStore(root).Settings.SearchTextInImages && shell.TextIndexStatusText.Text == "Text reading is off.", "Turning text reading off was not saved or shown.");
            Check(indexer.Index.Count == 0 && !indexer.Enabled, "Turning text reading off left stored text or kept indexing.");
            shell.StatusText.Text = string.Empty;
            if (((System.Windows.Controls.ScrollViewer)shell.SettingsPanel.Child).Content is FrameworkElement settingsContent) Render(settingsContent, Path.Combine(output, "ocr-settings.png"));
        }
        finally { shell?.CloseForTest(); }
    }

    /// <summary>Renders text, reads it with Windows OCR, and checks words, masking, and auto-redaction. Skips when no OCR language is installed.</summary>
    private static async Task<string> VerifyRealOcrAsync(string output)
    {
        string? unavailable = OcrService.UnavailableReason();
        if (unavailable is not null) return "SKIPPED real OCR: " + unavailable;

        BitmapSource image = RenderText(["Hello BetterShot 2026", "Email jane.doe@example.com", "TOPSECRET WORDS"]);
        SavePng(image, Path.Combine(output, "ocr-sample.png"));
        RecognizedText recognized = await OcrService.RecognizeAsync(image, CancellationToken.None);
        string all = recognized.FullText;
        Check(Contains(all, "Hello") && Contains(all, "BetterShot") && Contains(all, "2026"), $"Real OCR did not read the sample: \"{all}\".");
        TextWord hello = recognized.Lines.SelectMany(line => line.Words).First(word => Contains(word.Text, "Hello"));
        Check(hello.Bounds.Left is > 20 and < 120 && hello.Bounds.Top is > 10 and < 70, $"OCR word boxes are not in image pixels: {hello.Bounds}.");

        var document = new CaptureDocument(image);
        TextLine secretLine = recognized.Lines.First(line => Contains(line.Text, "TOPSECRET"));
        Rect cover = secretLine.Bounds; cover.Inflate(6, 6);
        document.Add(new RectangleAnnotation(cover, Colors.Black, 6, true) { RedactionMode = RedactionStyle.Blur });
        RecognizedText masked = await CaptureTextReader.ReadAsync(document, CancellationToken.None);
        Check(Contains(masked.FullText, "Hello") && !Contains(masked.FullText, "TOPSECRET"), "OCR read text under a redaction.");

        IReadOnlyList<SensitiveMatch> found = SensitiveTextDetector.Find(masked);
        Check(found.Any(match => match.Kind == SensitiveKind.Email), $"Auto-redaction did not find the email in \"{masked.FullText}\".");

        BitmapSource large = RenderText(["Hello BetterShot 2026"], width: 5400, height: 260);
        RecognizedText downscaled = await OcrService.RecognizeAsync(large, CancellationToken.None);
        Check(Contains(downscaled.FullText, "BetterShot"), "OCR failed after downscaling a wide capture.");
        TextWord wide = downscaled.Lines.SelectMany(line => line.Words).First();
        Check(wide.Bounds.Left < 120 && wide.Bounds.Right < 5400, "Downscaled OCR boxes were not mapped back to capture pixels.");
        return "Real OCR read \"" + recognized.Lines[0].Text + "\".";
    }

    private const double LineHeight = 20;
    private static double Column(int index) => index * 80;

    private static TextLine Line(int row, params string[] words)
    {
        TextWord[] boxes = words.Select((word, index) => new TextWord(word, new Rect(Column(index), row * LineHeight, 70, LineHeight * .8))).ToArray();
        return new TextLine(string.Join(' ', words), boxes);
    }

    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static BitmapSource RenderText(string[] lines, int width = 900, int height = 0)
    {
        const double fontSize = 36, lineSpacing = 58;
        if (height <= 0) height = (int)(lines.Length * lineSpacing + 60);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int index = 0; index < lines.Length; index++)
            {
                var formatted = new FormattedText(lines[index], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), fontSize, Brushes.Black, 1);
                context.DrawText(formatted, new Point(40, 30 + index * lineSpacing));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Solid(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4) { pixels[index] = color.B; pixels[index + 1] = color.G; pixels[index + 2] = color.R; pixels[index + 3] = 255; }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color PixelAt(BitmapSource source, int x, int y)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static async Task SettleAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    private static void Render(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        DpiScale dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
