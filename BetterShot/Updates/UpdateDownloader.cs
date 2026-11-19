using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;

namespace BetterShot.Updates;

/// <summary>
/// Downloads the release installer and its ".sha256" sidecar into %LOCALAPPDATA%\BetterShot\Updates, verifies the hash,
/// and returns the verified path. Nothing is executed here; launching the installer is a separate, user-confirmed step.
/// Only BetterShot release-asset URLs on github.com are requested, and redirects are followed manually (the client must
/// not follow them itself) and only to GitHub's release download hosts, always over HTTPS.
/// </summary>
internal sealed class UpdateDownloader
{
    internal const string ReleaseDownloadPathPrefix = "/JoshuaHintze/BetterShot/releases/download/";
    internal const int MaximumRedirects = 5;
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private const int MaximumChecksumBytes = 4096;
    private static readonly string[] AllowedDownloadHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];
    private readonly HttpClient _client;
    private readonly string _directory;
    private readonly Dictionary<string, string> _verifiedHashes = new(StringComparer.OrdinalIgnoreCase);

    internal UpdateDownloader(HttpClient client, string? directory = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _directory = directory ?? DefaultDirectory;
    }

    internal static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterShot", "Updates");

    /// <summary>The download client: redirects are never followed automatically so every hop can be checked.</summary>
    internal static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(GitHubReleaseClient.UserAgent);
        return client;
    }

    /// <summary>True only for https://github.com/JoshuaHintze/BetterShot/releases/download/… (installer and checksum assets).</summary>
    internal static bool IsAllowedAssetUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && IsHttpsOnDefaultPort(uri)
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith(ReleaseDownloadPathPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for an HTTPS redirect target on GitHub's release download hosts.</summary>
    internal static bool IsAllowedRedirectTarget(Uri uri) =>
        IsHttpsOnDefaultPort(uri)
        && AllowedDownloadHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase));

    private static bool IsHttpsOnDefaultPort(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

    internal async Task<string> DownloadVerifiedInstallerAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(release);
        ReleaseAsset installer = release.Installer ?? throw new InvalidOperationException("This release does not include a Windows installer.");
        ReleaseAsset checksumAsset = release.InstallerChecksum ?? throw new InvalidOperationException("This release does not include an installer checksum, so it cannot be verified.");
        string fileName = Path.GetFileName(installer.Name);
        if (fileName != installer.Name || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("The installer name in the release is not a plain file name.");
        if (!IsAllowedAssetUrl(installer.DownloadUrl) || !IsAllowedAssetUrl(checksumAsset.DownloadUrl))
            throw new InvalidDataException("The release download is not hosted in BetterShot's GitHub releases, so it was not downloaded.");

        string checksumText = await DownloadChecksumAsync(checksumAsset.DownloadUrl, cancellation);
        string expected = ParseSha256Sidecar(checksumText, fileName)
            ?? throw new InvalidDataException("The installer checksum file is not in the expected format.");

        Directory.CreateDirectory(_directory);
        string destination = Path.Combine(_directory, fileName);
        string temporary = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            await DownloadFileAsync(installer.DownloadUrl, temporary, installer.Size, progress, cancellation);
            string actual;
            await using (FileStream written = File.OpenRead(temporary)) actual = Convert.ToHexString(await SHA256.HashDataAsync(written, cancellation));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded installer did not match its published checksum, so it was deleted.");
            File.Move(temporary, destination, overwrite: true);
            _verifiedHashes[Path.GetFullPath(destination)] = expected;
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Re-hashes an installer this downloader verified, right before it is launched (the file may have been replaced while
    /// the user decided), and checks its Authenticode signature when it has one. The returned stream keeps the file open
    /// without write or delete sharing, so dispose it only after the installer process has started.
    /// Unsigned installers are allowed (current releases are unsigned); the published hash is always required.
    /// </summary>
    internal FileStream OpenVerifiedInstallerForLaunch(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!_verifiedHashes.TryGetValue(fullPath, out string? expected))
            throw new InvalidDataException("This installer was not downloaded and verified by BetterShot.");
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded installer changed after it was verified, so it was not started.");
            if (AuthenticodeVerifier.Check(stream.SafeFileHandle, fullPath) == AuthenticodeResult.Invalid)
                throw new InvalidDataException("The installer's digital signature is not valid, so it was not started.");
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Parses build-installer.ps1's sidecar ("HEX␠␠file-name", CRLF) or a bare hash. When a file name is present it must
    /// match <paramref name="expectedFileName"/> (a leading '*' binary marker is allowed). Returns the upper-case hash or null.
    /// </summary>
    internal static string? ParseSha256Sidecar(string text, string expectedFileName)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;
            string[] parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            string hash = parts[0];
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) continue;
            if (parts.Length == 2)
            {
                string name = parts[1].Trim().TrimStart('*');
                if (!name.Equals(expectedFileName, StringComparison.OrdinalIgnoreCase)) continue;
            }
            return hash.ToUpper(CultureInfo.InvariantCulture);
        }
        return null;
    }

    /// <summary>
    /// GETs an allowed release-asset URL, following at most <see cref="MaximumRedirects"/> redirects, each of which must stay
    /// on HTTPS and on GitHub's release download hosts. The caller disposes the returned successful response.
    /// </summary>
    internal async Task<HttpResponseMessage> GetFollowingAllowedRedirectsAsync(string url, CancellationToken cancellation)
    {
        if (!IsAllowedAssetUrl(url)) throw new InvalidDataException("The release download is not hosted in BetterShot's GitHub releases, so it was not downloaded.");
        var current = new Uri(url);
        for (int redirects = 0; ; redirects++)
        {
            HttpResponseMessage response = await _client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!IsRedirect(response.StatusCode))
            {
                try { response.EnsureSuccessStatusCode(); }
                catch { response.Dispose(); throw; }
                return response;
            }
            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new HttpRequestException("The release download redirected without a destination.");
            if (redirects >= MaximumRedirects) throw new HttpRequestException("The release download redirected too many times.");
            Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (!IsAllowedRedirectTarget(next))
                throw new InvalidDataException("The release download redirected away from GitHub, so it was not downloaded.");
            current = next;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private async Task<string> DownloadChecksumAsync(string url, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await GetFollowingAllowedRedirectsAsync(url, cancellation);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellation);
        var buffer = new byte[MaximumChecksumBytes];
        int total = 0, read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellation)) > 0) total += read;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    private async Task DownloadFileAsync(string url, string path, long expectedSize, IProgress<double>? progress, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await GetFollowingAllowedRedirectsAsync(url, cancellation);
        long total = response.Content.Headers.ContentLength ?? expectedSize;
        if (total > MaximumInstallerBytes) throw new InvalidDataException("The installer is larger than expected.");
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellation);
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
        {
            copied += read;
            if (copied > MaximumInstallerBytes) throw new InvalidDataException("The installer is larger than expected.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellation);
            if (total > 0) progress?.Report(Math.Min(1, (double)copied / total));
        }
    }
}
