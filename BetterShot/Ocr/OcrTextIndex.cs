using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterShot.Ocr;

/// <summary>
/// Persisted text recognized in library captures, keyed by capture id. Each entry stores the stamp of the
/// capture state it was read from, so edits that change visible text (crop, redactions) trigger a re-read.
/// Writes are atomic; a missing or corrupt file simply starts an empty index. Thread-safe.
/// </summary>
internal sealed class OcrTextIndex
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _sync = new();
    private readonly Dictionary<Guid, OcrIndexEntryDto> _entries = [];
    private bool _dirty;

    internal OcrTextIndex(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        Load();
    }

    internal static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterShot", "ocr-index.json");

    internal string Path { get; }

    /// <summary>True when the existing file could not be read and was ignored (it is replaced on the next save).</summary>
    internal bool RecoveredFromCorruption { get; private set; }

    internal int Count { get { lock (_sync) return _entries.Count; } }

    internal bool IsCurrent(Guid id, string stamp)
    {
        lock (_sync) return _entries.TryGetValue(id, out OcrIndexEntryDto? entry) && entry.Stamp == stamp;
    }

    /// <summary>Indexed text for the capture, or null if it has not been read yet.</summary>
    internal string? TextFor(Guid id)
    {
        lock (_sync) return _entries.TryGetValue(id, out OcrIndexEntryDto? entry) ? entry.Text : null;
    }

    internal void Set(Guid id, string stamp, string text)
    {
        lock (_sync)
        {
            _entries[id] = new OcrIndexEntryDto { Stamp = stamp, Text = text ?? string.Empty, IndexedUtc = DateTime.UtcNow };
            _dirty = true;
        }
    }

    internal bool Remove(Guid id)
    {
        lock (_sync)
        {
            if (!_entries.Remove(id)) return false;
            _dirty = true;
            return true;
        }
    }

    /// <summary>Removes entries for captures that no longer exist (deleted or expired while BetterShot was closed).</summary>
    internal int Prune(IEnumerable<Guid> existing)
    {
        var keep = existing.ToHashSet();
        lock (_sync)
        {
            Guid[] stale = _entries.Keys.Where(id => !keep.Contains(id)).ToArray();
            foreach (Guid id in stale) _entries.Remove(id);
            if (stale.Length > 0) _dirty = true;
            return stale.Length;
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            if (_entries.Count == 0 && !File.Exists(Path)) return;
            _entries.Clear();
            _dirty = true;
        }
    }

    /// <summary>Writes pending changes atomically. An empty index deletes the file.</summary>
    internal void Save()
    {
        OcrIndexDto snapshot;
        lock (_sync)
        {
            if (!_dirty) return;
            snapshot = new OcrIndexDto { Version = CurrentVersion, Entries = _entries.ToDictionary(pair => pair.Key.ToString("D"), pair => (OcrIndexEntryDto?)pair.Value) };
            _dirty = false;
        }
        try
        {
            if (snapshot.Entries.Count == 0) { if (File.Exists(Path)) File.Delete(Path); return; }
            WriteAtomically(snapshot);
        }
        catch
        {
            lock (_sync) _dirty = true;
            throw;
        }
    }

    private void WriteAtomically(OcrIndexDto snapshot)
    {
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, snapshot, JsonOptions);
            File.Move(temporary, Path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Load()
    {
        if (!File.Exists(Path)) return;
        try
        {
            OcrIndexDto? dto = JsonSerializer.Deserialize<OcrIndexDto>(File.ReadAllText(Path), JsonOptions);
            if (dto?.Entries is null || dto.Version > CurrentVersion) { MarkCorrupt(); return; }
            foreach ((string key, OcrIndexEntryDto? entry) in dto.Entries)
            {
                if (!Guid.TryParse(key, out Guid id) || entry is null || string.IsNullOrEmpty(entry.Stamp)) { _dirty = true; continue; }
                entry.Text ??= string.Empty;
                _entries[id] = entry;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MarkCorrupt();
        }
    }

    private void MarkCorrupt()
    {
        _entries.Clear();
        RecoveredFromCorruption = true;
        _dirty = true;
    }
}

internal sealed class OcrIndexDto
{
    public int Version { get; set; } = 1;
    public Dictionary<string, OcrIndexEntryDto?>? Entries { get; set; } = [];
}

internal sealed class OcrIndexEntryDto
{
    [JsonRequired] public string Stamp { get; set; } = string.Empty;
    public string? Text { get; set; } = string.Empty;
    public DateTime IndexedUtc { get; set; }
}
