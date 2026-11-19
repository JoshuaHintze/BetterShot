using Microsoft.Win32;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Export;

/// <summary>Post-processes rendered captures (resize, PNG/JPEG encoding) and writes them without damaging existing files.</summary>
internal static class ImageExporter
{
    private const int MaximumSaveAttempts = 20;

    internal static BitmapSource Resize(BitmapSource source, int maxDimension)
    {
        ArgumentNullException.ThrowIfNull(source);
        (int width, int height) = ExportResize.FitWithin(source.PixelWidth, source.PixelHeight, maxDimension);
        if (width == source.PixelWidth && height == source.PixelHeight) return source;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (DrawingContext context = visual.RenderOpen())
            context.DrawImage(source, new Rect(0, 0, width, height));
        var resized = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        resized.Render(visual);
        resized.Freeze();
        return resized;
    }

    /// <summary>Clipboard copies stay full size unless the user opted into resizing them too.</summary>
    internal static BitmapSource PrepareForClipboard(BitmapSource rendered, ExportOptions options) =>
        options.ResizeClipboard ? Resize(rendered, options.MaxDimension) : rendered;

    internal static BitmapSource PrepareForExport(BitmapSource rendered, ExportOptions options) =>
        Resize(rendered, options.MaxDimension);

    internal static void Encode(BitmapSource image, CaptureImageFormat format, int jpegQuality, Stream destination)
    {
        BitmapEncoder encoder = format == CaptureImageFormat.Jpeg
            ? new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) }
            : new PngBitmapEncoder();
        BitmapSource frame = format == CaptureImageFormat.Jpeg ? FlattenForJpeg(image) : image;
        encoder.Frames.Add(BitmapFrame.Create(frame));
        encoder.Save(destination);
    }

    private static BitmapSource FlattenForJpeg(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>Saves into the configured folder with the configured name pattern, never overwriting an existing file.</summary>
    internal static string SaveToFolder(BitmapSource rendered, ExportOptions options, DateTime localTime, int counter)
    {
        string folder = Path.GetFullPath(string.IsNullOrWhiteSpace(options.SaveFolder) ? ExportOptions.DefaultSaveFolder : options.SaveFolder);
        Directory.CreateDirectory(folder);
        string fileName = FileNameTemplate.BuildFileName(options.FileNamePattern, localTime, counter, options.Extension);
        BitmapSource prepared = PrepareForExport(rendered, options);
        for (int attempt = 1; ; attempt++)
        {
            string destination = FileNameTemplate.ResolveUniquePath(folder, fileName, File.Exists);
            try
            {
                WriteAtomically(prepared, options, destination, overwrite: false);
                return destination;
            }
            catch (IOException) when (attempt < MaximumSaveAttempts && File.Exists(destination))
            {
                // Another writer claimed the name between the check and the move; try the next free name.
            }
        }
    }

    /// <summary>Writes to a temporary file in the destination folder, then moves it into place.</summary>
    internal static void WriteAtomically(BitmapSource prepared, ExportOptions options, string destination, bool overwrite)
    {
        string fullPath = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Encode(prepared, options.Format, options.JpegQuality, stream);
            File.Move(temporary, fullPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Shows a Save As dialog pre-filled from the save folder, name pattern, and format. Returns the saved path or null if canceled.</summary>
    internal static string? SaveWithDialog(Window? owner, BitmapSource rendered, ExportOptions options, DateTime localTime, int counter)
    {
        string folder = string.IsNullOrWhiteSpace(options.SaveFolder) ? ExportOptions.DefaultSaveFolder : options.SaveFolder;
        bool jpeg = options.Format == CaptureImageFormat.Jpeg;
        var dialog = new SaveFileDialog
        {
            Title = "Save capture",
            Filter = jpeg ? "JPEG image (*.jpg)|*.jpg|PNG image (*.png)|*.png" : "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            DefaultExt = options.Extension,
            AddExtension = true,
            FileName = FileNameTemplate.BuildFileName(options.FileNamePattern, localTime, counter, options.Extension)
        };
        if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted != true) return null;

        string path = Path.GetFullPath(dialog.FileName);
        string extension = Path.GetExtension(path);
        CaptureImageFormat chosen = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? CaptureImageFormat.Jpeg : CaptureImageFormat.Png;
        ExportOptions effective = options with { Format = chosen };
        WriteAtomically(PrepareForExport(rendered, effective), effective, path, overwrite: true);
        return path;
    }
}
