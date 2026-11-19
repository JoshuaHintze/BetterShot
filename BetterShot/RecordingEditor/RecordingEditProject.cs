using BetterShot.Persistence;
using BetterShot.Recording;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterShot.RecordingEditor;

/// <summary>Editor view choices stored with the project but not part of undo or the export fingerprint.</summary>
internal sealed record RecordingEditViewOptions(bool MarksOnOwnRows = true);

/// <summary>A project read from disk: the edit state plus what is needed to write it back faithfully.</summary>
internal sealed record LoadedEditProject(
    RecordingEditState State,
    RecordingEditViewOptions View,
    string? MarkedCopyFingerprint,
    IReadOnlyList<JsonElement> PreservedMarks,
    Dictionary<string, JsonElement>? UnknownFields,
    string? Warning);

/// <summary>
/// The hidden ".&lt;movie&gt;.mp4.bettershot-edit" sidecar: a versioned JSON document holding cuts, in/out points, marks
/// (reusing the capture library's annotation format), tracking keyframes, and the fingerprint of the last marked copy.
/// Unknown fields and marks this build cannot read are carried through every rewrite, so newer files stay intact.
/// </summary>
internal static class RecordingEditProject
{
    internal const int CurrentVersion = 1;
    internal const string SidecarSuffix = ".bettershot-edit";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    // Marks embed the library's annotation format, whose required properties must be written even when null.
    private static readonly JsonSerializerOptions MarkJsonOptions = new();

    internal static string SidecarPath(string moviePath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(moviePath)) ?? string.Empty;
        return Path.Combine(folder, "." + Path.GetFileName(moviePath) + SidecarSuffix);
    }

    internal static LoadedEditProject Empty(double duration) =>
        new(RecordingEditState.Create(duration), new RecordingEditViewOptions(), null, [], null, null);

    /// <summary>Reads the sidecar beside <paramref name="moviePath"/>; a missing or damaged file gives a fresh project.</summary>
    internal static LoadedEditProject Load(string moviePath, double duration)
    {
        string path = SidecarPath(moviePath);
        if (!File.Exists(path)) return Empty(duration);
        try { return Deserialize(File.ReadAllText(path), duration); }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return Empty(duration) with { Warning = "The saved edits could not be read, so the editor started fresh. The movie is unchanged." };
        }
    }

    internal static void Save(string moviePath, RecordingEditState state, RecordingEditViewOptions view, string? markedCopyFingerprint,
        IReadOnlyList<JsonElement> preservedMarks, Dictionary<string, JsonElement>? unknownFields)
    {
        string path = SidecarPath(moviePath);
        string json = Serialize(state, view, markedCopyFingerprint, preservedMarks, unknownFields);
        HiddenSidecarFile.WriteAllText(path, json);
    }

    internal static string Serialize(RecordingEditState state, RecordingEditViewOptions view, string? markedCopyFingerprint,
        IReadOnlyList<JsonElement> preservedMarks, Dictionary<string, JsonElement>? unknownFields)
    {
        EditProjectDto dto = ToDto(state);
        dto.MarksOnOwnRows = view.MarksOnOwnRows;
        dto.MarkedCopyFingerprint = markedCopyFingerprint;
        dto.Marks.AddRange(preservedMarks.Select(element => element.Clone()));
        dto.AdditionalFields = unknownFields;
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    internal static LoadedEditProject Deserialize(string json, double duration)
    {
        EditProjectDto dto = JsonSerializer.Deserialize<EditProjectDto>(json, JsonOptions) ?? throw new InvalidDataException("The edit project is empty.");
        double safeDuration = double.IsFinite(duration) && duration > 0 ? duration : dto.Duration;
        bool sameMovie = Math.Abs(dto.Duration - safeDuration) <= 0.25;
        EditTimeline timeline = sameMovie
            ? EditTimeline.Restore(safeDuration, (dto.Segments ?? []).Select(segment => new TimelineSegment(segment.Start, segment.End, segment.Kept)), dto.In, dto.Out)
            : EditTimeline.Create(safeDuration);
        var marks = new List<VideoMark>();
        var preserved = new List<JsonElement>();
        foreach (JsonElement element in dto.Marks ?? [])
        {
            if (TryReadMark(element, safeDuration) is VideoMark mark) marks.Add(mark);
            else preserved.Add(element.Clone());
        }
        string? warning = null;
        if (!sameMovie) warning = "The movie's length changed since it was edited, so its cuts were reset. Marks were kept.";
        if (preserved.Count > 0) warning = $"{preserved.Count} mark{(preserved.Count == 1 ? " was" : "s were")} made by a newer BetterShot and will be kept but not shown.";
        var state = new RecordingEditState(timeline, marks, dto.HideMarkup ?? false);
        return new LoadedEditProject(state, new RecordingEditViewOptions(dto.MarksOnOwnRows ?? true), sameMovie ? dto.MarkedCopyFingerprint : null,
            preserved, dto.AdditionalFields, warning);
    }

    /// <summary>
    /// Identifies what an export of this state would contain (cuts, in/out, and visible marks). A marked copy is current
    /// when its stored fingerprint equals this one.
    /// </summary>
    internal static string Fingerprint(RecordingEditState state)
    {
        EditProjectDto dto = ToDto(state with { Marks = state.VisibleMarks.ToArray(), HideMarkup = false });
        string canonical = JsonSerializer.Serialize(dto, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
    }

    private static EditProjectDto ToDto(RecordingEditState state) => new()
    {
        Version = CurrentVersion,
        Duration = state.Timeline.Duration,
        Segments = state.Timeline.Segments.Select(segment => new SegmentDto { Start = segment.Start, End = segment.End, Kept = segment.Kept }).ToList(),
        In = state.Timeline.InPoint,
        Out = state.Timeline.OutPoint,
        HideMarkup = state.HideMarkup ? true : null,
        Marks = state.Marks.Select(mark => JsonSerializer.SerializeToElement(ToDto(mark), MarkJsonOptions)).ToList()
    };

    private static MarkDto ToDto(VideoMark mark) => new()
    {
        Id = mark.Id,
        Start = mark.Start,
        Duration = mark.Duration,
        Shape = CaptureHistoryStore.ToDto(mark.Shape),
        Tracked = mark.Tracked ? true : null,
        Keyframes = mark.Keyframes.Count == 0 ? null : mark.Keyframes.Select(key => new KeyframeDto { T = key.Time, X = key.Bounds.X, Y = key.Bounds.Y, W = key.Bounds.Width, H = key.Bounds.Height }).ToList(),
        LostAt = mark.LostAt,
        WhenLost = mark.WhenLost == LostFieldPolicy.BlurWholePicture ? "blur" : null
    };

    private static VideoMark? TryReadMark(JsonElement element, double duration)
    {
        try
        {
            MarkDto? dto = element.Deserialize<MarkDto>(MarkJsonOptions);
            if (dto?.Shape is null || !double.IsFinite(dto.Start) || !double.IsFinite(dto.Duration)) return null;
            var keyframes = (dto.Keyframes ?? [])
                .Where(key => double.IsFinite(key.T) && double.IsFinite(key.X) && double.IsFinite(key.Y) && double.IsFinite(key.W) && double.IsFinite(key.H) && key.W >= 0 && key.H >= 0)
                .OrderBy(key => key.T)
                .Select(key => new TrackKeyframe(key.T, new Rect(key.X, key.Y, key.W, key.H)))
                .ToArray();
            var mark = new VideoMark(dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id, CaptureHistoryStore.FromDto(dto.Shape), dto.Start, dto.Duration)
            {
                Tracked = dto.Tracked == true && keyframes.Length > 0,
                Keyframes = keyframes,
                LostAt = dto.LostAt is double lost && double.IsFinite(lost) ? lost : null,
                WhenLost = string.Equals(dto.WhenLost, "blur", StringComparison.OrdinalIgnoreCase) ? LostFieldPolicy.BlurWholePicture : LostFieldPolicy.StopFollowing
            };
            return mark.WithTiming(mark.Start, mark.Duration, duration);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
        {
            return null; // Kept verbatim and written back; see PreservedMarks.
        }
    }

    private sealed class EditProjectDto
    {
        [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
        [JsonPropertyName("duration")] public double Duration { get; set; }
        [JsonPropertyName("segments")] public List<SegmentDto>? Segments { get; set; }
        [JsonPropertyName("in")] public double? In { get; set; }
        [JsonPropertyName("out")] public double? Out { get; set; }
        [JsonPropertyName("marks")] public List<JsonElement> Marks { get; set; } = [];
        [JsonPropertyName("hideMarkup")] public bool? HideMarkup { get; set; }
        [JsonPropertyName("marksOnOwnRows")] public bool? MarksOnOwnRows { get; set; }
        [JsonPropertyName("markedCopyFingerprint")] public string? MarkedCopyFingerprint { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalFields { get; set; }
    }

    private sealed class SegmentDto
    {
        [JsonPropertyName("start")] public double Start { get; set; }
        [JsonPropertyName("end")] public double End { get; set; }
        [JsonPropertyName("kept")] public bool Kept { get; set; } = true;
    }

    private sealed class MarkDto
    {
        [JsonPropertyName("id")] public Guid Id { get; set; }
        [JsonPropertyName("start")] public double Start { get; set; }
        [JsonPropertyName("duration")] public double Duration { get; set; } = VideoMark.DefaultDuration;
        [JsonPropertyName("shape")] public AnnotationDto? Shape { get; set; }
        [JsonPropertyName("tracked")] public bool? Tracked { get; set; }
        [JsonPropertyName("keyframes")] public List<KeyframeDto>? Keyframes { get; set; }
        [JsonPropertyName("lostAt")] public double? LostAt { get; set; }
        [JsonPropertyName("whenLost")] public string? WhenLost { get; set; }
    }

    private sealed class KeyframeDto
    {
        [JsonPropertyName("t")] public double T { get; set; }
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }
        [JsonPropertyName("w")] public double W { get; set; }
        [JsonPropertyName("h")] public double H { get; set; }
    }
}
