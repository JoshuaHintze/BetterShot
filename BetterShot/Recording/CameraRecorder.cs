using System.IO;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace BetterShot.Recording;

/// <summary>
/// Records the webcam to its own MP4 ("-camera" sidecar) with Windows.Media.Capture, started just before and stopped
/// just after the screen recording. Its start offset relative to the screen timeline is logged as a "camera" event.
/// </summary>
internal sealed class CameraRecorder : IAsyncDisposable
{
    private readonly MediaCapture _capture;
    private bool _recording;

    private CameraRecorder(MediaCapture capture, string path)
    {
        _capture = capture;
        OutputPath = path;
    }

    internal string OutputPath { get; }

    /// <summary>Opens the camera (empty id = first camera) and starts recording. Throws <see cref="RecordingException"/>.</summary>
    internal static async Task<CameraRecorder> StartAsync(string deviceId, string outputPath)
    {
        var capture = new MediaCapture();
        try
        {
            string? id = string.IsNullOrWhiteSpace(deviceId) ? await FirstCameraIdAsync() : deviceId;
            if (id is null) throw new InvalidOperationException("No camera is connected.");
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl
            });
        }
        catch (Exception error)
        {
            capture.Dispose();
            throw RecordingException.ForCamera(error);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, []);
            StorageFile file = await StorageFile.GetFileFromPathAsync(outputPath);
            MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
            profile.Audio = null;
            await capture.StartRecordToStorageFileAsync(profile, file);
            return new CameraRecorder(capture, outputPath) { _recording = true };
        }
        catch (Exception error)
        {
            capture.Dispose();
            TryDelete(outputPath);
            throw RecordingException.ForCamera(error);
        }
    }

    /// <summary>Pauses with the screen recording so both timelines stay aligned. Best effort.</summary>
    internal async Task SetPausedAsync(bool paused)
    {
        if (!_recording) return;
        if (paused) await _capture.PauseRecordAsync(Windows.Media.Devices.MediaCapturePauseBehavior.RetainHardwareResources);
        else await _capture.ResumeRecordAsync();
    }

    internal async Task StopAsync()
    {
        if (!_recording) return;
        _recording = false;
        await _capture.StopRecordAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        catch (Exception) { } // Disposal after a failure: the file may be incomplete, which the caller already reports.
        _capture.Dispose();
    }

    private static async Task<string?> FirstCameraIdAsync()
    {
        DeviceInformationCollection cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return cameras.FirstOrDefault(camera => camera.IsEnabled)?.Id;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A device choice for the Settings pickers.</summary>
internal sealed record RecordingDevice(string Id, string Name);

/// <summary>Lists microphones (WASAPI capture endpoints) and cameras for Settings.</summary>
internal static class RecordingDevices
{
    internal static IReadOnlyList<RecordingDevice> Microphones()
    {
        var result = new List<RecordingDevice>();
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        foreach (NAudio.CoreAudioApi.MMDevice device in enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active))
        {
            using (device) result.Add(new RecordingDevice(device.ID, device.FriendlyName));
        }
        return result;
    }

    internal static async Task<IReadOnlyList<RecordingDevice>> CamerasAsync()
    {
        DeviceInformationCollection cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return cameras.Where(camera => camera.IsEnabled).Select(camera => new RecordingDevice(camera.Id, camera.Name)).ToArray();
    }
}
