using BetterShot.Editing;

namespace BetterShot.Ocr;

/// <summary>One recognized word and its bounding box in original capture pixels.</summary>
internal sealed record TextWord(string Text, Rect Bounds);

/// <summary>A recognized line in reading order. <see cref="Text"/> is the engine's own line text.</summary>
internal sealed record TextLine(string Text, IReadOnlyList<TextWord> Words)
{
    internal Rect Bounds
    {
        get
        {
            Rect union = Rect.Empty;
            foreach (TextWord word in Words) union.Union(word.Bounds);
            return union;
        }
    }
}

/// <summary>
/// On-device OCR output for a capture. Coordinates are original capture pixels, so annotation
/// geometry (crop, redactions, selections mapped back from the editor) can be compared directly.
/// </summary>
internal sealed record RecognizedText(IReadOnlyList<TextLine> Lines)
{
    internal static RecognizedText Empty { get; } = new([]);

    internal bool IsEmpty => Lines.All(line => line.Words.Count == 0 || string.IsNullOrWhiteSpace(line.Text));

    /// <summary>All text, one recognized line per output line.</summary>
    internal string FullText => string.Join(Environment.NewLine, Lines.Where(line => line.Words.Count > 0).Select(line => line.Text.Trim()).Where(text => text.Length > 0));

    /// <summary>
    /// Text of words whose boxes intersect <paramref name="region"/>. Words keep their line grouping, so a
    /// selection across several lines copies with line breaks in reading order.
    /// </summary>
    internal string TextInside(Rect region)
    {
        if (region.IsEmpty) return string.Empty;
        var lines = new List<string>();
        foreach (TextLine line in Lines)
        {
            TextWord[] selected = line.Words.Where(word => Intersects(word.Bounds, region)).ToArray();
            if (selected.Length == 0) continue;
            string text = selected.Length == line.Words.Count ? line.Text.Trim() : JoinWords(selected);
            if (text.Length > 0) lines.Add(text);
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Keeps only words inside the visible composition (for example the crop rectangle).</summary>
    internal RecognizedText Within(Rect visible) => Filter(word => Intersects(word.Bounds, visible));

    /// <summary>Drops words that sit mostly under any of the given regions (redactions), so hidden text is never returned.</summary>
    internal RecognizedText ExcludingRegions(IReadOnlyCollection<Rect> hidden)
    {
        if (hidden.Count == 0) return this;
        return Filter(word => !hidden.Any(region => CoveredFraction(word.Bounds, region) >= .3));
    }

    /// <summary>Maps boxes from a scaled OCR image back to capture pixels.</summary>
    internal RecognizedText Scaled(double factor)
    {
        if (factor <= 0 || !double.IsFinite(factor) || Math.Abs(factor - 1) < 1e-9) return this;
        return new RecognizedText(Lines.Select(line => line with
        {
            Words = line.Words.Select(word => word with
            {
                Bounds = new Rect(word.Bounds.X * factor, word.Bounds.Y * factor, word.Bounds.Width * factor, word.Bounds.Height * factor)
            }).ToArray()
        }).ToArray());
    }

    private RecognizedText Filter(Func<TextWord, bool> keep)
    {
        var lines = new List<TextLine>();
        foreach (TextLine line in Lines)
        {
            TextWord[] kept = line.Words.Where(keep).ToArray();
            if (kept.Length == 0) continue;
            lines.Add(kept.Length == line.Words.Count ? line : new TextLine(JoinWords(kept), kept));
        }
        return new RecognizedText(lines);
    }

    internal static bool Intersects(Rect first, Rect second)
    {
        if (first.IsEmpty || second.IsEmpty) return false;
        Rect overlap = Rect.Intersect(first, second);
        return !overlap.IsEmpty && overlap.Width > 0 && overlap.Height > 0;
    }

    private static double CoveredFraction(Rect word, Rect region)
    {
        if (word.IsEmpty || word.Width <= 0 || word.Height <= 0) return 0;
        Rect overlap = Rect.Intersect(word, region);
        if (overlap.IsEmpty) return 0;
        return overlap.Width * overlap.Height / (word.Width * word.Height);
    }

    /// <summary>Joins words with spaces, except between CJK characters, which Windows OCR reports without separators.</summary>
    internal static string JoinWords(IReadOnlyList<TextWord> words)
    {
        var builder = new System.Text.StringBuilder();
        foreach (TextWord word in words)
        {
            string text = word.Text.Trim();
            if (text.Length == 0) continue;
            if (builder.Length > 0 && !(IsCjk(builder[^1]) && IsCjk(text[0]))) builder.Append(' ');
            builder.Append(text);
        }
        return builder.ToString();
    }

    private static bool IsCjk(char character) =>
        character is >= '぀' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿';
}

/// <summary>Converts editor display coordinates to original capture pixels, accounting for zoom, background padding, and crop.</summary>
internal static class OcrRegionMapper
{
    /// <param name="display">Rectangle in image-control coordinates (DIPs).</param>
    /// <param name="displayScale">Displayed DIPs per output pixel (zoom).</param>
    internal static Rect DisplayToSource(Rect display, double displayScale, CompositionLayout layout)
    {
        if (display.IsEmpty || displayScale <= 0 || !double.IsFinite(displayScale)) return Rect.Empty;
        var output = new Rect(display.X / displayScale, display.Y / displayScale, display.Width / displayScale, display.Height / displayScale);
        output.Offset(-layout.Offset.X, -layout.Offset.Y);
        return Rect.Intersect(output, layout.Content);
    }

    /// <summary>Inverse of <see cref="DisplayToSource"/> for drawing word hints over the preview.</summary>
    internal static Rect SourceToDisplay(Rect source, double displayScale, CompositionLayout layout)
    {
        if (source.IsEmpty) return Rect.Empty;
        Rect output = source;
        output.Offset(layout.Offset.X, layout.Offset.Y);
        return new Rect(output.X * displayScale, output.Y * displayScale, output.Width * displayScale, output.Height * displayScale);
    }
}
