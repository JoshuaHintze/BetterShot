using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;

namespace BetterShot.Editing;

/// <summary>
/// Remembers each tool's last style and saved presets in a small JSON file. Unreadable or invalid
/// content falls back to defaults so a damaged file never blocks the editor.
/// </summary>
internal sealed class EditorStyleStore
{
    internal const int MaximumPresetsPerTool = 8;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static EditorStyleStore? _shared;
    private readonly string _path;
    private readonly Dictionary<EditorTool, ToolStyle> _defaults = [];
    private readonly Dictionary<EditorTool, List<ToolStyle>> _presets = [];
    private DispatcherTimer? _saveTimer;
    private bool _dirty;

    internal EditorStyleStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Load();
    }

    internal static EditorStyleStore Shared => _shared ??= new EditorStyleStore(DefaultPath);

    internal static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterShot", "editor-styles.json");

    internal string FilePath => _path;
    internal bool HasPendingSave => _saveTimer?.IsEnabled == true;

    internal ToolStyle Get(EditorTool tool) => _defaults.TryGetValue(tool, out ToolStyle? style) ? style : ToolStyle.DefaultFor(tool);

    internal void Set(EditorTool tool, ToolStyle style)
    {
        ToolStyle normalized = style.Normalized();
        if (Get(tool) == normalized) return;
        _defaults[tool] = normalized;
        ScheduleSave();
    }

    internal IReadOnlyList<ToolStyle> Presets(EditorTool tool) => _presets.TryGetValue(tool, out List<ToolStyle>? presets) ? presets : [];

    internal void AddPreset(EditorTool tool, ToolStyle style)
    {
        ToolStyle normalized = style.Normalized();
        if (!_presets.TryGetValue(tool, out List<ToolStyle>? presets)) _presets[tool] = presets = [];
        presets.Remove(normalized);
        presets.Add(normalized);
        if (presets.Count > MaximumPresetsPerTool) presets.RemoveAt(0);
        ScheduleSave();
    }

    internal void RemovePreset(EditorTool tool, ToolStyle style)
    {
        if (_presets.TryGetValue(tool, out List<ToolStyle>? presets) && presets.Remove(style)) ScheduleSave();
    }

    /// <summary>Writes pending changes now. Failures are ignored; styles are a convenience, not user data.</summary>
    internal void Flush()
    {
        _saveTimer?.Stop();
        if (!_dirty) return;
        try { WriteAtomically(); _dirty = false; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ScheduleSave()
    {
        _dirty = true;
        if (Dispatcher.FromThread(Thread.CurrentThread) is null) { Flush(); return; }
        _saveTimer ??= CreateTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        timer.Tick += (_, _) => Flush();
        return timer;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(_path));
            if (json.RootElement.ValueKind != JsonValueKind.Object) return;
            if (json.RootElement.TryGetProperty("Defaults", out JsonElement defaults) && defaults.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty entry in defaults.EnumerateObject())
                    if (TryParseTool(entry.Name, out EditorTool tool) && TryReadStyle(entry.Value, tool, out ToolStyle style)) _defaults[tool] = style;
            if (json.RootElement.TryGetProperty("Presets", out JsonElement presets) && presets.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty entry in presets.EnumerateObject())
                {
                    if (!TryParseTool(entry.Name, out EditorTool tool) || entry.Value.ValueKind != JsonValueKind.Array) continue;
                    var list = new List<ToolStyle>();
                    foreach (JsonElement item in entry.Value.EnumerateArray())
                        if (list.Count < MaximumPresetsPerTool && TryReadStyle(item, tool, out ToolStyle style) && !list.Contains(style)) list.Add(style);
                    if (list.Count > 0) _presets[tool] = list;
                }
        }
        catch (JsonException) { ResetLoaded(); }
        catch (IOException) { ResetLoaded(); }
        catch (UnauthorizedAccessException) { ResetLoaded(); }
    }

    private void ResetLoaded()
    {
        _defaults.Clear();
        _presets.Clear();
    }

    private static bool TryParseTool(string name, out EditorTool tool) =>
        Enum.TryParse(name, ignoreCase: false, out tool) && Enum.IsDefined(tool) && tool is not (EditorTool.Select or EditorTool.Crop);

    private static bool TryReadStyle(JsonElement element, EditorTool tool, out ToolStyle style)
    {
        style = ToolStyle.DefaultFor(tool);
        if (element.ValueKind != JsonValueKind.Object) return false;
        style = style with
        {
            Stroke = ReadColor(element, "Stroke") ?? style.Stroke,
            Width = ReadDouble(element, "Width") ?? style.Width,
            Fill = element.TryGetProperty("Fill", out JsonElement fill) && fill.ValueKind == JsonValueKind.String ? ParseColor(fill.GetString()) : null,
            CornerRadius = ReadDouble(element, "CornerRadius") ?? style.CornerRadius,
            Opacity = ReadDouble(element, "Opacity") ?? style.Opacity,
            Dashed = ReadBool(element, "Dashed") ?? style.Dashed,
            Shadow = ReadBool(element, "Shadow") ?? style.Shadow,
            FontSize = ReadDouble(element, "FontSize") ?? style.FontSize,
            TextBackground = ReadBool(element, "TextBackground") ?? style.TextBackground,
            Redaction = element.TryGetProperty("Redaction", out JsonElement redaction) && redaction.ValueKind == JsonValueKind.String &&
                Enum.TryParse(redaction.GetString(), out RedactionStyle mode) && Enum.IsDefined(mode) ? mode : style.Redaction,
            MagnifyZoom = ReadDouble(element, "MagnifyZoom") ?? style.MagnifyZoom,
            MagnifyDiameter = ReadDouble(element, "MagnifyDiameter") ?? style.MagnifyDiameter
        };
        style = style.Normalized();
        return true;
    }

    private static double? ReadDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;

    private static bool? ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static Color? ReadColor(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? ParseColor(value.GetString()) : null;

    internal static Color? ParseColor(string? value)
    {
        if (value is null || value.Length != 9 || value[0] != '#' || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb)) return null;
        return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    internal static string FormatColor(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private void WriteAtomically()
    {
        var root = new Dictionary<string, object>
        {
            ["Version"] = 1,
            ["Defaults"] = _defaults.ToDictionary(pair => pair.Key.ToString(), pair => ToJson(pair.Value)),
            ["Presets"] = _presets.Where(pair => pair.Value.Count > 0).ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.Select(ToJson).ToList())
        };
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, root, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static Dictionary<string, object?> ToJson(ToolStyle style) => new()
    {
        ["Stroke"] = FormatColor(style.Stroke),
        ["Width"] = style.Width,
        ["Fill"] = style.Fill is Color fill ? FormatColor(fill) : null,
        ["CornerRadius"] = style.CornerRadius,
        ["Opacity"] = style.Opacity,
        ["Dashed"] = style.Dashed,
        ["Shadow"] = style.Shadow,
        ["FontSize"] = style.FontSize,
        ["TextBackground"] = style.TextBackground,
        ["Redaction"] = style.Redaction.ToString(),
        ["MagnifyZoom"] = style.MagnifyZoom,
        ["MagnifyDiameter"] = style.MagnifyDiameter
    };
}
