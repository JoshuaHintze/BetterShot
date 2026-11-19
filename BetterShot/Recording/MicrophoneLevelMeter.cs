using NAudio.CoreAudioApi;

namespace BetterShot.Recording;

/// <summary>
/// Reads the live input peak of the microphone being recorded from the Windows endpoint meter, for the level bars on
/// the recording controls. It opens no capture stream of its own; the meter reports the stream the recorder runs.
/// </summary>
internal sealed class MicrophoneLevelMeter : IDisposable
{
    private readonly MMDevice _device;

    private MicrophoneLevelMeter(MMDevice device) => _device = device;

    /// <summary>Opens the meter for a device id (empty = Windows default), or returns null when it is unavailable.</summary>
    internal static MicrophoneLevelMeter? TryOpen(string deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice device = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                : enumerator.GetDevice(deviceId);
            return new MicrophoneLevelMeter(device);
        }
        catch (Exception) { return null; } // No meter: the bars simply stay dim; recording is unaffected.
    }

    /// <summary>Peak level 0..1 since the last read, or 0 when the device stopped reporting.</summary>
    internal float ReadPeak()
    {
        try { return Math.Clamp(_device.AudioMeterInformation.MasterPeakValue, 0f, 1f); }
        catch (Exception) { return 0f; } // Device unplugged mid-recording; the recorder reports that failure itself.
    }

    /// <summary>How many of <paramref name="barCount"/> bars to light for a 0..1 peak, on a perceptual (dB-like) scale.</summary>
    internal static int LitBars(float peak, int barCount)
    {
        if (peak <= 0.003f) return 0;
        double decibels = 20 * Math.Log10(peak);
        double fraction = Math.Clamp((decibels + 50) / 50, 0, 1);
        return Math.Clamp((int)Math.Ceiling(fraction * barCount), 1, barCount);
    }

    public void Dispose() => _device.Dispose();
}
