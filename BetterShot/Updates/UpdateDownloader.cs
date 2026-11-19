using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;

namespace BetterShot.Updates;

/// <summary>
/// Downloads the release installer and its ".sha256" sidecar into %LOCALAPPDATA%\BetterShot\Updates, verifies the hash,
/// and returns the verified path. Nothing is executed here; launching the installer is a separate, user-confirmed step.
/// </summary>
internal sealed class UpdateDownloader
{
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private const int MaximumChecksumBytes = 4096;
    private readonly HttpClient _client;
    private readonly string _directory;

    internal UpdateDownloader(HttpClient client, string? directory = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _directory = directory ?? DefaultDirectory;
    }

    internal static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterShot", "Updates");

    internal async Task<string> DownloadVerifiedInstallerAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(release);
        ReleaseAsset installer = release.Installer ?? throw new InvalidOperationException("This release does not include a Windows installer.");
        ReleaseAsset checksumAsset = release.InstallerChecksum ?? throw new InvalidOperationException("This release does not include an installer checksum, so it cannot be verified.");
        string fileName = Path.GetFileName(installer.Name);
        if (fileName != installer.Name || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("The installer name in the release is not a plain file name.");

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
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
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

    private async Task<string> DownloadChecksumAsync(string url, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellation);
        var buffer = new byte[MaximumChecksumBytes];
        int total = 0, read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellation)) > 0) total += read;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    private async Task DownloadFileAsync(string url, string path, long expectedSize, IProgress<double>? progress, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
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
