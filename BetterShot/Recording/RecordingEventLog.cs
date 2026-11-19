using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterShot.Recording;

/// <summary>
/// One click or keystroke during a recording. <see cref="Time"/> is seconds on the recording timeline (paused time
/// excluded). Clicks carry X/Y in output video pixels; keystrokes carry the combo text (e.g. "Ctrl+Shift+K").
/// Kind "camera" records webcam sidecar timing ("start" offset, "off"/"on" toggles) for the Phase 7 editor.
/// </summary>
internal sealed record RecordingEvent(
    [property: JsonPropertyName("time")] double Time,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("x")] double? X,
    [property: JsonPropertyName("y")] double? Y,
    [property: JsonPropertyName("text")] string? Text)
{
    internal const string ClickKind = "click";
    internal const string KeyKind = "key";
    internal const string CameraKind = "camera";
}

/// <summary>Thread-safe collector for click/keystroke events, written beside the movie as a hidden JSON sidecar.</summary>
internal sealed class RecordingEventLog
{
    internal const string SidecarSuffix = ".bettershot-events";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _gate = new();
    private readonly List<RecordingEvent> _events = [];

    internal int Count { get { lock (_gate) return _events.Count; } }

    internal void AddClick(TimeSpan time, double x, double y) =>
        Add(new RecordingEvent(Round(time), RecordingEvent.ClickKind, Math.Round(x), Math.Round(y), null));

    internal void AddKey(TimeSpan time, string text) =>
        Add(new RecordingEvent(Round(time), RecordingEvent.KeyKind, null, null, text));

    /// <summary>Camera timing; may be negative ("start" before the screen timeline began).</summary>
    internal void AddCamera(TimeSpan time, string text) =>
        Add(new RecordingEvent(Math.Round(time.TotalSeconds, 3), RecordingEvent.CameraKind, null, null, text));

    internal IReadOnlyList<RecordingEvent> Snapshot()
    {
        lock (_gate) return _events.OrderBy(item => item.Time).ToArray();
    }

    /// <summary>".&lt;movie file name&gt;.bettershot-events" in the movie's folder.</summary>
    internal static string SidecarPath(string moviePath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(moviePath)) ?? string.Empty;
        return Path.Combine(folder, "." + Path.GetFileName(moviePath) + SidecarSuffix);
    }

    internal static string Serialize(IEnumerable<RecordingEvent> events) => JsonSerializer.Serialize(events.ToArray(), JsonOptions);

    internal static IReadOnlyList<RecordingEvent> Deserialize(string json) =>
        JsonSerializer.Deserialize<RecordingEvent[]>(json, JsonOptions) ?? [];

    /// <summary>Writes the sidecar (also when empty, so the editor can tell "no events" from "not recorded") and hides it.</summary>
    internal string WriteSidecar(string moviePath)
    {
        string path = SidecarPath(moviePath);
        HiddenSidecarFile.WriteAllText(path, Serialize(Snapshot()));
        return path;
    }

    private void Add(RecordingEvent item)
    {
        lock (_gate) _events.Add(item);
    }

    private static double Round(TimeSpan time) => Math.Round(Math.Max(0, time.TotalSeconds), 3);
}
