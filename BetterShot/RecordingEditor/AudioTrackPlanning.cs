using BetterShot.Recording;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace BetterShot.RecordingEditor;

/// <summary>Audio tracks muted in the editor. <see cref="Soundtrack"/> is the single track of recordings made before separate tracks.</summary>
[Flags]
internal enum MutedAudioTracks { None = 0, System = 1, Microphone = 2, Soundtrack = 4 }

/// <summary>How the soundtrack of a preview or export is produced.</summary>
internal enum AudioOutputMode
{
    /// <summary>The movie's own mixed soundtrack, untouched.</summary>
    Original,
    /// <summary>The unmuted separate tracks, mixed again from their WAV sidecars.</summary>
    Remix,
    /// <summary>No audio at all.</summary>
    Silent
}

internal sealed record AudioOutputPlan(AudioOutputMode Mode, IReadOnlyList<AudioTrackKind> RemixTracks);

/// <summary>Pure decisions about per-track muting: which lanes a recording shows, and what a preview or export plays.</summary>
internal static class AudioTrackPlanning
{
    internal const string SystemKey = "system", MicrophoneKey = "mic", SoundtrackKey = "audio";

    internal static MutedAudioTracks Flag(AudioTrackKind kind) => kind == AudioTrackKind.Microphone ? MutedAudioTracks.Microphone : MutedAudioTracks.System;

    internal static MutedAudioTracks FlagForKey(string key) => key switch
    {
        SystemKey => MutedAudioTracks.System,
        MicrophoneKey => MutedAudioTracks.Microphone,
        SoundtrackKey => MutedAudioTracks.Soundtrack,
        _ => MutedAudioTracks.None
    };

    internal static string Key(AudioTrackKind kind) => kind == AudioTrackKind.Microphone ? MicrophoneKey : SystemKey;

    /// <summary>
    /// Everything audible is the original mix when nothing available is muted; silence when everything is; otherwise
    /// the unmuted separate tracks are mixed again. Without separate tracks only the whole soundtrack can be muted.
    /// </summary>
    internal static AudioOutputPlan Plan(MutedAudioTracks muted, IReadOnlyList<AudioTrackKind> separateTracks, bool movieHasAudio)
    {
        if (!movieHasAudio && separateTracks.Count == 0) return new AudioOutputPlan(AudioOutputMode.Silent, []);
        if (separateTracks.Count == 0)
            return new AudioOutputPlan(muted.HasFlag(MutedAudioTracks.Soundtrack) ? AudioOutputMode.Silent : AudioOutputMode.Original, []);
        AudioTrackKind[] audible = separateTracks.Where(kind => !muted.HasFlag(Flag(kind))).ToArray();
        if (audible.Length == 0) return new AudioOutputPlan(AudioOutputMode.Silent, []);
        if (audible.Length == separateTracks.Count && movieHasAudio) return new AudioOutputPlan(AudioOutputMode.Original, []);
        return new AudioOutputPlan(AudioOutputMode.Remix, audible);
    }

    internal static IReadOnlyList<string>? ToKeys(MutedAudioTracks muted)
    {
        var keys = new List<string>();
        if (muted.HasFlag(MutedAudioTracks.System)) keys.Add(SystemKey);
        if (muted.HasFlag(MutedAudioTracks.Microphone)) keys.Add(MicrophoneKey);
        if (muted.HasFlag(MutedAudioTracks.Soundtrack)) keys.Add(SoundtrackKey);
        return keys.Count == 0 ? null : keys;
    }

    /// <summary>Unknown keys (from a newer BetterShot) are ignored.</summary>
    internal static MutedAudioTracks FromKeys(IEnumerable<string>? keys) =>
        (keys ?? []).Aggregate(MutedAudioTracks.None, (muted, key) => muted | FlagForKey(key ?? string.Empty));
}

/// <summary>
/// Mixes (sums, with clipping) 48 kHz stereo 16-bit WAV tracks for just the kept stretches into one WAV on the output
/// timeline. Used to re-mix exports without muted tracks and to trim the separate tracks along with a trimmed movie.
/// Tracks shorter than the range are padded with silence.
/// </summary>
internal static class AudioRemixer
{
    private const int BlockFrames = 4800;

    internal static void WriteKeptMix(IReadOnlyList<string> sourcePaths, IReadOnlyList<KeptRange> ranges, string destinationPath, CancellationToken cancellation = default)
    {
        if (sourcePaths.Count == 0) throw new ArgumentException("At least one track is needed.", nameof(sourcePaths));
        var streams = new List<FileStream>();
        try
        {
            var layouts = new List<WaveLayout>();
            foreach (string path in sourcePaths)
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                streams.Add(stream);
                WaveLayout layout = WaveLayout.Parse(stream);
                if (layout.SampleRate != RecordingAudioTracks.SampleRate || layout.Channels != RecordingAudioTracks.Channels)
                    throw new InvalidDataException("A separate audio track is not 48 kHz stereo.");
                layouts.Add(layout);
            }
            using var writer = new PcmWaveWriter(destinationPath, RecordingAudioTracks.SampleRate, RecordingAudioTracks.Channels);
            byte[] input = new byte[BlockFrames * 4];
            int[] sum = new int[BlockFrames * 2];
            byte[] output = new byte[BlockFrames * 4];
            foreach (KeptRange range in ranges)
            {
                long first = (long)Math.Round(range.Start * RecordingAudioTracks.SampleRate), last = (long)Math.Round(range.End * RecordingAudioTracks.SampleRate);
                for (long frame = first; frame < last; frame += BlockFrames)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int frames = (int)Math.Min(BlockFrames, last - frame);
                    Array.Clear(sum, 0, frames * 2);
                    for (int track = 0; track < streams.Count; track++) AddTrack(streams[track], layouts[track], frame, frames, input, sum);
                    for (int sample = 0; sample < frames * 2; sample++)
                        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(sample * 2), (short)Math.Clamp(sum[sample], short.MinValue, short.MaxValue));
                    writer.WritePcm(output.AsSpan(0, frames * 4));
                }
            }
            writer.Complete();
        }
        finally
        {
            foreach (FileStream stream in streams) stream.Dispose();
        }
    }

    private static void AddTrack(FileStream stream, WaveLayout layout, long frame, int frames, byte[] buffer, int[] sum)
    {
        long available = layout.DataLength / layout.BytesPerFrame - frame;
        int readable = (int)Math.Clamp(available, 0, frames);
        if (readable == 0) return;
        stream.Position = layout.DataOffset + frame * layout.BytesPerFrame;
        stream.ReadExactly(buffer, 0, readable * 4);
        for (int sample = 0; sample < readable * 2; sample++) sum[sample] += BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(sample * 2));
    }

    /// <summary>
    /// Writes each separate track of <paramref name="sourceMovie"/> cut to <paramref name="ranges"/> beside
    /// <paramref name="targetMovie"/> (replacing any there). Best effort: a track that fails is removed so it can never
    /// disagree with the movie.
    /// </summary>
    internal static void TrimSidecars(string sourceMovie, IReadOnlyList<KeptRange> ranges, string targetMovie)
    {
        foreach (AudioTrackKind kind in RecordingAudioTracks.Existing(sourceMovie))
        {
            string source = RecordingAudioTracks.SidecarPath(sourceMovie, kind), target = RecordingAudioTracks.SidecarPath(targetMovie, kind);
            string temporary = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                WriteKeptMix([source], ranges, temporary);
                RecordingAudioTracks.ReplaceFile(temporary, target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                RecordingAudioTracks.TryDelete(temporary);
                RecordingAudioTracks.TryDelete(target);
            }
        }
    }
}
