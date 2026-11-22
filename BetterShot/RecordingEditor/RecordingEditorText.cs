using System.Globalization;

namespace BetterShot.RecordingEditor;

/// <summary>Times as the recording editor shows and accepts them: "0:13.2" (tenths), "1:02:03.4" past an hour.</summary>
internal static class RecordingTimeText
{
    internal static string Tenths(double seconds)
    {
        double safe = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
        long tenths = (long)Math.Floor(safe * 10 + 1e-6);
        long whole = tenths / 10, hours = whole / 3600, minutes = whole / 60 % 60, remainder = whole % 60;
        string fraction = (tenths % 10).ToString(CultureInfo.InvariantCulture);
        return hours > 0
            ? $"{hours}:{minutes:00}:{remainder:00}.{fraction}"
            : $"{whole / 60}:{remainder:00}.{fraction}";
    }

    /// <summary>Whole seconds, "0:13" or "1:02:03".</summary>
    internal static string Whole(double seconds)
    {
        long whole = (long)Math.Round(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);
        return whole >= 3600 ? $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}" : $"{whole / 60}:{whole % 60:00}";
    }

    /// <summary>Accepts "13.2", "0:13.2", "1:02:03.5" (a comma works as the decimal point too).</summary>
    internal static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3) return false;
        double total = 0;
        for (int index = 0; index < parts.Length; index++)
        {
            bool last = index == parts.Length - 1;
            if (!double.TryParse(parts[index], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value)) return false;
            if (!last && value != Math.Floor(value)) return false;
            if (index > 0 && value >= 60) return false;
            total = total * 60 + value;
        }
        seconds = total;
        return double.IsFinite(total);
    }
}

/// <summary>The header's subtitle and file sizes, e.g. "Today at 1:20 PM · 0:13 · 2278 × 1008 · 29 fps · 5.6 MB".</summary>
internal static class RecordingEditorText
{
    internal static string Subtitle(DateTime createdLocal, DateTime nowLocal, double duration, int width, int height, double frameRate, long? bytes, CultureInfo culture)
    {
        var parts = new List<string> { When(createdLocal, nowLocal, culture), RecordingTimeText.Whole(duration) };
        if (width > 0 && height > 0) parts.Add($"{width} × {height}");
        if (frameRate > 0) parts.Add($"{Math.Round(frameRate).ToString(CultureInfo.InvariantCulture)} fps");
        if (bytes is long size) parts.Add(Size(size));
        return string.Join(" · ", parts);
    }

    internal static string When(DateTime createdLocal, DateTime nowLocal, CultureInfo culture)
    {
        string time = createdLocal.ToString("t", culture);
        if (createdLocal.Date == nowLocal.Date) return $"Today at {time}";
        if (createdLocal.Date == nowLocal.Date.AddDays(-1)) return $"Yesterday at {time}";
        string day = createdLocal.Year == nowLocal.Year ? createdLocal.ToString("MMM d", culture) : createdLocal.ToString("MMM d, yyyy", culture);
        return $"{day} at {time}";
    }

    /// <summary>Windows-style sizes (1 KB = 1024 bytes): "640 KB", "5.6 MB", "1.2 GB".</summary>
    internal static string Size(long bytes)
    {
        double value = Math.Max(0, bytes);
        if (value < 1024 * 1024) return $"{Math.Max(1, Math.Round(value / 1024)).ToString(CultureInfo.InvariantCulture)} KB";
        if (value < 1024L * 1024 * 1024) return $"{(value / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture)} MB";
        return $"{(value / 1024 / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture)} GB";
    }
}

/// <summary>Rough output sizes for the export cards ("About 5.6 MB").</summary>
internal static class ExportEstimate
{
    /// <summary>Screen recordings palettize well; measured GIFs of desktop content land near this many bytes per pixel per frame.</summary>
    internal const double GifBytesPerPixel = 0.08;

    internal static long Mp4Bytes(double keptSeconds, Mp4ExportPlan plan, bool includeAudio) => plan.EstimateBytes(keptSeconds, includeAudio);

    // A variable-rate encoder often needs fewer bits for desktop content than its configured target.
    // Use the observed screen bitrate at the selected pixel rate and the encoder budget as two estimates.
    internal static string Mp4Range(double seconds, Mp4ExportPlan plan, bool includeAudio, VideoInfo? source, long? fileBytes)
    {
        long budget = Mp4Bytes(seconds, plan, includeAudio);
        if (source is null || source.Duration <= 0 || source.Width <= 0 || source.Height <= 0 || fileBytes is not > 0)
            return About(budget);
        double sourceBitsPerSecond = fileBytes.Value * 8 / source.Duration;
        double screenRate = Math.Max(0, sourceBitsPerSecond - (source.HasAudio ? plan.AudioBitrate : 0));
        double pixelRatio = (plan.Width * (double)plan.Height) / (source.Width * (double)source.Height);
        double adjustedRate = screenRate * pixelRatio * plan.FrameRate / Math.Max(1, source.FrameRate);
        long observed = (long)Math.Round(seconds * (adjustedRate + (includeAudio ? plan.AudioBitrate : 0)) / 8);
        if (observed <= 0 || observed >= budget * .95) return About(budget);
        string low = RecordingEditorText.Size(observed), high = RecordingEditorText.Size(budget);
        if (low == high) return About(budget);
        int unit = low.LastIndexOf(' ');
        if (unit >= 0 && high.EndsWith(low[unit..], StringComparison.Ordinal)) low = low[..unit];
        return $"Est. {low}–{high}";
    }

    internal static long GifBytes(double keptSeconds, int framesPerSecond, int width, int height) =>
        (long)Math.Round(Math.Floor(Math.Max(0, keptSeconds) * Math.Max(1, framesPerSecond)) * Math.Max(1, width) * Math.Max(1, height) * GifBytesPerPixel);

    /// <summary>A silent movie at the export bitrate for its size, playing <paramref name="speed"/> times faster.</summary>
    internal static long TimelapseBytes(double keptSeconds, int speed, int width, int height)
    {
        (int evenWidth, int evenHeight) = ExportPlanning.Even(width, height);
        double bitsPerSecond = Recording.RecordingGeometry.SuggestBitrate(evenWidth, evenHeight, ExportPlanning.TimelapseFrameRate);
        return (long)Math.Round(Math.Max(0, keptSeconds) / Math.Max(1, speed) * bitsPerSecond / 8);
    }

    internal static string About(long bytes) => "About " + RecordingEditorText.Size(bytes);
}
