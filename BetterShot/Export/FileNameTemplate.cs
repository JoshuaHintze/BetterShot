using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterShot.Export;

/// <summary>Expands capture file-name patterns and makes the result safe for Windows file systems.</summary>
internal static partial class FileNameTemplate
{
    internal const string DefaultPattern = "BetterShot %date at %time";
    internal const string FallbackName = "BetterShot";
    internal const int MaximumStemLength = 180;
    internal static readonly string[] Tokens = ["%date", "%time", "%year", "%month", "%day", "%hour", "%minute", "%second", "%counter"];

    private static readonly char[] InvalidWindowsCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    [GeneratedRegex("%(date|time|year|month|day|hour|minute|second|counter)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    internal static bool UsesCounter(string? pattern) =>
        !string.IsNullOrEmpty(pattern) && pattern.Contains("%counter", StringComparison.OrdinalIgnoreCase);

    /// <summary>Replaces supported tokens. Unknown % sequences are kept literally.</summary>
    internal static string Expand(string? pattern, DateTime localTime, int counter)
    {
        string source = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern;
        return TokenPattern().Replace(source, match => TokenValue(match.Groups[1].Value, localTime, counter));
    }

    private static string TokenValue(string token, DateTime time, int counter) => token.ToLowerInvariant() switch
    {
        "date" => time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "time" => time.ToString("HH.mm.ss", CultureInfo.InvariantCulture),
        "year" => time.ToString("yyyy", CultureInfo.InvariantCulture),
        "month" => time.ToString("MM", CultureInfo.InvariantCulture),
        "day" => time.ToString("dd", CultureInfo.InvariantCulture),
        "hour" => time.ToString("HH", CultureInfo.InvariantCulture),
        "minute" => time.ToString("mm", CultureInfo.InvariantCulture),
        "second" => time.ToString("ss", CultureInfo.InvariantCulture),
        "counter" => Math.Max(1, counter).ToString(CultureInfo.InvariantCulture),
        _ => "%" + token
    };

    /// <summary>Produces a Windows-safe file name stem (no extension, no folders).</summary>
    internal static string Sanitize(string? name)
    {
        var builder = new StringBuilder((name ?? string.Empty).Length);
        foreach (char character in name ?? string.Empty)
            builder.Append(character < 32 || Array.IndexOf(InvalidWindowsCharacters, character) >= 0 ? '-' : character);

        string stem = builder.ToString().Trim().TrimEnd('.', ' ');
        if (stem.Length > MaximumStemLength) stem = stem[..MaximumStemLength].TrimEnd('.', ' ');
        if (stem.Length == 0 || stem.All(character => character is '.' or '-' or ' ')) return FallbackName;

        string deviceCandidate = stem.Split('.')[0].TrimEnd(' ');
        return ReservedDeviceNames.Contains(deviceCandidate) ? "_" + stem : stem;
    }

    internal static string BuildFileName(string? pattern, DateTime localTime, int counter, string extension) =>
        Sanitize(Expand(pattern, localTime, counter)) + extension;

    /// <summary>Returns folder\name.ext, or folder\name (2).ext, (3)… for the first path that does not exist.</summary>
    internal static string ResolveUniquePath(string folder, string fileName, Func<string, bool> exists)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string candidate = Path.Combine(folder, fileName);
        for (int suffix = 2; exists(candidate); suffix++)
        {
            if (suffix > 9999) throw new IOException("Too many files already use this name.");
            candidate = Path.Combine(folder, $"{stem} ({suffix}){extension}");
        }
        return candidate;
    }
}
