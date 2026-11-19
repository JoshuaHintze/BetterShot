using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace BetterShot.Updates;

internal sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

internal sealed record ReleaseInfo(SemanticVersion Version, string Tag, string Name, string Notes, string PageUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    internal const string NoReleaseNotes = "No release notes";

    internal string DisplayNotes => string.IsNullOrWhiteSpace(Notes) ? NoReleaseNotes : Notes.Trim();

    /// <summary>The x64 installer built by build-installer.ps1 (BetterShot-x.y.z-Setup-x64.exe).</summary>
    internal ReleaseAsset? Installer => Assets.FirstOrDefault(asset =>
        asset.Name.EndsWith("-Setup-x64.exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>The "&lt;installer&gt;.sha256" checksum file written beside the installer.</summary>
    internal ReleaseAsset? InstallerChecksum => Installer is { } installer
        ? Assets.FirstOrDefault(asset => asset.Name.Equals(installer.Name + ".sha256", StringComparison.OrdinalIgnoreCase))
        : null;
}

/// <summary>
/// Reads the latest published release of JoshuaHintze/BetterShot from the public GitHub REST API (no authentication).
/// Returns null when the repository has no releases yet. Network and HTTP errors are thrown to the caller.
/// </summary>
internal sealed class GitHubReleaseClient
{
    internal const string LatestReleaseUrl = "https://api.github.com/repos/JoshuaHintze/BetterShot/releases/latest";
    internal const string UserAgent = "BetterShot-Updater";
    private readonly HttpClient _client;

    internal GitHubReleaseClient(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    internal static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    internal async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellation)
    {
        using HttpResponseMessage response = await _client.GetAsync(LatestReleaseUrl, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellation);
        return ParseRelease(json);
    }

    /// <summary>Parses a GitHub release object. Returns null for drafts, prereleases, or tags that are not versions.</summary>
    internal static ReleaseInfo? ParseRelease(string json)
    {
        GitHubReleaseDto dto = JsonSerializer.Deserialize<GitHubReleaseDto>(json)
            ?? throw new InvalidDataException("GitHub returned an empty release.");
        if (dto.Draft || dto.Prerelease) return null;
        if (!SemanticVersion.TryParse(dto.TagName, out SemanticVersion version)) return null;
        var assets = (dto.Assets ?? [])
            .Where(asset => !string.IsNullOrWhiteSpace(asset.Name) && UpdateDownloader.IsAllowedAssetUrl(asset.BrowserDownloadUrl))
            .Select(asset => new ReleaseAsset(asset.Name!, asset.BrowserDownloadUrl!, asset.Size))
            .ToArray();
        return new ReleaseInfo(version, dto.TagName ?? string.Empty, string.IsNullOrWhiteSpace(dto.Name) ? $"BetterShot {version}" : dto.Name!,
            dto.Body ?? string.Empty, IsHttps(dto.HtmlUrl) ? dto.HtmlUrl! : string.Empty, assets);
    }

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
