using BetterShot.Editing;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace BetterShot.Presentation;

internal static class CaptureDragData
{
    private const string FilePrefix = "BetterShot-Drag-";
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);

    internal static DataObject CreateData(CaptureDocument document, bool asFile = false)
    {
        ArgumentNullException.ThrowIfNull(document);

        BitmapSource rendered = document.Render();
        var pngStream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        encoder.Save(pngStream);
        pngStream.Position = 0;

        CleanOldExports();
        string directory = GetExportDirectory();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{FilePrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            pngStream.CopyTo(file);
        }
        pngStream.Position = 0;

        var data = new DataObject();
        if (!asFile)
        {
            data.SetData(DataFormats.Bitmap, rendered, false);
            data.SetData("PNG", pngStream, false);
        }
        else
        {
            pngStream.Dispose();
        }

        data.SetData(DataFormats.FileDrop, new[] { path }, false);
        return data;
    }

    private static string GetExportDirectory() =>
        Path.Combine(Path.GetTempPath(), "BetterShot", "DragExports");

    private static void CleanOldExports()
    {
        try
        {
            string directory = GetExportDirectory();
            if (!Directory.Exists(directory)) return;
            DateTime cutoff = DateTime.UtcNow - MaximumAge;
            foreach (string path in Directory.EnumerateFiles(directory, $"{FilePrefix}*.png", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                }
                catch { }
            }
        }
        catch { }
    }

}