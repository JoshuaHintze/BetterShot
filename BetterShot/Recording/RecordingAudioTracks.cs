using System.IO;

namespace BetterShot.Recording;

/// <summary>A recorded audio source that is also kept as its own track beside the movie.</summary>
internal enum AudioTrackKind { System, Microphone }

/// <summary>
/// The hidden per-source audio sidecars of a recording: ".&lt;movie&gt;.mp4.system.wav" and ".&lt;movie&gt;.mp4.mic.wav",
/// 48 kHz stereo 16-bit PCM on the same timeline as the movie's mixed soundtrack (sample 0 = video time 0, paused
/// time excluded). The MP4 keeps the mix so it plays everywhere; the editor uses these for per-track waveforms, mute
/// previews, and re-mixed exports. Older recordings have none and fall back to the movie's single track.
/// </summary>
internal static class RecordingAudioTracks
{
    internal const int SampleRate = AudioMixer.SampleRate;
    internal const int Channels = AudioMixer.Channels;
    internal static readonly AudioTrackKind[] Kinds = [AudioTrackKind.System, AudioTrackKind.Microphone];

    internal static string Suffix(AudioTrackKind kind) => kind == AudioTrackKind.Microphone ? ".mic.wav" : ".system.wav";

    internal static string SidecarPath(string moviePath, AudioTrackKind kind)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(moviePath)) ?? string.Empty;
        return Path.Combine(folder, "." + Path.GetFileName(moviePath) + Suffix(kind));
    }

    /// <summary>Every sidecar path, whether or not it exists (for deletion and moves).</summary>
    internal static IEnumerable<string> AllSidecarPaths(string moviePath) => Kinds.Select(kind => SidecarPath(moviePath, kind));

    /// <summary>The tracks this recording has on disk, in display order (system audio first, then the microphone).</summary>
    internal static IReadOnlyList<AudioTrackKind> Existing(string moviePath) =>
        Kinds.Where(kind => File.Exists(SidecarPath(moviePath, kind))).ToArray();

    internal static void Hide(string path)
    {
        if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
    }

    /// <summary>Moves the sidecars along with a movie that moved. Best effort: a sidecar that cannot move is left behind.</summary>
    internal static void MoveSidecars(string fromMoviePath, string toMoviePath)
    {
        foreach (AudioTrackKind kind in Kinds)
        {
            string source = SidecarPath(fromMoviePath, kind);
            if (!File.Exists(source)) continue;
            string destination = SidecarPath(toMoviePath, kind);
            try
            {
                ReplaceFile(source, destination);
            }
            catch (IOException) { } // The movie still plays with its mixed soundtrack; the editor shows one Audio track.
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Moves <paramref name="source"/> over <paramref name="destination"/> (clearing a hidden attribute that blocks it) and hides it.</summary>
    internal static void ReplaceFile(string source, string destination)
    {
        if (File.Exists(destination)) File.SetAttributes(destination, FileAttributes.Normal);
        File.Move(source, destination, overwrite: true);
        Hide(destination);
    }

    internal static void DeleteSidecars(string moviePath)
    {
        foreach (string path in AllSidecarPaths(moviePath)) TryDelete(path);
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (IOException) { } // A leftover hidden sidecar is harmless and is replaced by the next recording of that name.
        catch (UnauthorizedAccessException) { }
    }
}
