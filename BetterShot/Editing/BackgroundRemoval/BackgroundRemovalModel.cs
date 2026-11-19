using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;

namespace BetterShot.Editing.BackgroundRemoval;

/// <summary>
/// Location, pinned source, and integrity check for the U²-Netp model. The model is never bundled: it is downloaded
/// only after the user confirms, written to a temporary file, and moved into place only if its SHA-256 matches.
/// </summary>
internal sealed class BackgroundRemovalModel
{
    internal const string FileName = "u2netp.onnx";
    internal const string DownloadUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx";
    internal const string SourceDescription = "github.com/danielgatis/rembg (U²-Netp, Apache-2.0)";
    internal const string Sha256 = "309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8";
    internal const long ExpectedBytes = 4_574_861;

    internal BackgroundRemovalModel(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory;
    }

    internal static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterShot", "Models");

    internal string Directory { get; }
    internal string ModelPath => Path.Combine(Directory, FileName);

    internal static string SizeDescription => $"{ExpectedBytes / 1024d / 1024d:0.0} MB";

    /// <summary>True when the model file exists and matches the pinned hash. A damaged file is deleted so it can be downloaded again.</summary>
    internal bool IsInstalled()
    {
        if (!File.Exists(ModelPath)) return false;
        try
        {
            using FileStream stream = File.OpenRead(ModelPath);
            if (stream.Length == ExpectedBytes && HashMatches(stream, Sha256)) return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        TryDelete(ModelPath);
        return false;
    }

    /// <summary>Downloads to a temporary file in the model folder, verifies the hash, then moves it into place.</summary>
    internal async Task DownloadAsync(HttpClient client, IProgress<double>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(client);
        System.IO.Directory.CreateDirectory(Directory);
        string temporary = Path.Combine(Directory, $".{FileName}.{Guid.NewGuid():N}.download");
        try
        {
            using (HttpResponseMessage response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? ExpectedBytes;
                if (total > ExpectedBytes * 2) throw new InvalidDataException("The model download is larger than expected.");
                await using Stream source = await response.Content.ReadAsStreamAsync(cancellation);
                await using var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await CopyWithProgressAsync(source, destination, total, progress, cancellation);
            }
            await using (FileStream written = File.OpenRead(temporary))
            {
                if (written.Length != ExpectedBytes || !HashMatches(written, Sha256))
                    throw new InvalidDataException("The downloaded model did not match its expected checksum, so it was discarded.");
            }
            File.Move(temporary, ModelPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    internal static bool HashMatches(Stream stream, string expectedHex)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek) stream.Position = 0;
        byte[] hash = SHA256.HashData(stream);
        return string.Equals(Convert.ToHexString(hash), expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyWithProgressAsync(Stream source, Stream destination, long total, IProgress<double>? progress, CancellationToken cancellation)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
        {
            copied += read;
            if (copied > ExpectedBytes * 2) throw new InvalidDataException("The model download is larger than expected.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellation);
            progress?.Report(total > 0 ? Math.Min(1, (double)copied / total) : 0);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
