using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Editing.BackgroundRemoval;

/// <summary>Runs U²-Netp locally with ONNX Runtime (CPU) and returns an <see cref="AlphaMask"/> for the image.</summary>
internal static class BackgroundRemover
{
    /// <summary>Reads the pixels on the calling (UI) thread, then preprocesses, infers, and postprocesses on a worker thread.</summary>
    internal static Task<AlphaMask> ComputeMaskAsync(BitmapSource image, string modelPath, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(image);
        int width = image.PixelWidth, height = image.PixelHeight;
        byte[] pixels = ReadBgra(image);
        return Task.Run(() => ComputeMask(pixels, width, height, modelPath, cancellation), cancellation);
    }

    private static AlphaMask ComputeMask(byte[] pixels, int width, int height, string modelPath, CancellationToken cancellation)
    {
        float[] input = BackgroundRemovalMath.Preprocess(pixels, width, height);
        cancellation.ThrowIfCancellationRequested();
        float[] prediction = RunModel(modelPath, input);
        cancellation.ThrowIfCancellationRequested();
        byte[] mask = BackgroundRemovalMath.Postprocess(prediction, width, height, BackgroundRemovalMath.FeatherRadiusFor(width, height));
        return new AlphaMask(width, height, mask);
    }

    /// <summary>Feeds the [1,3,320,320] tensor and returns the first output (the fused saliency map d0).</summary>
    internal static float[] RunModel(string modelPath, float[] input)
    {
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        using var session = new InferenceSession(modelPath, options);
        string inputName = session.InputMetadata.Keys.First();
        int size = BackgroundRemovalMath.ModelSize;
        var tensor = new DenseTensor<float>(input, [1, 3, size, size]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        Tensor<float> output = results.First().AsTensor<float>();
        return output.ToArray();
    }

    private static byte[] ReadBgra(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }
}
