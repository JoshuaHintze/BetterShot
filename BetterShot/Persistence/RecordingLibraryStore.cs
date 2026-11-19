using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterShot.Persistence;

/// <summary>A finished recording shown in the library. The movie itself lives in the user's save folder.</summary>
internal sealed record RecordingEntry(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("path")] string MoviePath,
    [property: JsonPropertyName("cameraPath")] string? CameraPath,
    [property: JsonPropertyName("createdUtc")] DateTime CreatedUtc,
    [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

/// <summary>
/// Index of recordings (recordings.json beside the capture history). Entries whose movie was moved or deleted outside
/// BetterShot are dropped on load. Also owns the folder that holds in-progress recordings until they are finalized.
/// </summary>
internal sealed class RecordingLibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _indexPath;
    private readonly List<RecordingEntry> _entries = [];

    internal RecordingLibraryStore(string historyRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyRoot);
        string root = Path.GetFullPath(historyRoot);
        Directory.CreateDirectory(root);
        _indexPath = Path.Combine(root, "recordings.json");
        InProgressFolder = Path.Combine(root, "RecordingsInProgress");
    }

    /// <summary>Temporary home of a movie while it is being recorded; leftovers indicate an interrupted recording.</summary>
    internal string InProgressFolder { get; }

    internal IReadOnlyList<RecordingEntry> Entries => _entries;

    internal IReadOnlyList<RecordingEntry> Load()
    {
        _entries.Clear();
        if (!File.Exists(_indexPath)) return _entries;
        RecordingEntry[] stored;
        try { stored = JsonSerializer.Deserialize<RecordingEntry[]>(File.ReadAllText(_indexPath), JsonOptions) ?? []; }
        catch (JsonException) { stored = []; } // A damaged index only loses the list; the movies are untouched.
        _entries.AddRange(stored.Where(entry => !string.IsNullOrWhiteSpace(entry.MoviePath) && File.Exists(entry.MoviePath)).OrderByDescending(entry => entry.CreatedUtc));
        if (_entries.Count != stored.Length) Save();
        return _entries;
    }

    internal void Add(RecordingEntry entry)
    {
        _entries.RemoveAll(existing => existing.Id == entry.Id);
        _entries.Insert(0, entry);
        Save();
    }

    /// <summary>Replaces an entry in place (for example, after its movie was trimmed), keeping the list order.</summary>
    internal void Update(RecordingEntry entry)
    {
        int index = _entries.FindIndex(existing => existing.Id == entry.Id);
        if (index < 0) return;
        _entries[index] = entry;
        Save();
    }

    internal void Remove(Guid id)
    {
        if (_entries.RemoveAll(entry => entry.Id == id) > 0) Save();
    }

    /// <summary>Movies left in <see cref="InProgressFolder"/> by a crash or forced exit.</summary>
    internal IReadOnlyList<string> FindInterruptedRecordings()
    {
        if (!Directory.Exists(InProgressFolder)) return [];
        return Directory.EnumerateFiles(InProgressFolder, "*.mp4", SearchOption.TopDirectoryOnly).ToArray();
    }

    private void Save()
    {
        string temporary = _indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries, JsonOptions));
            File.Move(temporary, _indexPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
