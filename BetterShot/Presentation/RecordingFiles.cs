using BetterShot.Persistence;
using BetterShot.Recording;
using BetterShot.RecordingEditor;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using VisualBasicFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;

namespace BetterShot.Presentation;

/// <summary>File operations for finished recordings: thumbnail, open, show in folder, drag, and delete.</summary>
internal static class RecordingFiles
{
    /// <summary>The Windows shell thumbnail of the movie (usually its first frame), or null if Windows has none yet.</summary>
    internal static async Task<BitmapSource?> LoadThumbnailAsync(string moviePath, uint size = 320)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(moviePath);
            using StorageItemThumbnail? thumbnail = await file.GetThumbnailAsync(ThumbnailMode.VideosView, size, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null || thumbnail.Size == 0) return null;
            using Stream stream = thumbnail.AsStreamForRead();
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            memory.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; } // Shell thumbnails are cosmetic; the card falls back to an icon.
    }

    internal static void Open(string path) => Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })?.Dispose();

    internal static void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true })?.Dispose();

    /// <summary>
    /// The file to drag out for a recording: its marked copy when it has been edited (always when it has a redaction),
    /// otherwise the movie. Null (with a reason) when a redacted recording has no up-to-date covered copy yet.
    /// </summary>
    internal static (string? Path, string? Note) SharePath(string moviePath) => RecordingSharing.ResolveSharePath(moviePath);

    /// <summary>The movie as a file drop (CF_HDROP) for dragging into chat apps, email, or Explorer.</summary>
    internal static DataObject CreateDragData(string path)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { path }, false);
        return data;
    }

    /// <summary>Sends the movie, its camera sidecar, its event log, and its edit project to the Recycle Bin; drops its cached marked copy.</summary>
    internal static void DeleteToRecycleBin(RecordingEntry entry)
    {
        RecordingSharing.DeleteMarkedCopy(entry.MoviePath);
        foreach (string? path in new[] { entry.MoviePath, entry.CameraPath, RecordingEventLog.SidecarPath(entry.MoviePath), RecordingEditProject.SidecarPath(entry.MoviePath) })
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            VisualBasicFileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }

    internal static string FormatDuration(double seconds)
    {
        TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
    }
}
