namespace BetterShot.Ai;

internal sealed record AskClaudePreset(string Label, string Prompt);

/// <summary>Preset questions, worded to match KhingShot's "Ask about this capture" sheet.</summary>
internal static class AskClaudePresets
{
    internal static IReadOnlyList<AskClaudePreset> All { get; } =
    [
        new("Describe", "Describe what this screenshot shows, concisely. Mention the app or site if identifiable and anything notable."),
        new("Explain error", "This screenshot shows an error or unexpected behavior. Explain what the error means and suggest the most likely fixes, most probable first."),
        new("Draft bug report", "Draft a concise bug report based on this screenshot: a one-line title, what appears to happen, expected behavior, and any error text visible. Use Markdown."),
        new("Find UI issues", "Review this screenshot as a UI designer. List the most important visual, hierarchy, spacing, contrast, and copy issues, most important first, each with a concrete fix."),
        new("Extract text", "Extract the meaningful content of this screenshot as clean Markdown. Reproduce tables as Markdown tables, code as fenced code blocks, and lists as lists. Output only the extracted content."),
        new("Write alt text", "Write alt text for this screenshot: one or two plain sentences a screen reader user would find useful. Output only the alt text.")
    ];
}
