namespace BetterShot.Editing.BackgroundRemoval;

/// <summary>
/// Pure pre- and post-processing for the U²-Netp salient-object model, matching the rembg reference pipeline:
/// resize to 320×320, scale by the image maximum, ImageNet mean/std normalization in NCHW RGB order; then
/// min-max normalize the predicted map, resize it back, and feather the edge.
/// </summary>
internal static class BackgroundRemovalMath
{
    internal const int ModelSize = 320;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Deviation = [0.229f, 0.224f, 0.225f];

    /// <summary>Converts top-down BGRA pixels to a [1, 3, 320, 320] RGB tensor buffer.</summary>
    internal static float[] Preprocess(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) throw new ArgumentException("The pixel buffer does not match its size.", nameof(bgra));
        int plane = ModelSize * ModelSize;
        var tensor = new float[3 * plane];
        float maximum = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            // BGRA stores red at offset 2; the model expects R, G, B planes.
            int sourceOffset = 2 - channel;
            float[] resized = ResizeBilinear(ExtractChannel(bgra, width, height, sourceOffset), width, height, ModelSize, ModelSize);
            Array.Copy(resized, 0, tensor, channel * plane, plane);
            maximum = Math.Max(maximum, resized.Max());
        }
        if (maximum <= 0) maximum = 1;
        for (int channel = 0; channel < 3; channel++)
        {
            int start = channel * plane;
            for (int index = start; index < start + plane; index++)
                tensor[index] = (tensor[index] / maximum - Mean[channel]) / Deviation[channel];
        }
        return tensor;
    }

    /// <summary>Turns the raw 320×320 prediction into a feathered 8-bit mask at the original size.</summary>
    internal static byte[] Postprocess(ReadOnlySpan<float> prediction, int width, int height, int featherRadius)
    {
        if (prediction.Length < ModelSize * ModelSize) throw new ArgumentException("The model output is smaller than expected.", nameof(prediction));
        float[] normalized = NormalizeMinMax(prediction[..(ModelSize * ModelSize)]);
        float[] resized = ResizeBilinear(normalized, ModelSize, ModelSize, width, height);
        var mask = new byte[width * height];
        for (int index = 0; index < mask.Length; index++) mask[index] = (byte)Math.Clamp((int)Math.Round(resized[index] * 255f), 0, 255);
        return featherRadius > 0 ? Feather(mask, width, height, featherRadius) : mask;
    }

    /// <summary>A gentle edge softening proportional to the image size: about 1 px per 500 px of the shorter side.</summary>
    internal static int FeatherRadiusFor(int width, int height) => Math.Clamp(Math.Min(width, height) / 500, 1, 4);

    internal static float[] NormalizeMinMax(ReadOnlySpan<float> values)
    {
        float minimum = float.MaxValue, maximum = float.MinValue;
        foreach (float value in values)
        {
            if (!float.IsFinite(value)) continue;
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }
        var result = new float[values.Length];
        float range = maximum - minimum;
        if (!(range > 1e-6f)) return result;
        for (int index = 0; index < values.Length; index++)
            result[index] = float.IsFinite(values[index]) ? (values[index] - minimum) / range : 0;
        return result;
    }

    /// <summary>Bilinear resampling with pixel-center alignment (the same convention as common image libraries).</summary>
    internal static float[] ResizeBilinear(float[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        var target = new float[targetWidth * targetHeight];
        double scaleX = (double)sourceWidth / targetWidth, scaleY = (double)sourceHeight / targetHeight;
        for (int row = 0; row < targetHeight; row++)
        {
            double sourceY = Math.Clamp((row + 0.5) * scaleY - 0.5, 0, sourceHeight - 1);
            int top = (int)sourceY, bottom = Math.Min(top + 1, sourceHeight - 1);
            float verticalWeight = (float)(sourceY - top);
            for (int column = 0; column < targetWidth; column++)
            {
                double sourceX = Math.Clamp((column + 0.5) * scaleX - 0.5, 0, sourceWidth - 1);
                int left = (int)sourceX, right = Math.Min(left + 1, sourceWidth - 1);
                float horizontalWeight = (float)(sourceX - left);
                float upper = Lerp(source[top * sourceWidth + left], source[top * sourceWidth + right], horizontalWeight);
                float lower = Lerp(source[bottom * sourceWidth + left], source[bottom * sourceWidth + right], horizontalWeight);
                target[row * targetWidth + column] = Lerp(upper, lower, verticalWeight);
            }
        }
        return target;
    }

    /// <summary>Two passes of a separable box blur (close to a Gaussian) to soften the cut-out edge.</summary>
    internal static byte[] Feather(byte[] mask, int width, int height, int radius)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (radius <= 0) return (byte[])mask.Clone();
        byte[] current = mask;
        for (int pass = 0; pass < 2; pass++)
        {
            current = BoxBlur(current, width, height, radius, horizontal: true);
            current = BoxBlur(current, width, height, radius, horizontal: false);
        }
        return current;
    }

    private static byte[] BoxBlur(byte[] source, int width, int height, int radius, bool horizontal)
    {
        var result = new byte[source.Length];
        int lines = horizontal ? height : width, length = horizontal ? width : height;
        for (int line = 0; line < lines; line++)
        {
            int sum = 0, count = 0;
            for (int position = -radius; position <= radius; position++)
            {
                if (position < 0 || position >= length) continue;
                sum += source[IndexOf(line, position, width, horizontal)];
                count++;
            }
            for (int position = 0; position < length; position++)
            {
                result[IndexOf(line, position, width, horizontal)] = (byte)((sum + count / 2) / count);
                int leaving = position - radius, entering = position + radius + 1;
                if (leaving >= 0) { sum -= source[IndexOf(line, leaving, width, horizontal)]; count--; }
                if (entering < length) { sum += source[IndexOf(line, entering, width, horizontal)]; count++; }
            }
        }
        return result;
    }

    private static int IndexOf(int line, int position, int width, bool horizontal) =>
        horizontal ? line * width + position : position * width + line;

    private static float[] ExtractChannel(byte[] bgra, int width, int height, int offset)
    {
        var channel = new float[width * height];
        for (int index = 0; index < channel.Length; index++) channel[index] = bgra[index * 4 + offset];
        return channel;
    }

    private static float Lerp(float start, float end, float amount) => start + (end - start) * amount;
}
