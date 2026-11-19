using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterShot.Ocr;

internal enum SensitiveKind { Email, Phone, CardNumber, Key, IpAddress }

/// <summary>A detected item and the union of the word boxes it spans, in capture pixels.</summary>
internal sealed record SensitiveMatch(SensitiveKind Kind, string Text, Rect Bounds);

/// <summary>A match inside a single line of text, as a character range.</summary>
internal readonly record struct SensitiveSpan(SensitiveKind Kind, int Start, int Length)
{
    internal int End => Start + Length;
}

/// <summary>
/// Finds emails, phone numbers, card numbers (Luhn-checked), API keys/tokens, and IP addresses in OCR
/// output so they can be covered automatically. Works line by line, because OCR splits numbers such as
/// "4111 1111 1111 1111" or "(555) 123-4567" into several words.
/// </summary>
internal static partial class SensitiveTextDetector
{
    private const double MinimumSecretEntropy = 3.3;
    private const int MinimumSecretLength = 20;

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![\w.+]|\d[\s.\-])(?:\+\d{1,3}[\s.\-]?)?(?:\(\d{2,4}\)[\s.\-]?|\d{2,4}[\s.\-])\d{3,4}[\s.\-]\d{3,4}(?![\w]|[\s.\-]\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"(?<![\d\-])(?:\d[ \-]?){12,18}\d(?![\d\-])", RegexOptions.CultureInvariant)]
    private static partial Regex CardPattern();

    [GeneratedRegex(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?!\d|\.\d)", RegexOptions.CultureInvariant)]
    private static partial Regex IPv4Pattern();

    [GeneratedRegex(@"(?<![:\w])[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7}(?![:\w])", RegexOptions.CultureInvariant)]
    private static partial Regex IPv6Pattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:(?:sk|pk|rk)[-_](?:live[-_]|test[-_]|proj[-_]|ant[-_][A-Za-z0-9]+[-_])?[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abposr]-[A-Za-z0-9\-]{10,}|(?:AKIA|ASIA)[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{30,}|glpat-[A-Za-z0-9_\-]{20,}|eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,})", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"[^\s""'`()\[\]{}<>,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    /// <summary>Detected items across all lines. Overlapping matches in a line are merged into one region.</summary>
    internal static IReadOnlyList<SensitiveMatch> Find(RecognizedText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var matches = new List<SensitiveMatch>();
        foreach (TextLine line in text.Lines) matches.AddRange(FindInLine(line));
        return matches;
    }

    /// <summary>Covers each match with padding proportional to the text height, clipped to the capture.</summary>
    internal static Rect PaddedBounds(Rect bounds, Rect canvas)
    {
        double padding = Math.Max(2, Math.Round(bounds.Height * .18));
        Rect padded = bounds;
        padded.Inflate(padding, padding);
        Rect clipped = Rect.Intersect(padded, canvas);
        return clipped.IsEmpty ? Rect.Empty : clipped;
    }

    private static IEnumerable<SensitiveMatch> FindInLine(TextLine line)
    {
        var builder = new StringBuilder();
        var ranges = new List<(int Start, int End)>();
        foreach (TextWord word in line.Words)
        {
            if (builder.Length > 0) builder.Append(' ');
            int start = builder.Length;
            builder.Append(word.Text.Trim());
            ranges.Add((start, builder.Length));
        }
        IReadOnlyList<SensitiveSpan> spans = FindSpans(builder.ToString());
        var groups = new List<(SensitiveKind Kind, HashSet<int> Words)>();
        foreach (SensitiveSpan span in spans)
        {
            var words = new HashSet<int>(Enumerable.Range(0, ranges.Count).Where(index => ranges[index].Start < span.End && span.Start < ranges[index].End));
            if (words.Count == 0) continue;
            var overlapping = groups.Where(group => group.Words.Overlaps(words)).ToList();
            foreach (var group in overlapping) { words.UnionWith(group.Words); groups.Remove(group); }
            groups.Add((overlapping.Count > 0 ? overlapping[0].Kind : span.Kind, words));
        }
        foreach ((SensitiveKind kind, HashSet<int> words) in groups)
        {
            int[] ordered = words.Order().ToArray();
            Rect bounds = Rect.Empty;
            foreach (int index in ordered) bounds.Union(line.Words[index].Bounds);
            yield return new SensitiveMatch(kind, RecognizedText.JoinWords(ordered.Select(index => line.Words[index]).ToArray()), bounds);
        }
    }

    /// <summary>Character ranges of sensitive items in one line of text.</summary>
    internal static IReadOnlyList<SensitiveSpan> FindSpans(string line)
    {
        var spans = new List<SensitiveSpan>();
        if (string.IsNullOrWhiteSpace(line)) return spans;
        AddMatches(spans, EmailPattern(), line, SensitiveKind.Email, null);
        AddMatches(spans, KeyPattern(), line, SensitiveKind.Key, null);
        AddMatches(spans, IPv4Pattern(), line, SensitiveKind.IpAddress, null);
        AddMatches(spans, IPv6Pattern(), line, SensitiveKind.IpAddress, IsIPv6);
        AddMatches(spans, CardPattern(), line, SensitiveKind.CardNumber, IsCardNumber);
        AddMatches(spans, PhonePattern(), line, SensitiveKind.Phone, IsPhoneNumber);
        AddMatches(spans, TokenPattern(), line, SensitiveKind.Key, value => LooksLikeSecret(TrimPunctuation(value)));
        return spans.OrderBy(span => span.Start).ToArray();
    }

    private static void AddMatches(List<SensitiveSpan> spans, Regex pattern, string line, SensitiveKind kind, Func<string, bool>? accept)
    {
        foreach (Match match in pattern.Matches(line))
        {
            if (match.Length == 0 || (accept is not null && !accept(match.Value))) continue;
            spans.Add(new SensitiveSpan(kind, match.Index, match.Length));
        }
    }

    internal static bool PassesLuhn(string digits)
    {
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return false;
        int sum = 0;
        bool doubleDigit = false;
        for (int index = digits.Length - 1; index >= 0; index--)
        {
            int digit = digits[index] - '0';
            if (doubleDigit) { digit *= 2; if (digit > 9) digit -= 9; }
            sum += digit;
            doubleDigit = !doubleDigit;
        }
        return sum % 10 == 0;
    }

    private static bool IsCardNumber(string value)
    {
        string digits = new(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length is >= 13 and <= 19 && digits.Distinct().Count() > 1 && PassesLuhn(digits);
    }

    private static bool IsPhoneNumber(string value)
    {
        int digits = value.Count(char.IsAsciiDigit);
        return digits is >= 9 and <= 15;
    }

    private static bool IsIPv6(string value) =>
        value.Count(character => character == ':') >= 2 &&
        value.Any(char.IsAsciiHexDigit) &&
        IPAddress.TryParse(value, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>
    /// Long, random-looking tokens: at least 20 characters mixing letters and digits with many switches
    /// between them and high character entropy. Ordinary words, dates, and file names stay unmatched.
    /// </summary>
    internal static bool LooksLikeSecret(string token)
    {
        if (token.Length < MinimumSecretLength || token.Contains('@') || token.Contains("://", StringComparison.Ordinal)) return false;
        if (!token.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '+' or '/' or '=' or '.')) return false;
        if (token.Count(character => character == '.') > 2) return false;
        int letters = token.Count(char.IsAsciiLetter), digits = token.Count(char.IsAsciiDigit);
        if (letters < 4 || digits < 2) return false;
        return ClassSwitches(token) >= 4 && Entropy(token) >= MinimumSecretEntropy;
    }

    private static int ClassSwitches(string token)
    {
        int switches = 0;
        bool? previousIsDigit = null;
        foreach (char character in token)
        {
            if (!char.IsAsciiLetterOrDigit(character)) continue;
            bool isDigit = char.IsAsciiDigit(character);
            if (previousIsDigit is bool previous && previous != isDigit) switches++;
            previousIsDigit = isDigit;
        }
        return switches;
    }

    private static double Entropy(string token)
    {
        double entropy = 0;
        foreach (IGrouping<char, char> group in token.GroupBy(character => character))
        {
            double probability = (double)group.Count() / token.Length;
            entropy -= probability * Math.Log2(probability);
        }
        return entropy;
    }

    private static string TrimPunctuation(string value) => value.Trim('.', ',', ';', ':', '!', '?', '"', '\'', ')', '(');
}
