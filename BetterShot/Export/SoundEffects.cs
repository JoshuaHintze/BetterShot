using System.IO;
using System.Security.Cryptography;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BetterShot.Export;

internal enum SoundEffectEvent { Screenshot, RecordingStart, RecordingStop }

internal sealed record SoundEffectChoice(string Id, string Label);

/// <summary>Resolves, validates, imports, and plays BetterShot's short UI sounds without ever blocking capture on an audio failure.</summary>
internal static class SoundEffects
{
    private static readonly object PlaybackGate = new();
    private static Playback? ActivePlayback;
    private static long PlaybackRequest;
    internal const string None = "none", Shutter = "shutter", SoftChime = "soft-chime", Up = "up", Down = "down", Giggity = "giggity", Custom = "custom";
    internal const long MaxCustomBytes = 10 * 1024 * 1024;
    private const int MaxDecodedBytes = 32 * 1024 * 1024;
    // Some shared-mode drivers emit a cold-start transient while their endpoint powers up.
    // Keep the endpoint short-lived, but feed it one quiet settling interval before the cue.
    private const int DeviceSettleMilliseconds = 40;
    // WasapiOut can report source EOF before the shared-mode engine and endpoint have
    // rendered their queued samples. Keep feeding silence so the audible tail drains.
    private const int DeviceDrainMilliseconds = 100;
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(15);
    internal static IReadOnlyList<SoundEffectChoice> Choices { get; } =
    [
        new(None, "None"), new(Shutter, "Shutter"), new(SoftChime, "Soft chime"),
        new(Up, "Up"), new(Down, "Down"), new(Giggity, "Giggity"), new(Custom, "Custom sound…")
    ];

    internal static string NormalizeId(string? id, string fallback) => Choices.Any(choice => choice.Id == id) ? id! : fallback;

    internal static Task<string> ImportAsync(string source) => ImportAsync(source, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterShot", "Sounds"));

    internal static async Task<string> ImportAsync(string source, string folder)
    {
        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not ".mp3" and not ".wav") throw new InvalidDataException("Choose an MP3 or WAV file.");
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException("The selected sound file no longer exists.", source);
        if (info.Length <= 0 || info.Length > MaxCustomBytes) throw new InvalidDataException("Custom sounds must be 10 MB or smaller.");
        await ValidateMediaAsync(source);
        Directory.CreateDirectory(folder);
        string hash;
        await using (FileStream stream = File.OpenRead(source)) hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        string stem = string.Concat(Path.GetFileNameWithoutExtension(source).Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character)).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(stem)) stem = "sound";
        string destination = Path.Combine(folder, stem[..Math.Min(stem.Length, 48)] + "-" + hash + extension);
        if (!File.Exists(destination)) File.Copy(source, destination);
        return destination;
    }

    internal static async Task PlayAsync(string id, string customPath, bool waitForFinish = false)
    {
        try
        {
            long request = Interlocked.Increment(ref PlaybackRequest);
            StopActivePlayback();
            string? path = EnsurePlayablePath(id, customPath);
            if (path is null) return;
            DecodedCue cue = await Task.Run(() => Decode(path)).ConfigureAwait(false);
            if (request != Volatile.Read(ref PlaybackRequest)) return;
            // Force construction/initialization onto the pool even when decode completed before
            // this await was observed; WASAPI must never inherit the busy WPF dispatcher.
            await Task.Run(() => PlayDecodedAsync(cue, request, TimeSpan.FromSeconds(16))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or TaskCanceledException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // A missing/busy audio device or stale custom file must never interrupt capture or recording.
        }
    }

    internal static bool IsAvailable(string id, string customPath) => EnsurePlayablePath(id, customPath) is not null;
    internal static bool IsPlayingForTest => ActivePlayback is not null;

    internal static void Shutdown()
    {
        Interlocked.Increment(ref PlaybackRequest);
        StopActivePlayback();
    }

    private static string? EnsurePlayablePath(string id, string customPath)
    {
        if (id == None) return null;
        if (id == Custom) return File.Exists(customPath) ? customPath : null;
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterShot", "Sounds", "Generated");
        if (id == Giggity)
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "giggity.mp3");
            return File.Exists(bundled) ? bundled : null;
        }
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, id + ".wav");
        if (!File.Exists(path)) File.WriteAllBytes(path, id switch
        {
            Shutter => CaptureSound.CreateShutterWave(),
            SoftChime => CaptureSound.CreateToneWave([(660, .00, .18), (880, .11, .25)]),
            Up => CaptureSound.CreateToneWave([(520, .00, .13), (780, .11, .20)]),
            Down => CaptureSound.CreateToneWave([(780, .00, .13), (520, .11, .20)]),
            _ => CaptureSound.CreateShutterWave()
        });
        return path;
    }

    private static async Task ValidateMediaAsync(string path)
    {
        TimeSpan duration;
        try
        {
            duration = await Task.Run(() =>
            {
                using var reader = new MediaFoundationReader(Path.GetFullPath(path));
                return reader.TotalTime;
            }).WaitAsync(TimeSpan.FromSeconds(4));
        }
        catch (Exception error) when (error is not InvalidDataException)
        {
            throw new InvalidDataException("Windows could not decode this sound file.", error);
        }
        if (duration <= TimeSpan.Zero) throw new InvalidDataException("Windows could not decode this sound file.");
        if (duration > MaxDuration) throw new InvalidDataException("Custom sounds must be 15 seconds or shorter.");
    }

    internal static Task ValidateForTestAsync(string path) => ValidateMediaAsync(path);

    private static DecodedCue Decode(string path)
    {
        using var reader = new MediaFoundationReader(Path.GetFullPath(path));
        using var pcm = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (pcm.Length + read > MaxDecodedBytes) throw new InvalidDataException("The decoded sound is too large to play safely.");
            pcm.Write(buffer, 0, read);
        }
        if (pcm.Length == 0) throw new InvalidDataException("Windows could not decode this sound file.");
        byte[] decoded = pcm.ToArray();
        int leadingSilenceBytes = AlignedSilenceBytes(reader.WaveFormat, DeviceSettleMilliseconds);
        int trailingSilenceBytes = AlignedSilenceBytes(reader.WaveFormat, DeviceDrainMilliseconds);
        var primed = new byte[checked(leadingSilenceBytes + decoded.Length + trailingSilenceBytes)];
        Buffer.BlockCopy(decoded, 0, primed, leadingSilenceBytes, decoded.Length);
        return new DecodedCue(primed, reader.WaveFormat, leadingSilenceBytes, trailingSilenceBytes);
    }

    private static int AlignedSilenceBytes(WaveFormat format, int milliseconds)
    {
        int bytes = checked((int)((long)format.AverageBytesPerSecond * milliseconds / 1000));
        return bytes - bytes % format.BlockAlign;
    }

    private static Task PlayDecodedAsync(DecodedCue cue, long request, TimeSpan timeout)
    {
        var playback = new Playback(cue);
        lock (PlaybackGate)
        {
            if (request != PlaybackRequest) { playback.Finish(); return playback.Completion; }
            ActivePlayback = playback;
        }
        playback.Start(timeout);
        return playback.Completion;
    }

    private static void StopActivePlayback()
    {
        Playback? playback;
        lock (PlaybackGate) { playback = ActivePlayback; ActivePlayback = null; }
        playback?.Finish();
    }

    private sealed record DecodedCue(byte[] Bytes, WaveFormat Format, int LeadingSilenceBytes, int TrailingSilenceBytes);

    internal static (byte[] Bytes, WaveFormat Format, int LeadingSilenceBytes, int TrailingSilenceBytes) DecodeForTest(string path)
    {
        DecodedCue cue = Decode(path);
        return (cue.Bytes, cue.Format, cue.LeadingSilenceBytes, cue.TrailingSilenceBytes);
    }

    private sealed class Playback
    {
        private readonly MMDevice _device;
        private readonly WasapiOut _output;
        private readonly RawSourceWaveStream _source;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private CancellationTokenSource? _timeout;
        private int _finished;

        internal Playback(DecodedCue cue)
        {
            MMDevice? device = null;
            WasapiOut? output = null;
            RawSourceWaveStream? source = null;
            try
            {
                source = new RawSourceWaveStream(cue.Bytes, 0, cue.Bytes.Length, cue.Format);
                using var enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                output = new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
                _device = device;
                _output = output;
                _source = source;
            }
            catch
            {
                output?.Dispose();
                device?.Dispose();
                source?.Dispose();
                throw;
            }
            // PlaybackStopped is raised on NAudio's render thread. Disposal can join that
            // thread, so move teardown to the pool instead of disposing from the callback.
            _output.PlaybackStopped += (_, _) => _ = Task.Run(Finish);
        }

        internal Task Completion => _completion.Task;

        internal void Start(TimeSpan timeout)
        {
            lock (_gate)
            {
                if (_finished != 0) return;
                try
                {
                    _output.Init(_source);
                    if (_finished != 0) return;
                    _output.Play();
                    _timeout = new CancellationTokenSource();
                    _ = StopAfterTimeoutAsync(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(16), _timeout.Token);
                    return;
                }
                catch { }
            }
            Finish();
        }

        private async Task StopAfterTimeoutAsync(TimeSpan timeout, CancellationToken cancellation)
        {
            try { await Task.Delay(timeout, cancellation).ConfigureAwait(false); Finish(); }
            catch (OperationCanceledException) { }
        }

        internal void Finish()
        {
            lock (_gate)
            {
                if (_finished != 0) return;
                _finished = 1;
                _timeout?.Cancel();
                try { _output.Stop(); } catch { }
                try { _output.Dispose(); } catch { }
                try { _device.Dispose(); } catch { }
                _source.Dispose();
            }
            lock (PlaybackGate) { if (ReferenceEquals(ActivePlayback, this)) ActivePlayback = null; }
            _completion.TrySetResult();
        }
    }
}
