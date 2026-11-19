namespace BetterShot.RecordingEditor;

internal enum RecordingExportKind { Mp4, Mp4WithoutAudio, Gif, Timelapse }

/// <summary>Pure timing and sizing math for recording exports.</summary>
internal static class ExportPlanning
{
    internal const double TimelapseTargetSeconds = 30;
    internal const int TimelapseFrameRate = 30;
    internal static readonly int[] TimelapseSpeeds = [2, 4, 8, 16];
    internal static readonly int[] GifFrameRates = [8, 10, 12, 15, 20, 24];
    internal static readonly int[] GifSizes = [480, 640, 800, 960, 1280, 0];
    internal const int DefaultGifFrameRate = 12;
    internal const int DefaultGifMaxDimension = 960;
    /// <summary>A GIF needs at least two frames and a quarter second of selection.</summary>
    internal const double MinimumGifSeconds = 0.25;

    /// <summary>
    /// The timelapse speed: the user's choice from <see cref="TimelapseSpeeds"/>, or (when null) the smallest power of two
    /// (at least 2x) that brings the kept selection down to <see cref="TimelapseTargetSeconds"/> or less.
    /// </summary>
    internal static int TimelapseSpeed(double keptSeconds, int? requested)
    {
        if (requested is int chosen && TimelapseSpeeds.Contains(chosen)) return chosen;
        int speed = 2;
        while (speed < 1024 && keptSeconds / speed > TimelapseTargetSeconds) speed *= 2;
        return speed;
    }

    /// <summary>Source times of each output frame when the kept selection plays at <paramref name="speed"/>x and <paramref name="framesPerSecond"/>.</summary>
    internal static IReadOnlyList<double> FrameTimes(EditTimeline timeline, double framesPerSecond, double speed = 1)
    {
        double fps = Math.Clamp(double.IsFinite(framesPerSecond) ? framesPerSecond : 30, 1, 120);
        double rate = Math.Max(1e-6, double.IsFinite(speed) ? speed : 1);
        double output = timeline.OutputDuration / rate;
        int count = (int)Math.Floor(output * fps + 1e-6);
        if (count == 0 && output > 0) count = 1;
        var times = new double[count];
        for (int index = 0; index < count; index++) times[index] = timeline.OutputToSource(index / fps * rate);
        return times;
    }

    internal static bool IsTooShortForGif(EditTimeline timeline, int framesPerSecond) =>
        timeline.OutputDuration < MinimumGifSeconds || FrameTimes(timeline, framesPerSecond).Count < 2;

    /// <summary>Per-frame GIF delays in hundredths of a second, rounded so the total stays true to the frame rate.</summary>
    internal static IReadOnlyList<ushort> GifDelays(int frameCount, double framesPerSecond)
    {
        var delays = new ushort[frameCount];
        for (int index = 0; index < frameCount; index++)
        {
            long start = (long)Math.Round(index * 100 / framesPerSecond), end = (long)Math.Round((index + 1) * 100 / framesPerSecond);
            delays[index] = (ushort)Math.Clamp(end - start, 2, ushort.MaxValue); // Browsers treat 0-1 as "default" (slow).
        }
        return delays;
    }

    /// <summary>Scales a size down (never up) so its longer side is at most <paramref name="maxDimension"/>; 0 keeps it.</summary>
    internal static (int Width, int Height) FitWithin(int width, int height, int maxDimension)
    {
        if (maxDimension <= 0 || Math.Max(width, height) <= maxDimension) return (Math.Max(1, width), Math.Max(1, height));
        double scale = maxDimension / (double)Math.Max(width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>H.264 needs even dimensions.</summary>
    internal static (int Width, int Height) Even(int width, int height) => (Math.Max(2, width & ~1), Math.Max(2, height & ~1));

    internal static int NormalizeGifFrameRate(int value) => GifFrameRates.Contains(value) ? value : DefaultGifFrameRate;
    internal static int NormalizeGifMaxDimension(int value) => GifSizes.Contains(value) ? value : DefaultGifMaxDimension;
}

/// <summary>Converts BGRA frames to NV12 (4:2:0, limited range), the input format Windows' H.264 encoder takes directly.</summary>
internal static class Nv12Converter
{
    internal static int BufferSize(int width, int height) => width * height * 3 / 2;

    /// <param name="bt709">Rec. 709 coefficients for HD sizes; Rec. 601 otherwise, matching what players assume.</param>
    internal static void Convert(ReadOnlySpan<byte> bgra, int width, int height, int stride, Span<byte> nv12, bool bt709)
    {
        (double kr, double kb) = bt709 ? (0.2126, 0.0722) : (0.299, 0.114);
        double kg = 1 - kr - kb;
        int yr = (int)Math.Round(219 * kr / 255 * 1024), yg = (int)Math.Round(219 * kg / 255 * 1024), yb = (int)Math.Round(219 * kb / 255 * 1024);
        for (int y = 0; y < height; y++)
        {
            int row = y * stride, target = y * width;
            for (int x = 0; x < width; x++)
            {
                int pixel = row + x * 4;
                nv12[target + x] = (byte)(16 + ((bgra[pixel + 2] * yr + bgra[pixel + 1] * yg + bgra[pixel] * yb + 512) >> 10));
            }
        }
        double uScale = 224 / 255.0 / (2 * (1 - kb)), vScale = 224 / 255.0 / (2 * (1 - kr));
        int chroma = width * height;
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                double red = 0, green = 0, blue = 0;
                int count = 0;
                for (int dy = 0; dy < 2 && y + dy < height; dy++)
                    for (int dx = 0; dx < 2 && x + dx < width; dx++)
                    {
                        int pixel = (y + dy) * stride + (x + dx) * 4;
                        blue += bgra[pixel]; green += bgra[pixel + 1]; red += bgra[pixel + 2]; count++;
                    }
                red /= count; green /= count; blue /= count;
                double luma = kr * red + kg * green + kb * blue;
                int index = chroma + (y / 2) * width + x;
                nv12[index] = ClampByte(128 + (blue - luma) * uScale);
                nv12[index + 1] = ClampByte(128 + (red - luma) * vScale);
            }
        }
    }

    private static byte ClampByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
