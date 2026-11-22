using BetterShot.Persistence;
using BetterShot.Recording;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BetterShot.RecordingEditor;

internal enum CameraShape { Circle, Square }

internal sealed record CameraPlacement(double X, double Y, double Size)
{
    internal static CameraPlacement Default { get; } = new(0.97, 0.96, 0.24);
    internal CameraPlacement Clamp()
    {
        double size = double.IsFinite(Size) ? Math.Clamp(Size, 0.08, 0.60) : Default.Size;
        return new(double.IsFinite(X) ? Math.Clamp(X, 0, 1) : Default.X,
            double.IsFinite(Y) ? Math.Clamp(Y, 0, 1) : Default.Y, size);
    }
    internal Rect ToPixels(double frameWidth, double frameHeight)
    {
        if (!double.IsFinite(frameWidth) || !double.IsFinite(frameHeight) || frameWidth <= 0 || frameHeight <= 0) return Rect.Empty;
        CameraPlacement p = Clamp();
        double side = p.Size * Math.Min(frameWidth, frameHeight);
        return new Rect(p.X * (frameWidth - side), p.Y * (frameHeight - side), side, side);
    }
}

internal sealed record CameraEdit(bool Visible, CameraShape Shape, CameraPlacement Placement)
{
    internal static CameraEdit Default { get; } = new(true, CameraShape.Circle, CameraPlacement.Default);
}

internal sealed record CameraVisibilityChange(double SourceTime, bool Visible);

internal sealed record CameraSource(string? Path, double StartOffsetSeconds, IReadOnlyList<CameraVisibilityChange> VisibilityChanges)
{
    internal const double EndFrameToleranceSeconds = 0.075;
    /// <summary>True when resolution found a sidecar. Existence is intentionally checked once by the resolver, not per frame.</summary>
    internal bool IsAvailable => Path is not null;
    internal double CameraTimeAt(double sourceTime) => Math.Max(0, sourceTime - StartOffsetSeconds);
    /// <summary>Maps and clamps a source time, holding the final camera frame for a short encoder-tail mismatch.</summary>
    internal double? CameraTimeAt(double sourceTime, double cameraDuration)
    {
        double time = CameraTimeAt(sourceTime);
        if (!double.IsFinite(cameraDuration) || cameraDuration <= 0 || time > cameraDuration + EndFrameToleranceSeconds) return null;
        return Math.Min(time, cameraDuration);
    }
    internal bool IsVisibleAt(double sourceTime, CameraEdit edit)
    {
        if (!IsAvailable || !edit.Visible || sourceTime + 1e-6 < StartOffsetSeconds) return false;
        bool visible = true;
        foreach (CameraVisibilityChange change in VisibilityChanges)
        {
            if (change.SourceTime > sourceTime + 1e-6) break;
            visible = change.Visible;
        }
        return visible;
    }
    internal Rect OverlayRectAt(double sourceTime, CameraEdit edit, double frameWidth, double frameHeight) =>
        IsVisibleAt(sourceTime, edit) ? edit.Placement.ToPixels(frameWidth, frameHeight) : Rect.Empty;
    internal static CameraSource Resolve(RecordingEntry entry, IReadOnlyList<RecordingEvent>? events = null) =>
        CameraSourceResolver.Resolve(entry.MoviePath, entry.CameraPath, events);
}

internal static partial class CameraSourceResolver
{
    internal static CameraSource Resolve(string moviePath, string? explicitCameraPath = null, IReadOnlyList<RecordingEvent>? events = null)
    {
        IReadOnlyList<RecordingEvent> timing = events ?? ReadEvents(moviePath);
        string? path = ResolvePath(moviePath, explicitCameraPath, timing);
        double start = timing.Where(item => IsCamera(item, "start") && double.IsFinite(item.Time)).Select(item => item.Time).DefaultIfEmpty(0).First();
        CameraVisibilityChange[] changes = timing.Where(item => item.Kind == RecordingEvent.CameraKind && (IsText(item, "on") || IsText(item, "off")))
            .Where(item => double.IsFinite(item.Time)).OrderBy(item => item.Time)
            .Select(item => new CameraVisibilityChange(item.Time, IsText(item, "on"))).ToArray();
        return new(path, start, changes);
    }

    internal static string? ResolvePath(string moviePath, string? explicitCameraPath = null, IReadOnlyList<RecordingEvent>? events = null)
    {
        if (File.Exists(SuppressionPath(moviePath))) return null;
        if (!string.IsNullOrWhiteSpace(explicitCameraPath) && File.Exists(explicitCameraPath)) return explicitCameraPath;
        string folder = Path.GetDirectoryName(moviePath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(moviePath);
        string canonical = Path.Combine(folder, stem + "-camera.mp4");
        if (File.Exists(canonical)) return canonical;
        // Older libraries sometimes lost CameraPath. Only try the precise matching index when camera timing proves this was a camera recording.
        if (events?.Any(item => item.Kind == RecordingEvent.CameraKind) == true)
        {
            Match indexed = IndexedStem().Match(stem);
            if (indexed.Success)
            {
                string legacy = Path.Combine(folder, indexed.Groups["root"].Value + "-camera (" + indexed.Groups["number"].Value + ").mp4");
                if (File.Exists(legacy)) return legacy;
            }
        }
        return null;
    }

    internal static string SuppressionPath(string moviePath) => RecordingEditProject.SidecarPath(moviePath) + ".camera-suppressed";
    internal static void Suppress(string moviePath) => HiddenSidecarFile.WriteAllText(SuppressionPath(moviePath), "camera overlay already rendered");
    internal static void Unsuppress(string moviePath) { string path = SuppressionPath(moviePath); if (File.Exists(path)) File.Delete(path); }

    private static IReadOnlyList<RecordingEvent> ReadEvents(string moviePath)
    {
        string path = RecordingEventLog.SidecarPath(moviePath);
        try { return File.Exists(path) ? RecordingEventLog.Deserialize(File.ReadAllText(path)) : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    private static bool IsCamera(RecordingEvent item, string text) => item.Kind == RecordingEvent.CameraKind && IsText(item, text);
    private static bool IsText(RecordingEvent item, string text) => string.Equals(item.Text, text, StringComparison.OrdinalIgnoreCase);
    [GeneratedRegex(@"^(?<root>.+) \((?<number>\d+)\)$", RegexOptions.CultureInvariant)] private static partial Regex IndexedStem();
}
