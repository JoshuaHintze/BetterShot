namespace BetterShot.Ai;

internal sealed record ClaudeModel(string Id, string Label);

/// <summary>The models offered in Settings > Ask Claude. The first entry is the default.</summary>
internal static class ClaudeModels
{
    internal const string DefaultId = "claude-sonnet-5-5";

    internal static IReadOnlyList<ClaudeModel> All { get; } =
    [
        new(DefaultId, "Claude Sonnet 5.5 (recommended)"),
        new("claude-opus-5-5", "Claude Opus 5.5"),
        new("claude-haiku-5-5", "Claude Haiku 5.5")
    ];

    /// <summary>Unknown or empty ids (hand-edited files, retired models) fall back to the default.</summary>
    internal static string Normalize(string? id)
    {
        string trimmed = id?.Trim() ?? string.Empty;
        return All.Any(model => model.Id == trimmed) ? trimmed : DefaultId;
    }

    internal static string LabelFor(string id) =>
        All.FirstOrDefault(model => model.Id == id)?.Label ?? id;
}
