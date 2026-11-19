using System.Text.Json;

namespace BetterShot.Ai;

/// <summary>A failure already phrased for the Ask Claude window.</summary>
internal sealed class AskClaudeException(string message, bool retryable = false, bool needsApiKey = false) : Exception(message)
{
    /// <summary>Busy, rate-limited, or timed out: the same request may work shortly.</summary>
    internal bool Retryable { get; } = retryable;
    /// <summary>The fix is in Settings > Ask Claude (no key, or the key was refused).</summary>
    internal bool NeedsApiKey { get; } = needsApiKey;
}

/// <summary>Maps HTTP statuses, API error bodies, and stream error events to short, readable messages.</summary>
internal static class AskClaudeErrors
{
    internal const string NoApiKey = "No API key yet. Add one in Settings, under Ask Claude.";
    internal const string NoResponse = "No response from the API.";
    internal const string NoText = "The reply contained no text.";
    internal const string TryAgain = "Try again in a moment.";
    internal const string TimedOut = "Claude took too long to respond. " + TryAgain;
    private const int MaximumDetailLength = 300;

    internal static AskClaudeException MissingKey() => new(NoApiKey, needsApiKey: true);

    internal static AskClaudeException FromStatus(int status, string? body)
    {
        (string? type, string? detail) = ParseErrorBody(body);
        return Describe(type, detail, status);
    }

    /// <summary>An <c>event: error</c> sent mid-stream, for example overloaded_error after a 200 response.</summary>
    internal static AskClaudeException FromStreamError(string data)
    {
        (string? type, string? detail) = ParseErrorBody(data);
        return Describe(type, detail, null);
    }

    internal static AskClaudeException Describe(string? type, string? detail, int? status)
    {
        string message = Clean(detail);
        string suffix = message.Length == 0 ? string.Empty : $" ({message})";
        return (type, status) switch
        {
            ("overloaded_error", _) or (_, 529) => new($"Claude is overloaded right now. {TryAgain}", retryable: true),
            ("rate_limit_error", _) or (_, 429) => new($"Rate limit reached for this API key. {TryAgain}", retryable: true),
            ("authentication_error", _) or (_, 401) => new("The API key was not accepted. Check it in Settings, under Ask Claude.", needsApiKey: true),
            ("permission_error", _) or (_, 403) => new($"This API key does not have permission for that request{suffix}.", needsApiKey: true),
            ("not_found_error", _) or (_, 404) => new($"Not found{suffix}. Try another model in Settings, under Ask Claude."),
            ("request_too_large", _) or (_, 413) => new("The capture is too large to send. Try a smaller capture."),
            ("invalid_request_error", _) or (_, 400) => new($"The request was rejected{suffix}."),
            ("api_error", _) or (_, >= 500) => new($"The Anthropic API had a problem{suffix}. {TryAgain}", retryable: true),
            (_, int code) => new($"API error {code}{suffix}."),
            _ => new(message.Length == 0 ? "The API reported an error." : $"API error: {message}.")
        };
    }

    private static (string? Type, string? Message) ParseErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            JsonElement error = root.TryGetProperty("error", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
            string? type = error.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;
            string? message = error.TryGetProperty("message", out JsonElement messageElement) && messageElement.ValueKind == JsonValueKind.String ? messageElement.GetString() : null;
            return (type, message);
        }
        catch (JsonException)
        {
            // Proxies and gateways can answer with HTML or plain text; show a short plain-text excerpt.
            return (null, body.TrimStart().StartsWith('<') ? null : body);
        }
    }

    private static string Clean(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return string.Empty;
        string singleLine = string.Join(' ', detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        singleLine = singleLine.TrimEnd('.');
        return singleLine.Length <= MaximumDetailLength ? singleLine : singleLine[..MaximumDetailLength] + "…";
    }
}
