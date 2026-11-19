using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace BetterShot.Ocr;

/// <summary>Library search: every word of the query must appear in the capture's name/date labels or its text.</summary>
internal static class CaptureSearch
{
    private const CompareOptions Comparison = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth;

    internal static bool IsEmptyQuery(string? query) => string.IsNullOrWhiteSpace(query);

    internal static bool Matches(string? query, IEnumerable<string?> searchable)
    {
        if (IsEmptyQuery(query)) return true;
        string[] fields = searchable.Where(field => !string.IsNullOrWhiteSpace(field)).Select(field => field!).ToArray();
        if (fields.Length == 0) return false;
        CompareInfo compare = CultureInfo.CurrentCulture.CompareInfo;
        return query!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(term => fields.Any(field => compare.IndexOf(field, term, Comparison) >= 0));
    }

    /// <summary>Labels a capture can be found by: the times and dates shown in the library, in several common forms.</summary>
    internal static IEnumerable<string> DateLabels(DateTime local, DateTime today)
    {
        yield return local.ToString("h:mm tt", CultureInfo.CurrentCulture);
        yield return local.ToString("t", CultureInfo.CurrentCulture);
        yield return local.ToString("d", CultureInfo.CurrentCulture);
        yield return local.ToString("D", CultureInfo.CurrentCulture);
        yield return local.ToString("MMM d yyyy", CultureInfo.CurrentCulture);
        yield return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        yield return local.ToString("dddd", CultureInfo.CurrentCulture);
        if (local.Date == today.Date) yield return "Today";
        else if (local.Date == today.Date.AddDays(-1)) yield return "Yesterday";
    }
}

/// <summary>Writes text to the clipboard, retrying briefly while another app holds it open.</summary>
internal static class ClipboardText
{
    /// <summary>Returns null on success, otherwise a short reason.</summary>
    internal static async Task<string?> SetAsync(string text, CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try { Clipboard.SetText(text, TextDataFormat.UnicodeText); return null; }
            catch (COMException) when (attempt < 5) { await Task.Delay(attempt * 60, cancellationToken); }
            catch (COMException) { return "the clipboard is busy. Please try again."; }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException) { return ex.Message; }
        }
        return "the clipboard is busy. Please try again.";
    }
}
