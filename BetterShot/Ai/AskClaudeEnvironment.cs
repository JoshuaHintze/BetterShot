namespace BetterShot.Ai;

/// <summary>
/// Process-wide wiring for Ask Claude windows, which can be opened from thumbnails, the library, or the editor.
/// MainWindow points these at the saved settings and key file; diagnostics point them at fixtures and fakes.
/// </summary>
internal static class AskClaudeEnvironment
{
    internal static ApiKeyStore KeyStore { get; set; } = new(ApiKeyStore.DefaultPath);
    internal static Func<string> ModelProvider { get; set; } = () => ClaudeModels.DefaultId;
    internal static Func<AnthropicClient> ClientFactory { get; set; } = () => new AnthropicClient();
    /// <summary>Opens Settings at the Ask Claude section; null hides the "Open Settings" shortcut.</summary>
    internal static Action? OpenSettings { get; set; }
}
