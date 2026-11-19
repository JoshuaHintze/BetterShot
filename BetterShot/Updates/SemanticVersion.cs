using System.Globalization;

namespace BetterShot.Updates;

/// <summary>Semantic version (major.minor.patch[-prerelease][+build]) with SemVer 2.0 precedence. A leading "v" is accepted.</summary>
internal sealed record SemanticVersion(int Major, int Minor, int Patch, string Prerelease = "") : IComparable<SemanticVersion>
{
    internal bool IsPrerelease => Prerelease.Length > 0;

    internal static bool TryParse(string? text, out SemanticVersion version)
    {
        version = new SemanticVersion(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        int build = value.IndexOf('+');
        if (build >= 0) value = value[..build];
        string prerelease = string.Empty;
        int dash = value.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..];
            value = value[..dash];
            if (prerelease.Length == 0 || prerelease.Split('.').Any(identifier => identifier.Length == 0)) return false;
        }
        string[] parts = value.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        var numbers = new int[3];
        for (int index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out int number)) return false;
            if (index < 3) numbers[index] = number;
        }
        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    internal static SemanticVersion FromAssemblyVersion(Version? version) =>
        version is null ? new SemanticVersion(0, 0, 0) : new SemanticVersion(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build));

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        int core = Major != other.Major ? Major.CompareTo(other.Major)
            : Minor != other.Minor ? Minor.CompareTo(other.Minor)
            : Patch.CompareTo(other.Patch);
        if (core != 0) return core;
        if (!IsPrerelease || !other.IsPrerelease) return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    private static int ComparePrerelease(string first, string second)
    {
        string[] left = first.Split('.'), right = second.Split('.');
        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            bool leftNumeric = int.TryParse(left[index], NumberStyles.None, CultureInfo.InvariantCulture, out int leftNumber);
            bool rightNumeric = int.TryParse(right[index], NumberStyles.None, CultureInfo.InvariantCulture, out int rightNumber);
            int result = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[index], right[index])
            };
            if (result != 0) return result;
        }
        return left.Length.CompareTo(right.Length);
    }

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public override string ToString() =>
        IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";
}
