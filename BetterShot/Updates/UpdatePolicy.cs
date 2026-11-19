namespace BetterShot.Updates;

internal enum UpdateAvailability { UpToDate, Available, Skipped, NoReleases }

/// <summary>Pure rules for when to check and whether a release should be offered.</summary>
internal static class UpdatePolicy
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    internal static bool IsCheckDue(DateTime? lastCheckUtc, DateTime utcNow) =>
        lastCheckUtc is not DateTime last || last > utcNow || utcNow - last >= CheckInterval;

    /// <summary>A newer release is offered unless the user skipped that exact version; manual checks still offer skipped versions.</summary>
    internal static UpdateAvailability Evaluate(ReleaseInfo? release, SemanticVersion current, string skippedVersion, bool manualCheck)
    {
        if (release is null) return UpdateAvailability.NoReleases;
        if (!(release.Version > current)) return UpdateAvailability.UpToDate;
        bool skipped = SemanticVersion.TryParse(skippedVersion, out SemanticVersion skippedParsed) && skippedParsed.CompareTo(release.Version) == 0;
        return skipped && !manualCheck ? UpdateAvailability.Skipped : UpdateAvailability.Available;
    }
}
