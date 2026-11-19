using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BetterShot.Updates;

/// <summary>
/// Update-check checks with an injected <see cref="HttpMessageHandler"/> (no network): semantic version precedence,
/// GitHub release parsing, skip-version and daily-check rules, ".sha256" sidecar parsing, and verified downloads.
/// </summary>
internal static class UpdateDiagnostics
{
    private const string InstallerName = "BetterShot-0.3.0-Setup-x64.exe";
    private const string InstallerUrl = "https://github.com/JoshuaHintze/BetterShot/releases/download/v0.3.0/" + InstallerName;
    private const string ChecksumUrl = InstallerUrl + ".sha256";
    private const string AssetHostUrl = "https://objects.githubusercontent.com/github-production-release-asset/" + InstallerName;

    internal static async Task<string> RunAsync(string fixtureRoot)
    {
        VerifySemanticVersions();
        VerifyReleaseParsing();
        VerifyPolicy();
        VerifySidecarParsing();
        VerifyAssetUrlPolicy();
        await VerifyClientAndDownloadsAsync(fixtureRoot);
        await VerifyRedirectsAndLaunchCheckAsync(fixtureRoot);
        return "semantic version precedence, release JSON parsing, no-release/draft/prerelease handling, skip-version and daily-check rules, .sha256 sidecar parsing, hash-verified installer download via an injected HTTP handler, BetterShot-release-only asset URLs, GitHub-only redirects, and re-verification before launch";
    }

    private static void VerifySemanticVersions()
    {
        string[] ordered = ["0.1.9", "0.1.10", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "v1.0.1", "2.0.0"];
        for (int index = 1; index < ordered.Length; index++)
        {
            bool parsedLower = SemanticVersion.TryParse(ordered[index - 1], out SemanticVersion lower);
            bool parsedHigher = SemanticVersion.TryParse(ordered[index], out SemanticVersion higher);
            Check(parsedLower && parsedHigher, $"Could not parse {ordered[index - 1]} or {ordered[index]}.");
            Check(lower < higher && higher > lower, $"{ordered[index - 1]} should sort before {ordered[index]}.");
        }
        Check(SemanticVersion.TryParse("v0.2.0+build.7", out SemanticVersion withBuild) && withBuild.CompareTo(new SemanticVersion(0, 2, 0)) == 0, "Build metadata affected precedence.");
        Check(SemanticVersion.TryParse("1.2", out SemanticVersion twoPart) && twoPart.ToString() == "1.2.0", "A two-part version did not parse.");
        Check(!SemanticVersion.TryParse("abc", out _) && !SemanticVersion.TryParse("1", out _) && !SemanticVersion.TryParse("1.2.x", out _) && !SemanticVersion.TryParse("1.0.0-", out _) && !SemanticVersion.TryParse("", out _), "An invalid version parsed.");
        Check(SemanticVersion.FromAssemblyVersion(new Version(0, 1, 4, 0)).ToString() == "0.1.4", "Assembly version conversion changed.");
    }

    private static void VerifyReleaseParsing()
    {
        ReleaseInfo release = GitHubReleaseClient.ParseRelease(ReleaseJson("v0.3.0", "## Fixes\n- Faster capture", draft: false, prerelease: false))
            ?? throw new InvalidOperationException("Update check: a published release was not parsed.");
        Check(release.Version.ToString() == "0.3.0" && release.Tag == "v0.3.0" && release.DisplayNotes.StartsWith("## Fixes", StringComparison.Ordinal), "Release fields were parsed incorrectly.");
        Check(release.Installer?.Name == InstallerName && release.InstallerChecksum?.DownloadUrl == ChecksumUrl, "The installer or checksum asset was not found.");
        Check(release.Assets.All(asset => asset.DownloadUrl.StartsWith("https://", StringComparison.Ordinal)), "A non-HTTPS asset was accepted.");
        Check(release.Assets.All(asset => UpdateDownloader.IsAllowedAssetUrl(asset.DownloadUrl)) && release.Assets.Count == 2, "An asset outside BetterShot's GitHub releases was accepted.");
        ReleaseInfo? empty = GitHubReleaseClient.ParseRelease(ReleaseJson("v0.3.1", "", draft: false, prerelease: false));
        Check(empty?.DisplayNotes == ReleaseInfo.NoReleaseNotes, "Empty release notes did not fall back to \"No release notes\".");
        Check(GitHubReleaseClient.ParseRelease(ReleaseJson("v0.4.0", "x", draft: true, prerelease: false)) is null, "A draft release was offered.");
        Check(GitHubReleaseClient.ParseRelease(ReleaseJson("v0.4.0-beta.1", "x", draft: false, prerelease: true)) is null, "A prerelease was offered.");
        Check(GitHubReleaseClient.ParseRelease(ReleaseJson("nightly", "x", draft: false, prerelease: false)) is null, "A non-version tag was offered.");
    }

    private static void VerifyPolicy()
    {
        ReleaseInfo release = GitHubReleaseClient.ParseRelease(ReleaseJson("v0.3.0", "notes", false, false))!;
        var current = new SemanticVersion(0, 2, 5);
        Check(UpdatePolicy.Evaluate(null, current, "", manualCheck: true) == UpdateAvailability.NoReleases, "No releases was not reported.");
        Check(UpdatePolicy.Evaluate(release, new SemanticVersion(0, 3, 0), "", false) == UpdateAvailability.UpToDate, "The same version was offered.");
        Check(UpdatePolicy.Evaluate(release, new SemanticVersion(0, 4, 0), "", false) == UpdateAvailability.UpToDate, "An older release was offered.");
        Check(UpdatePolicy.Evaluate(release, current, "", false) == UpdateAvailability.Available, "A newer release was not offered.");
        Check(UpdatePolicy.Evaluate(release, current, "0.3.0", manualCheck: false) == UpdateAvailability.Skipped, "A skipped version was offered by a background check.");
        Check(UpdatePolicy.Evaluate(release, current, "0.3.0", manualCheck: true) == UpdateAvailability.Available, "Check for updates hid a skipped version.");
        Check(UpdatePolicy.Evaluate(release, current, "0.2.9", manualCheck: false) == UpdateAvailability.Available, "Skipping an older version hid a newer one.");
        DateTime now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        Check(UpdatePolicy.IsCheckDue(null, now) && UpdatePolicy.IsCheckDue(now.AddDays(-1), now) && !UpdatePolicy.IsCheckDue(now.AddHours(-23), now), "The once-a-day rule is wrong.");
        Check(UpdatePolicy.IsCheckDue(now.AddDays(3), now), "A last-check time in the future (clock change) blocked checks forever.");
    }

    private static void VerifySidecarParsing()
    {
        string hash = new('a', 64);
        Check(UpdateDownloader.ParseSha256Sidecar($"{hash.ToUpperInvariant()}  {InstallerName}\r\n", InstallerName) == hash.ToUpperInvariant(), "build-installer.ps1's sidecar format was not parsed.");
        Check(UpdateDownloader.ParseSha256Sidecar($"{hash} *{InstallerName}\n", InstallerName) == hash.ToUpperInvariant(), "The binary-mode marker was not accepted.");
        Check(UpdateDownloader.ParseSha256Sidecar(hash, InstallerName) == hash.ToUpperInvariant(), "A bare hash was not accepted.");
        Check(UpdateDownloader.ParseSha256Sidecar($"{hash}  BetterShot-0.2.0-Setup-x64.exe", InstallerName) is null, "A checksum for a different file was accepted.");
        Check(UpdateDownloader.ParseSha256Sidecar("not a hash", InstallerName) is null && UpdateDownloader.ParseSha256Sidecar("", InstallerName) is null, "A malformed sidecar was accepted.");
    }

    private static void VerifyAssetUrlPolicy()
    {
        Check(UpdateDownloader.IsAllowedAssetUrl(InstallerUrl) && UpdateDownloader.IsAllowedAssetUrl(ChecksumUrl), "A BetterShot release asset URL was rejected.");
        string[] rejected =
        [
            "http://github.com/JoshuaHintze/BetterShot/releases/download/v0.3.0/" + InstallerName,
            "https://evil.example.com/JoshuaHintze/BetterShot/releases/download/v0.3.0/" + InstallerName,
            "https://github.com.evil.example.com/JoshuaHintze/BetterShot/releases/download/v0.3.0/" + InstallerName,
            "https://objects.githubusercontent.com/JoshuaHintze/BetterShot/releases/download/v0.3.0/" + InstallerName,
            "https://github.com/SomeoneElse/BetterShot/releases/download/v0.3.0/" + InstallerName,
            "https://github.com/JoshuaHintze/BetterShot/archive/v0.3.0.zip",
            "https://github.com/JoshuaHintze/BetterShot/releases/download/../../../SomeoneElse/x/releases/download/" + InstallerName,
            "https://github.com:8443/JoshuaHintze/BetterShot/releases/download/v0.3.0/" + InstallerName,
            "https://user@github.com/JoshuaHintze/BetterShot/releases/download/v0.3.0/" + InstallerName,
            "not a url"
        ];
        foreach (string url in rejected)
            Check(!UpdateDownloader.IsAllowedAssetUrl(url), $"The asset URL {url} was accepted.");
        Check(UpdateDownloader.IsAllowedRedirectTarget(new Uri(AssetHostUrl))
            && UpdateDownloader.IsAllowedRedirectTarget(new Uri("https://release-assets.githubusercontent.com/x"))
            && UpdateDownloader.IsAllowedRedirectTarget(new Uri(InstallerUrl)), "A GitHub release download host was rejected as a redirect target.");
        Check(!UpdateDownloader.IsAllowedRedirectTarget(new Uri("http://objects.githubusercontent.com/x"))
            && !UpdateDownloader.IsAllowedRedirectTarget(new Uri("https://evil.example.com/x"))
            && !UpdateDownloader.IsAllowedRedirectTarget(new Uri("https://api.github.com/x")), "A redirect away from GitHub's download hosts was allowed.");
    }

    private static async Task VerifyClientAndDownloadsAsync(string root)
    {
        byte[] installer = Encoding.ASCII.GetBytes("MZ fake installer payload for diagnostics");
        string goodHash = Convert.ToHexString(SHA256.HashData(installer));
        var handler = new FakeHandler();
        using HttpClient client = GitHubReleaseClient.CreateHttpClient(handler);

        handler.Responses[GitHubReleaseClient.LatestReleaseUrl] = () => Json(ReleaseJson("v0.3.0", "notes", false, false));
        ReleaseInfo release = await new GitHubReleaseClient(client).GetLatestAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("Update check: the latest release was not returned.");
        Check(handler.LastUserAgent == GitHubReleaseClient.UserAgent, "The GitHub request did not send the BetterShot-Updater User-Agent.");
        Check(handler.LastAuthorization is null, "The GitHub request sent credentials.");

        handler.Responses[GitHubReleaseClient.LatestReleaseUrl] = () => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}") };
        Check(await new GitHubReleaseClient(client).GetLatestAsync(CancellationToken.None) is null, "A repository without releases was not reported as no releases.");

        handler.Responses[GitHubReleaseClient.LatestReleaseUrl] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        bool failed = false;
        try { await new GitHubReleaseClient(client).GetLatestAsync(CancellationToken.None); }
        catch (HttpRequestException) { failed = true; }
        Check(failed, "A server error was not surfaced to the caller.");

        handler.Responses[InstallerUrl] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) };
        handler.Responses[ChecksumUrl] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{goodHash}  {InstallerName}\r\n") };
        string goodRoot = Path.Combine(root, "good");
        string path = await new UpdateDownloader(client, goodRoot).DownloadVerifiedInstallerAsync(release, null, CancellationToken.None);
        Check(File.ReadAllBytes(path).SequenceEqual(installer) && Path.GetFileName(path) == InstallerName, "The verified installer was not saved.");
        Check(Directory.GetFiles(goodRoot).Length == 1, "Temporary download files were left behind.");

        handler.Responses[ChecksumUrl] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{new string('0', 64)}  {InstallerName}\r\n") };
        string badRoot = Path.Combine(root, "bad");
        bool rejected = false;
        try { await new UpdateDownloader(client, badRoot).DownloadVerifiedInstallerAsync(release, null, CancellationToken.None); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "An installer with the wrong hash was accepted.");
        Check(!Directory.Exists(badRoot) || Directory.GetFiles(badRoot).Length == 0, "A rejected installer was left on disk.");
    }

    private static async Task VerifyRedirectsAndLaunchCheckAsync(string root)
    {
        // Not a PE file, so the launch check exercises the unsigned path (hash still required).
        byte[] installer = Encoding.ASCII.GetBytes("fake installer payload for redirect diagnostics");
        string goodHash = Convert.ToHexString(SHA256.HashData(installer));
        var handler = new FakeHandler();
        using HttpClient client = GitHubReleaseClient.CreateHttpClient(handler);
        ReleaseInfo release = GitHubReleaseClient.ParseRelease(ReleaseJson("v0.3.0", "notes", false, false))!;
        handler.Responses[ChecksumUrl] = () => Redirect(AssetHostUrl + ".sha256");
        handler.Responses[AssetHostUrl + ".sha256"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{goodHash}  {InstallerName}\r\n") };
        handler.Responses[InstallerUrl] = () => Redirect(AssetHostUrl);
        handler.Responses[AssetHostUrl] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) };

        string redirectRoot = Path.Combine(root, "redirect");
        var downloader = new UpdateDownloader(client, redirectRoot);
        string path = await downloader.DownloadVerifiedInstallerAsync(release, null, CancellationToken.None);
        Check(File.ReadAllBytes(path).SequenceEqual(installer), "A redirect to GitHub's asset host was not followed.");

        using (FileStream verified = downloader.OpenVerifiedInstallerForLaunch(path))
        {
            Check(verified.Name == Path.GetFullPath(path), "The launch check returned a different file.");
            bool blockedWrite = false;
            try { using FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException) { blockedWrite = true; }
            catch (UnauthorizedAccessException) { blockedWrite = true; }
            Check(blockedWrite, "The installer could be replaced while it was being launched.");
        }
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("swapped installer"));
        Check(await ThrowsInvalidDataAsync(() => Task.FromResult(downloader.OpenVerifiedInstallerForLaunch(path))), "An installer changed after verification would have been started.");
        string stranger = Path.Combine(redirectRoot, "unverified.exe");
        File.WriteAllBytes(stranger, installer);
        Check(await ThrowsInvalidDataAsync(() => Task.FromResult(downloader.OpenVerifiedInstallerForLaunch(stranger))), "A file BetterShot never verified would have been started.");

        handler.Responses[InstallerUrl] = () => Redirect("https://evil.example.com/" + InstallerName);
        handler.Responses["https://evil.example.com/" + InstallerName] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) };
        string offHostRoot = Path.Combine(root, "redirect-off-host");
        Check(await ThrowsInvalidDataAsync(() => new UpdateDownloader(client, offHostRoot).DownloadVerifiedInstallerAsync(release, null, CancellationToken.None)), "A redirect away from GitHub was followed.");
        Check(!handler.Requested.Contains("https://evil.example.com/" + InstallerName), "The off-GitHub redirect target was requested.");
        Check(!Directory.Exists(offHostRoot) || Directory.GetFiles(offHostRoot).Length == 0, "A download redirected away from GitHub left a file on disk.");

        handler.Responses[InstallerUrl] = () => Redirect(AssetHostUrl.Replace("https://", "http://", StringComparison.Ordinal));
        Check(await ThrowsInvalidDataAsync(() => new UpdateDownloader(client, offHostRoot).DownloadVerifiedInstallerAsync(release, null, CancellationToken.None)), "A redirect to plain HTTP was followed.");

        handler.Responses[InstallerUrl] = () => Redirect(InstallerUrl);
        bool loopStopped = false;
        try { await new UpdateDownloader(client, offHostRoot).DownloadVerifiedInstallerAsync(release, null, CancellationToken.None); }
        catch (HttpRequestException) { loopStopped = true; }
        Check(loopStopped, "A redirect loop was not stopped.");

        var foreignAssets = new ReleaseInfo(release.Version, release.Tag, release.Name, release.Notes, release.PageUrl,
            [new ReleaseAsset(InstallerName, "https://evil.example.com/" + InstallerName, 1), new ReleaseAsset(InstallerName + ".sha256", ChecksumUrl, 1)]);
        handler.Requested.Clear();
        Check(await ThrowsInvalidDataAsync(() => new UpdateDownloader(client, offHostRoot).DownloadVerifiedInstallerAsync(foreignAssets, null, CancellationToken.None)), "An installer outside BetterShot's GitHub releases was downloaded.");
        Check(handler.Requested.Count == 0, "A rejected release asset was still requested.");
    }

    private static async Task<bool> ThrowsInvalidDataAsync<T>(Func<Task<T>> action)
    {
        try
        {
            T result = await action();
            (result as IDisposable)?.Dispose();
            return false;
        }
        catch (InvalidDataException) { return true; }
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string ReleaseJson(string tag, string body, bool draft, bool prerelease) => $$"""
        {
          "tag_name": "{{tag}}",
          "name": "BetterShot {{tag}}",
          "body": {{System.Text.Json.JsonSerializer.Serialize(body)}},
          "html_url": "https://github.com/JoshuaHintze/BetterShot/releases/tag/{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [
            { "name": "{{InstallerName}}", "browser_download_url": "{{InstallerUrl}}", "size": 41 },
            { "name": "{{InstallerName}}.sha256", "browser_download_url": "{{ChecksumUrl}}", "size": 100 },
            { "name": "insecure.zip", "browser_download_url": "http://example.com/insecure.zip", "size": 1 },
            { "name": "mirror.zip", "browser_download_url": "https://example.com/JoshuaHintze/BetterShot/releases/download/v0.3.0/mirror.zip", "size": 1 }
          ]
        }
        """;

    private sealed class FakeHandler : HttpMessageHandler
    {
        internal Dictionary<string, Func<HttpResponseMessage>> Responses { get; } = new(StringComparer.Ordinal);
        internal string? LastUserAgent { get; private set; }
        internal string? LastAuthorization { get; private set; }
        internal List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.ToString() ?? string.Empty;
            LastUserAgent = request.Headers.UserAgent.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            Requested.Add(url);
            HttpResponseMessage response = Responses.TryGetValue(url, out Func<HttpResponseMessage>? create)
                ? create()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Update check: " + message);
    }
}
