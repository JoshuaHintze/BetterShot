using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace BetterShot.Ai;

/// <summary>One turn of an Ask Claude conversation. Only the first user turn carries the capture.</summary>
internal sealed record ClaudeMessage(string Role, string Text, string? ImagePngBase64 = null)
{
    internal const string UserRole = "user";
    internal const string AssistantRole = "assistant";
}

/// <summary>
/// Minimal streaming client for the Anthropic Messages API. The API key is only ever placed in the
/// x-api-key header of requests to <see cref="Endpoint"/>; redirects are refused so it cannot be forwarded.
/// </summary>
internal sealed class AnthropicClient
{
    internal const string Endpoint = "https://api.anthropic.com/v1/messages";
    internal const string ApiVersion = "2023-06-01";
    internal const int MaxTokens = 2048;

    private static readonly Lazy<HttpClient> SharedHttpClient = new(() => CreateHttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }));

    private readonly HttpClient _httpClient;

    internal AnthropicClient() : this(SharedHttpClient.Value) { }

    /// <summary>Diagnostics inject a handler so no request reaches the network.</summary>
    internal AnthropicClient(HttpMessageHandler handler) : this(CreateHttpClient(handler)) { }

    private AnthropicClient(HttpClient httpClient) => _httpClient = httpClient;

    /// <summary>Longest wait for the response headers.</summary>
    internal TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Longest silence allowed between streamed chunks (the API sends pings while thinking).</summary>
    internal TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(90);

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) =>
        new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Sends the conversation and streams the answer. <paramref name="progress"/> receives the full answer
    /// text so far after each delta. Returns the complete answer, or throws <see cref="AskClaudeException"/>
    /// (readable message) or <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires.
    /// </summary>
    internal async Task<string> StreamAsync(string apiKey, string model, IReadOnlyList<ClaudeMessage> messages,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw AskClaudeErrors.MissingKey();
        using HttpRequestMessage request = CreateRequest(apiKey.Trim(), model, messages);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResponseTimeout);
        try
        {
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await ReadErrorBodyAsync(response, timeout.Token).ConfigureAwait(false);
                throw AskClaudeErrors.FromStatus((int)response.StatusCode, body);
            }
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            return await ReadEventStreamAsync(stream, progress, timeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AskClaudeException(AskClaudeErrors.TimedOut, retryable: true);
        }
        catch (HttpRequestException ex)
        {
            throw new AskClaudeException($"Could not reach api.anthropic.com. Check your internet connection. ({ex.Message})", retryable: true);
        }
        catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AskClaudeException($"The connection to api.anthropic.com was interrupted. {AskClaudeErrors.TryAgain} ({ex.Message})", retryable: true);
        }
    }

    internal static HttpRequestMessage CreateRequest(string apiKey, string model, IReadOnlyList<ClaudeMessage> messages)
    {
        byte[] body = Encoding.UTF8.GetBytes(BuildRequestJson(model, messages));
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content };
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    internal static string BuildRequestJson(string model, IReadOnlyList<ClaudeMessage> messages)
    {
        var messageArray = new JsonArray();
        foreach (ClaudeMessage message in messages) messageArray.Add(BuildMessage(message));
        var root = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = MaxTokens,
            ["stream"] = true,
            ["messages"] = messageArray
        };
        return root.ToJsonString();
    }

    private static JsonObject BuildMessage(ClaudeMessage message)
    {
        var content = new JsonArray();
        if (message.ImagePngBase64 is not null)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = "image/png",
                    ["data"] = message.ImagePngBase64
                }
            });
        }
        content.Add(new JsonObject { ["type"] = "text", ["text"] = message.Text });
        return new JsonObject { ["role"] = message.Role, ["content"] = content };
    }

    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Length > 16_384 ? body[..16_384] : body;
        }
        catch (HttpRequestException) { return string.Empty; }
        catch (IOException) { return string.Empty; }
    }

    private async Task<string> ReadEventStreamAsync(Stream stream, IProgress<string>? progress, CancellationTokenSource timeout)
    {
        var parser = new ServerSentEventParser();
        var accumulator = new ClaudeStreamAccumulator();
        Decoder decoder = Encoding.UTF8.GetDecoder();
        byte[] buffer = new byte[8192];
        char[] characters = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        while (true)
        {
            timeout.CancelAfter(IdleTimeout);
            int read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0) break;
            int count = decoder.GetChars(buffer, 0, read, characters, 0, flush: false);
            if (ApplyEvents(parser.Feed(characters.AsSpan(0, count)), accumulator, progress)) return accumulator.Finish();
        }
        ApplyEvents(parser.Complete(), accumulator, progress);
        return accumulator.Finish();
    }

    private static bool ApplyEvents(IReadOnlyList<ServerSentEvent> events, ClaudeStreamAccumulator accumulator, IProgress<string>? progress)
    {
        foreach (ServerSentEvent serverEvent in events)
        {
            int before = accumulator.Text.Length;
            bool finished = accumulator.Apply(serverEvent);
            if (accumulator.Text.Length != before) progress?.Report(accumulator.Text);
            if (finished) return true;
        }
        return false;
    }
}

/// <summary>Turns Messages API stream events into answer text; error events become <see cref="AskClaudeException"/>.</summary>
internal sealed class ClaudeStreamAccumulator
{
    private readonly StringBuilder _text = new();
    private bool _receivedMessage;

    internal string Text => _text.ToString();

    /// <summary>Returns true once message_stop arrives.</summary>
    internal bool Apply(ServerSentEvent serverEvent)
    {
        if (serverEvent.EventName == "error") throw AskClaudeErrors.FromStreamError(serverEvent.Data);
        JsonDocument document;
        try { document = JsonDocument.Parse(serverEvent.Data); }
        catch (JsonException) { return false; }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            string type = root.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString() ?? serverEvent.EventName
                : serverEvent.EventName;
            switch (type)
            {
                case "error": throw AskClaudeErrors.FromStreamError(serverEvent.Data);
                case "message_start": _receivedMessage = true; return false;
                case "content_block_delta": AppendDelta(root); return false;
                case "message_stop": _receivedMessage = true; return true;
                default: return false;
            }
        }
    }

    private void AppendDelta(JsonElement root)
    {
        _receivedMessage = true;
        if (!root.TryGetProperty("delta", out JsonElement delta) || delta.ValueKind != JsonValueKind.Object) return;
        if (!delta.TryGetProperty("type", out JsonElement deltaType) || deltaType.GetString() != "text_delta") return;
        if (delta.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String) _text.Append(text.GetString());
    }

    internal string Finish()
    {
        if (!_receivedMessage) throw new AskClaudeException(AskClaudeErrors.NoResponse, retryable: true);
        string answer = _text.ToString();
        if (string.IsNullOrWhiteSpace(answer)) throw new AskClaudeException(AskClaudeErrors.NoText);
        return answer;
    }
}
