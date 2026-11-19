using System.IO;

namespace BetterShot.Export;

public enum CaptureImageFormat { Png, Jpeg }

/// <summary>Format, resize, and destination choices applied to saved files, drag exports, and (optionally) clipboard copies.</summary>
internal sealed record ExportOptions(
    CaptureImageFormat Format,
    int JpegQuality,
    int MaxDimension,
    bool ResizeClipboard,
    string SaveFolder,
    string FileNamePattern)
{
    internal const int DefaultJpegQuality = 90;

    internal static ExportOptions Default { get; } = new(CaptureImageFormat.Png, DefaultJpegQuality, 0, false,
        DefaultSaveFolder, FileNameTemplate.DefaultPattern);

    internal string Extension => Format == CaptureImageFormat.Jpeg ? ".jpg" : ".png";
    internal string FormatLabel => Format == CaptureImageFormat.Jpeg ? "JPEG" : "PNG";

    /// <summary>%USERPROFILE%\Pictures\BetterShot, following a redirected Pictures library when Windows reports one.</summary>
    internal static string DefaultSaveFolder
    {
        get
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures))
                pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            return Path.Combine(pictures, "BetterShot");
        }
    }
}

internal static class ExportResize
{
    internal static readonly int[] Choices = [0, 1920, 1280];

    /// <summary>Scales down proportionally so the longer side is at most <paramref name="maxDimension"/>; never upscales. 0 means full size.</summary>
    internal static (int Width, int Height) FitWithin(int width, int height, int maxDimension)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        if (maxDimension <= 0 || (width <= maxDimension && height <= maxDimension)) return (width, height);
        double scale = (double)maxDimension / Math.Max(width, height);
        int scaledWidth = width >= height ? maxDimension : Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero));
        int scaledHeight = height >= width ? maxDimension : Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero));
        return (scaledWidth, scaledHeight);
    }
}
