using BetterShot.Recording;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BetterShot.RecordingEditor;

/// <summary>Moves marks, tracking paths, and click events onto the timeline of a trimmed movie.</summary>
internal static class TrimRemap
{
    /// <summary>The edit state for the trimmed movie: no cuts, marks shifted (or dropped when entirely cut).</summary>
    internal static RecordingEditState Apply(RecordingEditState state, double trimmedDuration)
    {
        EditTimeline timeline = state.Timeline;
        var marks = new List<VideoMark>();
        foreach (VideoMark mark in state.Marks)
            if (Remap(mark, timeline, trimmedDuration) is VideoMark moved) marks.Add(moved);
        return new RecordingEditState(EditTimeline.Create(trimmedDuration), marks, state.HideMarkup) { MutedTracks = state.MutedTracks, Camera = state.Camera };
    }

    internal static VideoMark? Remap(VideoMark mark, EditTimeline timeline, double trimmedDuration)
    {
        double? start = null, end = null;
        foreach (KeptRange range in timeline.KeptRanges)
        {
            double from = Math.Max(mark.Start, range.Start), to = Math.Min(mark.End, range.End);
            if (to - from <= 1e-6) continue;
            start ??= range.OutputStart + (from - range.Start);
            end = range.OutputStart + (to - range.Start);
        }
        if (start is not double outputStart || end is not double outputEnd) return null;
        TrackKeyframe[] keyframes = mark.Keyframes
            .Select(key => timeline.SourceToOutput(key.Time) is double time ? key with { Time = time } : null)
            .OfType<TrackKeyframe>()
            .ToArray();
        double? lostAt = mark.LostAt is double lost && timeline.NextPlayable(lost) is double playable ? timeline.SourceToOutput(playable) : null;
        return (mark with { Keyframes = keyframes, Tracked = mark.Tracked && keyframes.Length > 0, LostAt = lostAt })
            .WithTiming(outputStart, outputEnd - outputStart, trimmedDuration);
    }

    internal static IReadOnlyList<RecordingEvent> RemapEvents(IEnumerable<RecordingEvent> events, EditTimeline timeline) => events
        .Select(item => item.Kind == RecordingEvent.CameraKind ? null : timeline.SourceToOutput(item.Time) is double time ? item with { Time = Math.Round(time, 3) } : null)
        .OfType<RecordingEvent>()
        .ToArray();
}

/// <summary>
/// Decides which file is shared when a recording is dragged out: the original, or a rendered "marked copy" with cuts
/// and markup burned in. Recordings with a redaction always share their covered copy and are never shared unredacted.
/// Marked copies live in the history folder (MarkedCopies\&lt;hash of the movie path&gt;\&lt;same file name&gt;), so the
/// recipient sees the original name.
/// </summary>
internal static class RecordingSharing
{
    private static string _markedCopiesRoot = Path.Combine(Persistence.CaptureHistoryStore.DefaultRoot, "MarkedCopies");
    private static Func<bool> _keepMarkedCopy = () => true;

    internal static void Configure(string markedCopiesRoot, Func<bool> keepMarkedCopy)
    {
        _markedCopiesRoot = markedCopiesRoot;
        _keepMarkedCopy = keepMarkedCopy;
    }

    internal static bool KeepMarkedCopy => _keepMarkedCopy();

    internal static string MarkedCopyPath(string moviePath)
    {
        string full = Path.GetFullPath(moviePath);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..16];
        return Path.Combine(_markedCopiesRoot, key, Path.GetFileName(full));
    }

    /// <summary>A redaction always needs a covered copy; other edits only when "Keep a marked copy for sharing" is on.</summary>
    internal static bool NeedsMarkedCopy(RecordingEditState state, bool keepMarkedCopy, bool hasCamera = false) =>
        state.HasRedaction || hasCamera && state.Camera.Visible || keepMarkedCopy && state.HasEdits;

    internal static string SharingFingerprint(string moviePath, RecordingEditState state, CameraSource? camera = null)
    {
        string fingerprint = RecordingEditProject.Fingerprint(state);
        CameraSource source = camera ?? CameraSourceResolver.Resolve(moviePath);
        if (!source.IsAvailable || !state.Camera.Visible) return fingerprint;
        var file = new FileInfo(source.Path!);
        string changes = string.Join(';', source.VisibilityChanges.Select(change => $"{change.SourceTime:R}:{change.Visible}"));
        return $"{fingerprint}|camera-v2:{file.Length}:{file.LastWriteTimeUtc.Ticks}:{source.StartOffsetSeconds:R}:{changes}";
    }

    /// <summary>The file to share for a drag, or null with a reason when only an up-to-date covered copy may be shared.</summary>
    internal static (string? Path, string? Note) ResolveSharePath(string moviePath, string? cameraPath = null)
    {
        CameraSource camera = CameraSourceResolver.Resolve(moviePath, cameraPath);
        string sidecar = RecordingEditProject.SidecarPath(moviePath);
        if (!File.Exists(sidecar))
        {
            if (camera.IsAvailable)
                return (null, "This recording has a camera picture. Open it in the editor and choose Share to prepare its shareable copy.");
            return (moviePath, null);
        }
        LoadedEditProject project = RecordingEditProject.Load(moviePath, double.NaN);
        if (project.Warning is not null && project.PreservedMarks.Count == 0 && project.State.Marks.Count == 0 && !project.State.Timeline.HasCuts)
        {
            // Unreadable project: be safe if it might have held a redaction.
            return (null, "The saved edits of this recording could not be read, so it was not shared. Open it in the editor to check it.");
        }
        RecordingEditState state = project.State;
        bool unknownMarks = project.PreservedMarks.Count > 0;
        bool hasCamera = camera.IsAvailable;
        if (!NeedsMarkedCopy(state, KeepMarkedCopy, hasCamera) && !unknownMarks) return (moviePath, null);
        string copy = MarkedCopyPath(moviePath);
        bool current = File.Exists(copy) && project.MarkedCopyFingerprint == SharingFingerprint(moviePath, state, camera);
        if (current) return (copy, null);
        if (state.HasRedaction || unknownMarks)
            return (null, "This recording has a redaction, so only its covered copy can be shared. Open it in the editor and choose Share to update its covered copy.");
        if (hasCamera && state.Camera.Visible)
            return (null, "Open the recording and choose Share to prepare a copy with the current camera layout.");
        return (null, "Open the recording and choose Share to prepare a copy with your saved edits.");
    }

    internal static void DeleteMarkedCopy(string moviePath)
    {
        string copy = MarkedCopyPath(moviePath);
        try
        {
            if (File.Exists(copy)) File.Delete(copy);
            string? folder = Path.GetDirectoryName(copy);
            if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (IOException) { } // A stale cached copy is never shared once its fingerprint no longer matches.
        catch (UnauthorizedAccessException) { }
    }
}
