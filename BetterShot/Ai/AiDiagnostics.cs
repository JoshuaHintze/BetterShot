using BetterShot.Persistence;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterShot.Ai;

/// <summary>
/// Ask Claude checks with no network access: every request goes to an injected HttpMessageHandler. Covers the
/// request JSON and headers, SSE parsing (split chunks, CRLF, split UTF-8, error events), error wording,
/// timeouts/cancellation, conversation history, image downscaling, AiModel settings, key storage, and the window.
/// </summary>
internal static class AiDiagnostics
{
    private const string TestKey = "sk-ant-diagnostic-NOT-A-REAL-KEY-7f3c9a";

    internal static async Task RunAsync(string? output)
    {
        string directory = string.IsNullOrWhiteSpace(output) ? AppContext.BaseDirectory : Path.HasExtension(output) ? Path.GetDirectoryName(Path.GetFullPath(output))! : output;
        Directory.CreateDirectory(directory);
        string runRoot = Path.Combine(directory, "ai-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        // Belt and braces: nothing in this run may construct a real network client.
        AskClaudeEnvironment.ClientFactory = () => throw new InvalidOperationException("A diagnostic tried to use the real network client.");

        await VerifyRequestShapeAsync();
        VerifyParser();
        await VerifyStreamingAsync();
        await VerifyErrorMappingAsync();
        await VerifyTimeoutsAndCancellationAsync();
        await VerifyConversationAsync();
        VerifyImageDownscale();
        VerifyPresetsAndModels();
        VerifySettingsRoundTrip(Path.Combine(runRoot, "settings"));
        VerifyKeyStore(Path.Combine(runRoot, "keys"));
        await VerifySettingsPanelAsync(Path.Combine(runRoot, "panel", "History"), directory);
        await VerifyWindowAsync(directory);
        App.WriteTestResult(Path.Combine(directory, "ai-test.txt"), "PASS: Ask Claude request JSON and headers, SSE parsing (split chunks, CRLF, split UTF-8, comments, error events), error messages (no key, no response, no text, 401/403/404/400/413/429/5xx/529, overloaded), timeouts and cancellation, multi-turn history with image only in the first turn, 1568 px downscale, presets, AiModel settings round trip and unknown-field preservation, DPAPI key storage outside settings.json, Settings panel, and window send/follow-up/no-key/cancel-on-close, all without network access");
    }

    // ---------- Request shape ----------

    private static async Task VerifyRequestShapeAsync()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(SseResponse(StandardEvents("Hi"), 64)));
        var client = new AnthropicClient(handler);
        ClaudeMessage[] messages =
        [
            new(ClaudeMessage.UserRole, "What is this?", "aW1hZ2U="),
            new(ClaudeMessage.AssistantRole, "A dialog."),
            new(ClaudeMessage.UserRole, "And the button?")
        ];
        string answer = await client.StreamAsync(TestKey, "claude-opus-5-5", messages, null, CancellationToken.None);
        Check(answer == "Hi", "Streaming answer was not returned.");
        CapturedRequest request = handler.Requests.Single();
        Check(request.Method == HttpMethod.Post && request.Uri == new Uri("https://api.anthropic.com/v1/messages"), "Request did not POST to the Messages endpoint.");
        Check(request.Headers.TryGetValue("x-api-key", out string? key) && key == TestKey, "x-api-key header missing or wrong.");
        Check(request.Headers.TryGetValue("anthropic-version", out string? version) && version == "2023-06-01", "anthropic-version header missing or wrong.");
        Check(request.ContentType == "application/json", $"Content type was {request.ContentType}.");
        Check(!request.Headers.ContainsKey("Authorization"), "The key leaked into an Authorization header.");
        Check(!request.Body.Contains(TestKey, StringComparison.Ordinal), "The API key appeared in the request body.");

        JsonNode root = JsonNode.Parse(request.Body)!;
        Check(root["model"]!.GetValue<string>() == "claude-opus-5-5" && root["max_tokens"]!.GetValue<int>() == 2048 && root["stream"]!.GetValue<bool>(), "model/max_tokens/stream are wrong.");
        JsonArray turns = root["messages"]!.AsArray();
        Check(turns.Count == 3, "Messages were not all sent.");
        JsonArray first = turns[0]!["content"]!.AsArray();
        Check(turns[0]!["role"]!.GetValue<string>() == "user" && first.Count == 2, "First user turn should hold image + text.");
        Check(first[0]!["type"]!.GetValue<string>() == "image" && first[0]!["source"]!["type"]!.GetValue<string>() == "base64"
            && first[0]!["source"]!["media_type"]!.GetValue<string>() == "image/png" && first[0]!["source"]!["data"]!.GetValue<string>() == "aW1hZ2U=", "Image block shape is wrong.");
        Check(first[1]!["type"]!.GetValue<string>() == "text" && first[1]!["text"]!.GetValue<string>() == "What is this?", "Image must be followed by the text block.");
        Check(turns[1]!["role"]!.GetValue<string>() == "assistant" && turns[1]!["content"]!.AsArray().Count == 1, "Assistant turn shape is wrong.");
        JsonArray third = turns[2]!["content"]!.AsArray();
        Check(third.Count == 1 && third[0]!["type"]!.GetValue<string>() == "text", "Follow-up turns must not repeat the image.");
    }

    // ---------- SSE ----------

    private static void VerifyParser()
    {
        var parser = new ServerSentEventParser();
        var events = new List<ServerSentEvent>();
        foreach (string piece in new[] { ": keep-alive\r", "\nevent: one\r\nda", "ta: first\r\ndata:second\r", "\n\r\n", "event: two\ndata: {\"x\":1}" })
            events.AddRange(parser.Feed(piece));
        events.AddRange(parser.Complete());
        Check(events.Count == 2, $"Parser produced {events.Count} events.");
        Check(events[0].EventName == "one" && events[0].Data == "first\nsecond", "Multi-line data or CRLF split was mishandled.");
        Check(events[1].EventName == "two" && events[1].Data == "{\"x\":1}", "A final event without a blank line was lost.");
        Check(new ServerSentEventParser().Feed("event: ping\n\n").Count == 0, "Events without data should be ignored.");
    }

    private static async Task VerifyStreamingAsync()
    {
        string expected = "Hello, **world**! café ✓ 日本語\n```\ncode\n```";
        foreach (int chunkSize in new[] { 1, 2, 3, 5, 17, 4096 })
        {
            foreach (bool crlf in new[] { false, true })
            {
                var handler = new FakeHandler((_, _) => Task.FromResult(SseResponse(StandardEvents(SplitIntoDeltas(expected)), chunkSize, crlf)));
                var reports = new List<string>();
                string answer = await new AnthropicClient(handler).StreamAsync(TestKey, ClaudeModels.DefaultId, [new(ClaudeMessage.UserRole, "q")], new SyncProgress(reports.Add), CancellationToken.None);
                Check(answer == expected, $"Chunk size {chunkSize} (CRLF {crlf}) produced \"{answer}\".");
                Check(reports.Count > 1 && reports[^1] == expected, "Progress did not report incremental text.");
                for (int index = 1; index < reports.Count; index++)
                    Check(reports[index].StartsWith(reports[index - 1], StringComparison.Ordinal), "Progress text was not cumulative.");
            }
        }

        // Non-text deltas (for example tool or thinking deltas) are ignored, and message_stop ends the read.
        string mixed = Event("message_start", "{\"type\":\"message_start\",\"message\":{}}")
            + Event("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{}\"}}")
            + Event("content_block_delta", Delta("ok"))
            + Event("message_stop", "{\"type\":\"message_stop\"}")
            + Event("content_block_delta", Delta(" after stop"));
        string stopped = await new AnthropicClient(new FakeHandler((_, _) => Task.FromResult(SseResponse(mixed, 4)))).StreamAsync(TestKey, ClaudeModels.DefaultId, [new(ClaudeMessage.UserRole, "q")], null, CancellationToken.None);
        Check(stopped == "ok", "Non-text deltas or post-stop data changed the answer.");
    }

    // ---------- Errors ----------

    private static async Task VerifyErrorMappingAsync()
    {
        Check(AskClaudeErrors.NoApiKey == "No API key yet. Add one in Settings, under Ask Claude.", "No-key wording changed.");
        Check(AskClaudeErrors.NoResponse == "No response from the API." && AskClaudeErrors.NoText == "The reply contained no text.", "Empty-response wording changed.");

        AskClaudeException missing = await ExpectErrorAsync(new FakeHandler((_, _) => throw new InvalidOperationException("Sent without a key.")), apiKey: "  ");
        Check(missing.Message == AskClaudeErrors.NoApiKey && missing.NeedsApiKey, "A missing key did not stop the request.");

        AskClaudeException unauthorized = await ExpectStatusAsync(HttpStatusCode.Unauthorized, ErrorBody("authentication_error", "invalid x-api-key"));
        Check(unauthorized.NeedsApiKey && unauthorized.Message.Contains("API key", StringComparison.Ordinal), "401 was not explained as a key problem.");
        foreach (HttpStatusCode busy in new[] { HttpStatusCode.TooManyRequests, (HttpStatusCode)529 })
        {
            AskClaudeException error = await ExpectStatusAsync(busy, ErrorBody(busy == HttpStatusCode.TooManyRequests ? "rate_limit_error" : "overloaded_error", "busy"));
            Check(error.Retryable && error.Message.EndsWith(AskClaudeErrors.TryAgain, StringComparison.Ordinal), $"{(int)busy} did not say to try again.");
        }
        AskClaudeException invalid = await ExpectStatusAsync(HttpStatusCode.BadRequest, ErrorBody("invalid_request_error", "messages: text content blocks must be non-empty"));
        Check(invalid.Message.Contains("text content blocks must be non-empty", StringComparison.Ordinal) && !invalid.Retryable, "400 detail was not shown.");
        AskClaudeException notFound = await ExpectStatusAsync(HttpStatusCode.NotFound, ErrorBody("not_found_error", "model: claude-x"));
        Check(notFound.Message.Contains("model: claude-x", StringComparison.Ordinal) && notFound.Message.Contains("Try another model", StringComparison.Ordinal), "404 did not point at the model.");
        AskClaudeException tooLarge = await ExpectStatusAsync(HttpStatusCode.RequestEntityTooLarge, "");
        Check(tooLarge.Message.Contains("too large", StringComparison.Ordinal), "413 was not explained.");
        AskClaudeException gateway = await ExpectStatusAsync(HttpStatusCode.BadGateway, "<html><body>Bad gateway</body></html>");
        Check(gateway.Retryable && !gateway.Message.Contains('<'), "HTML error pages leaked into the message.");
        AskClaudeException redirect = await ExpectStatusAsync(HttpStatusCode.TemporaryRedirect, "");
        Check(redirect.Message.StartsWith("API error 307", StringComparison.Ordinal), "Redirects must be refused, not followed.");

        AskClaudeException overloaded = await ExpectErrorAsync(new FakeHandler((_, _) => Task.FromResult(SseResponse(
            Event("message_start", "{\"type\":\"message_start\",\"message\":{}}") + Event("content_block_delta", Delta("partial "))
            + Event("error", ErrorBody("overloaded_error", "Overloaded")), 7))));
        Check(overloaded.Retryable && overloaded.Message == "Claude is overloaded right now. " + AskClaudeErrors.TryAgain, "Mid-stream overloaded error was not mapped.");
        AskClaudeException untyped = await ExpectErrorAsync(new FakeHandler((_, _) => Task.FromResult(SseResponse(
            "data: " + ErrorBody("api_error", "Internal") + "\n\n", 3))));
        Check(untyped.Retryable && untyped.Message.Contains("Internal", StringComparison.Ordinal), "An error identified only by its data type was missed.");

        AskClaudeException empty = await ExpectErrorAsync(new FakeHandler((_, _) => Task.FromResult(SseResponse(string.Empty, 8))));
        Check(empty.Message == AskClaudeErrors.NoResponse, "An empty stream was not reported as no response.");
        AskClaudeException noText = await ExpectErrorAsync(new FakeHandler((_, _) => Task.FromResult(SseResponse(StandardEvents(Array.Empty<string>()), 8))));
        Check(noText.Message == AskClaudeErrors.NoText, "A text-less reply was not reported.");
        AskClaudeException network = await ExpectErrorAsync(new FakeHandler((_, _) => throw new HttpRequestException("No such host is known.")));
        Check(network.Retryable && network.Message.Contains("api.anthropic.com", StringComparison.Ordinal), "Network failures were not explained.");
    }

    private static async Task VerifyTimeoutsAndCancellationAsync()
    {
        var hangingHeaders = new FakeHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.OK); });
        AskClaudeException headers = await ExpectErrorAsync(hangingHeaders, client: new AnthropicClient(hangingHeaders) { ResponseTimeout = TimeSpan.FromMilliseconds(150) });
        Check(headers.Message == AskClaudeErrors.TimedOut && headers.Retryable, "A response timeout was not reported.");

        var hangingBody = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(Encoding.UTF8.GetBytes(Event("message_start", "{\"type\":\"message_start\",\"message\":{}}") + Event("content_block_delta", Delta("so far"))), 16, hangAtEnd: true))
        }));
        AskClaudeException idle = await ExpectErrorAsync(hangingBody, client: new AnthropicClient(hangingBody) { IdleTimeout = TimeSpan.FromMilliseconds(150) });
        Check(idle.Message == AskClaudeErrors.TimedOut, "A stalled stream was not reported as a timeout.");

        using var cancellation = new CancellationTokenSource();
        var cancelled = new FakeHandler(async (_, token) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.OK); });
        bool threwCancellation = false;
        try { await new AnthropicClient(cancelled).StreamAsync(TestKey, ClaudeModels.DefaultId, [new(ClaudeMessage.UserRole, "q")], null, cancellation.Token); }
        catch (OperationCanceledException) { threwCancellation = true; }
        Check(threwCancellation && cancelled.SawCancellation, "User cancellation was not propagated to the request.");
    }

    private static async Task VerifyConversationAsync()
    {
        int encodes = 0;
        string Encode() { encodes++; return "UE5H"; }
        var answers = new Queue<string>(["First answer.", "Second answer."]);
        var handler = new FakeHandler((_, _) => Task.FromResult(SseResponse(StandardEvents(answers.Dequeue()), 11)));
        var client = new AnthropicClient(handler);
        var conversation = new AskClaudeConversation();
        Check(await conversation.AskAsync(client, TestKey, ClaudeModels.DefaultId, "Describe", Encode, null, CancellationToken.None) == "First answer.", "First turn failed.");
        Check(await conversation.AskAsync(client, TestKey, ClaudeModels.DefaultId, "Follow up", Encode, null, CancellationToken.None) == "Second answer.", "Follow-up failed.");
        Check(encodes == 1, "The image was encoded more than once.");
        JsonArray second = JsonNode.Parse(handler.Requests[1].Body)!["messages"]!.AsArray();
        Check(second.Count == 3 && second[1]!["content"]![0]!["text"]!.GetValue<string>() == "First answer." && second[0]!["content"]!.AsArray().Count == 2 && second[2]!["content"]!.AsArray().Count == 1, "Follow-up did not resend history with the image only in the first turn.");
        Check(conversation.Messages.Count == 4, "History was not retained.");

        var failing = new AnthropicClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)529) { Content = new StringContent(ErrorBody("overloaded_error", "Overloaded")) })));
        try { await conversation.AskAsync(failing, TestKey, ClaudeModels.DefaultId, "Third", Encode, null, CancellationToken.None); Check(false, "A failed turn did not throw."); }
        catch (AskClaudeException) { }
        Check(conversation.Messages.Count == 4 && conversation.Messages[^1].Role == ClaudeMessage.AssistantRole, "A failed turn was left in history.");

        conversation.Reset();
        Check(conversation.IsEmpty, "New conversation did not clear history.");
        await conversation.AskAsync(new AnthropicClient(new FakeHandler((_, _) => Task.FromResult(SseResponse(StandardEvents("Fresh"), 64)))), TestKey, ClaudeModels.DefaultId, "Again", Encode, null, CancellationToken.None);
        Check(encodes == 2 && conversation.Messages[0].ImagePngBase64 is not null, "A new conversation did not resend the image.");
    }

    // ---------- Image, presets, models ----------

    private static void VerifyImageDownscale()
    {
        Check(ClaudeImage.FitLongEdge(3840, 2160) == (1568, 882), "4K landscape did not fit 1568.");
        Check(ClaudeImage.FitLongEdge(1000, 3000) == (523, 1568), "Tall capture did not fit 1568.");
        Check(ClaudeImage.FitLongEdge(1568, 1000) == (1568, 1000) && ClaudeImage.FitLongEdge(800, 600) == (800, 600), "Small captures were resized.");
        BitmapSource large = SampleBitmap(2400, 1200);
        BitmapSource scaled = ClaudeImage.Downscale(large);
        Check(scaled.PixelWidth == 1568 && scaled.PixelHeight == 784, $"Downscale produced {scaled.PixelWidth}x{scaled.PixelHeight}.");
        byte[] png = Convert.FromBase64String(ClaudeImage.ToPngBase64(large));
        Check(png.Length > 8 && png[0] == 0x89 && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G', "Encoded image is not PNG.");
        BitmapFrame decoded = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Check(decoded.PixelWidth == 1568 && decoded.PixelHeight == 784, "Sent PNG was not downscaled.");
        BitmapFrame small = BitmapDecoder.Create(new MemoryStream(Convert.FromBase64String(ClaudeImage.ToPngBase64(SampleBitmap(300, 200)))), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Check(small.PixelWidth == 300 && small.PixelHeight == 200, "A small capture changed size.");
    }

    private static void VerifyPresetsAndModels()
    {
        string[] labels = AskClaudePresets.All.Select(preset => preset.Label).ToArray();
        Check(labels.SequenceEqual(["Describe", "Explain error", "Draft bug report", "Find UI issues", "Extract text", "Write alt text"]), "Preset list changed.");
        Check(AskClaudePresets.All[0].Prompt == "Describe what this screenshot shows, concisely. Mention the app or site if identifiable and anything notable.", "Describe prompt changed.");
        Check(AskClaudePresets.All[5].Prompt == "Write alt text for this screenshot: one or two plain sentences a screen reader user would find useful. Output only the alt text.", "Alt text prompt changed.");
        Check(ClaudeModels.All.Select(model => model.Id).SequenceEqual(["claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5"]) && ClaudeModels.DefaultId == "claude-sonnet-5-5", "Model choices changed.");
        Check(ClaudeModels.All[0].Label == "Claude Sonnet 5.5 (recommended)", "Default model label changed.");
        Check(ClaudeModels.Normalize(" claude-haiku-5-5 ") == "claude-haiku-5-5" && ClaudeModels.Normalize("gpt-4") == ClaudeModels.DefaultId && ClaudeModels.Normalize(null) == ClaudeModels.DefaultId, "Model normalization failed.");
        List<System.Windows.Documents.Block> blocks = AnswerMarkdown.ToBlocks("# Title\n\nSome **bold** and `code`.\n- item\n1. first\n```\nunterminated");
        Check(blocks.Count == 5, $"Markdown produced {blocks.Count} blocks.");
    }

    // ---------- Settings and key storage ----------

    private static void VerifySettingsRoundTrip(string root)
    {
        var fresh = new CaptureHistoryStore(Path.Combine(root, "fresh"));
        Check(fresh.Settings.AiModel == ClaudeModels.DefaultId, "AiModel did not default to Sonnet.");
        fresh.UpdateSettings(settings => settings with { AiModel = "claude-opus-5-5" });
        Check(new CaptureHistoryStore(Path.Combine(root, "fresh")).Settings.AiModel == "claude-opus-5-5", "AiModel did not round-trip.");

        string legacyRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        string legacyPath = Path.Combine(legacyRoot, "settings.json");
        File.WriteAllText(legacyPath, "{\"RetentionDays\":14,\"CaptureDelaySeconds\":3,\"FutureAiThing\":{\"Enabled\":true}}");
        var legacy = new CaptureHistoryStore(legacyRoot);
        Check(legacy.Settings.AiModel == ClaudeModels.DefaultId && legacy.RetentionDays == 14 && legacy.Settings.CaptureDelaySeconds == 3, "Older settings did not load with the default model.");
        legacy.UpdateSettings(settings => settings with { AiModel = "claude-haiku-5-5" });
        JsonNode saved = JsonNode.Parse(File.ReadAllText(legacyPath))!;
        Check(saved["AiModel"]!.GetValue<string>() == "claude-haiku-5-5" && saved["FutureAiThing"]?["Enabled"]?.GetValue<bool>() == true && saved["RetentionDays"]!.GetValue<int>() == 14, "AiModel write dropped other or unknown fields.");

        string unknownRoot = Path.Combine(root, "unknown");
        Directory.CreateDirectory(unknownRoot);
        File.WriteAllText(Path.Combine(unknownRoot, "settings.json"), "{\"RetentionDays\":7,\"AiModel\":\"claude-retired-1\"}");
        Check(new CaptureHistoryStore(unknownRoot).Settings.AiModel == ClaudeModels.DefaultId, "An unknown model id was accepted.");
    }

    private static void VerifyKeyStore(string root)
    {
        string path = Path.Combine(root, "ask-claude-key.bin");
        var store = new ApiKeyStore(path);
        Check(!store.HasKey && store.Load() is null, "A fresh key store reported a key.");
        store.Save("  " + TestKey + "  ");
        Check(store.HasKey && store.Load() == TestKey && new ApiKeyStore(path).Load() == TestKey, "The key did not round-trip through DPAPI.");
        byte[] raw = File.ReadAllBytes(path);
        Check(!ContainsBytes(raw, Encoding.UTF8.GetBytes(TestKey)) && !ContainsBytes(raw, Encoding.Unicode.GetBytes(TestKey)), "The key file contains the key in plain text.");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        Check(store.Load() is null, "A corrupt key file did not read as no key.");
        store.Remove();
        Check(!store.HasKey && !File.Exists(path), "Remove did not delete the key.");
        var memory = new ApiKeyStore(null);
        memory.Save(TestKey);
        Check(memory.Load() == TestKey && memory.FilePath is null, "In-memory key store failed.");
        memory.Remove();
        Check(!memory.HasKey, "In-memory remove failed.");
        bool rejected = false;
        try { store.Save("   "); } catch (ArgumentException) { rejected = true; }
        Check(rejected && !store.HasKey, "An empty key was saved.");
    }

    private static async Task VerifySettingsPanelAsync(string historyRoot, string output)
    {
        MainWindow? shell = null;
        try
        {
            shell = new MainWindow(enableHistory: true, historyRoot: historyRoot);
            Application.Current.MainWindow = shell;
            shell.Show(); shell.OpenSettingsForTest(); await SettleAsync(shell);
            Check(ReferenceEquals(AskClaudeEnvironment.KeyStore, shell.ApiKeyStoreForTest), "Main window did not share its key store with Ask Claude.");
            Check(shell.ApiKeyStoreForTest.FilePath == Path.Combine(Path.GetDirectoryName(Path.GetFullPath(historyRoot))!, "ask-claude-key.bin"), "Fixture key path is not beside the fixture history root.");
            Check(shell.ApiKeySavedState.Visibility != Visibility.Visible && !shell.RemoveApiKeyButton.IsEnabled && !shell.SaveApiKeyButton.IsEnabled, "Empty key state is wrong.");
            Check(((ComboBoxItem)shell.AiModelComboBox.SelectedItem).Tag?.ToString() == ClaudeModels.DefaultId && shell.AiModelComboBox.Items.Count == 3, "Model dropdown is wrong.");

            shell.ApiKeyBox.Password = TestKey;
            Check(shell.SaveApiKeyButton.IsEnabled, "Save did not enable after typing a key.");
            shell.SaveApiKeyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await SettleAsync(shell);
            Check(shell.ApiKeyBox.Password.Length == 0 && shell.ApiKeySavedState.Visibility == Visibility.Visible && shell.RemoveApiKeyButton.IsEnabled, "Saved state is wrong after saving.");
            Check(shell.ApiKeyStoreForTest.Load() == TestKey, "The saved key cannot be read back.");

            shell.AiModelComboBox.SelectedItem = shell.AiModelComboBox.Items.Cast<ComboBoxItem>().Single(item => item.Tag?.ToString() == "claude-opus-5-5");
            await SettleAsync(shell);
            Check(shell.CurrentSettings.AiModel == "claude-opus-5-5" && AskClaudeEnvironment.ModelProvider() == "claude-opus-5-5", "Model choice was not saved or shared.");
            Check(new CaptureHistoryStore(historyRoot).Settings.AiModel == "claude-opus-5-5", "Model choice was not persisted.");

            string fixtureRoot = Path.GetDirectoryName(Path.GetFullPath(historyRoot))!;
            foreach (string file in Directory.EnumerateFiles(fixtureRoot, "*", SearchOption.AllDirectories))
            {
                byte[] bytes = File.ReadAllBytes(file);
                Check(!ContainsBytes(bytes, Encoding.UTF8.GetBytes(TestKey)) && !ContainsBytes(bytes, Encoding.Unicode.GetBytes(TestKey)), $"The API key appears in plain text in {Path.GetFileName(file)}.");
            }
            Check(!File.ReadAllText(Path.Combine(historyRoot, "settings.json")).Contains("sk-ant", StringComparison.OrdinalIgnoreCase), "settings.json mentions the key.");

            shell.StatusText.Text = string.Empty;
            shell.AskClaudeSection.BringIntoView(); await SettleAsync(shell);
            Render(shell, Path.Combine(output, "ai-settings-panel.png"));

            shell.RemoveApiKeyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await SettleAsync(shell);
            Check(!shell.ApiKeyStoreForTest.HasKey && shell.ApiKeySavedState.Visibility != Visibility.Visible, "Remove did not clear the key.");
        }
        finally { shell?.CloseForTest(); }
    }

    // ---------- Window ----------

    private static async Task VerifyWindowAsync(string output)
    {
        var keyStore = new ApiKeyStore(null);
        AskClaudeEnvironment.KeyStore = keyStore;
        AskClaudeEnvironment.ModelProvider = () => "claude-haiku-5-5";
        AskClaudeEnvironment.OpenSettings = null;
        var replies = new Queue<string>(["This is a **settings** dialog.\n\n- Item one\n- Item two\n\n```\nError 0x80070005\n```", "The button saves changes."]);
        var handler = new FakeHandler((_, _) => Task.FromResult(SseResponse(StandardEvents(SplitIntoDeltas(replies.Dequeue())), 9)));
        AskClaudeEnvironment.ClientFactory = () => new AnthropicClient(handler);

        AskClaudeWindow window = AskClaudeWindow.Show(null, SampleBitmap(2000, 1100), "Diagnostic capture");
        try
        {
            await SettleAsync(window);
            Check(handler.Requests.Count == 0, "Opening the window sent a request.");
            Render(window, Path.Combine(output, "ai-window-empty.png"));

            await window.AskForTest("Describe");
            Check(window.ErrorForTest == AskClaudeErrors.NoApiKey && handler.Requests.Count == 0 && window.TurnCountForTest == 0, "Missing key was not reported before sending.");

            keyStore.Save(TestKey);
            window.ClickPresetForTest(0);
            for (int attempt = 0; attempt < 200 && (window.IsBusyForTest || handler.Requests.Count == 0); attempt++) await Task.Delay(20);
            await SettleAsync(window);
            Check(handler.Requests.Count == 1 && window.ErrorForTest.Length == 0, "Preset click did not send exactly one request.");
            JsonNode first = JsonNode.Parse(handler.Requests[0].Body)!;
            Check(first["model"]!.GetValue<string>() == "claude-haiku-5-5", "Window ignored the model setting.");
            JsonArray content = first["messages"]![0]!["content"]!.AsArray();
            Check(content[1]!["text"]!.GetValue<string>() == AskClaudePresets.All[0].Prompt, "Preset prompt was not sent verbatim.");
            BitmapFrame sent = BitmapDecoder.Create(new MemoryStream(Convert.FromBase64String(content[0]!["source"]!["data"]!.GetValue<string>())), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Check(sent.PixelWidth == 1568 && sent.PixelHeight == 862, $"Window sent a {sent.PixelWidth}x{sent.PixelHeight} image.");
            Check(window.LastAnswerForTest.StartsWith("This is a **settings** dialog.", StringComparison.Ordinal) && window.ConversationTextForTest.Contains("Error 0x80070005", StringComparison.Ordinal), "Answer was not rendered.");

            await window.AskForTest("What does the button do?");
            await SettleAsync(window);
            Check(handler.Requests.Count == 2 && JsonNode.Parse(handler.Requests[1].Body)!["messages"]!.AsArray().Count == 3 && window.TurnCountForTest == 4, "Follow-up did not include history.");
            Check(window.LastAnswerForTest == "The button saves changes.", "Follow-up answer was not shown.");
            Render(window, Path.Combine(output, "ai-window-conversation.png"));
        }
        finally { window.Close(); }

        var hanging = new FakeHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.OK); });
        AskClaudeEnvironment.ClientFactory = () => new AnthropicClient(hanging);
        AskClaudeWindow closing = AskClaudeWindow.Show(null, SampleBitmap(200, 120), "Close test");
        await SettleAsync(closing);
        Task pending = closing.AskForTest("Describe");
        for (int attempt = 0; attempt < 100 && hanging.Requests.Count == 0; attempt++) await Task.Delay(20);
        closing.Close();
        await Task.WhenAny(pending, Task.Delay(3000));
        Check(pending.IsCompleted && hanging.SawCancellation, "Closing the window did not cancel the in-flight request.");
    }

    // ---------- Helpers ----------

    private static async Task<AskClaudeException> ExpectStatusAsync(HttpStatusCode status, string body) =>
        await ExpectErrorAsync(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") })));

    private static async Task<AskClaudeException> ExpectErrorAsync(FakeHandler handler, string apiKey = TestKey, AnthropicClient? client = null)
    {
        try
        {
            await (client ?? new AnthropicClient(handler)).StreamAsync(apiKey, ClaudeModels.DefaultId, [new(ClaudeMessage.UserRole, "q")], null, CancellationToken.None);
        }
        catch (AskClaudeException error)
        {
            Check(!error.Message.Contains(TestKey, StringComparison.Ordinal), "An error message contained the API key.");
            return error;
        }
        throw new InvalidOperationException("Expected an Ask Claude error but the request succeeded.");
    }

    private static string StandardEvents(params string[] deltas)
    {
        var builder = new StringBuilder();
        builder.Append(Event("message_start", "{\"type\":\"message_start\",\"message\":{\"id\":\"msg_test\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}"));
        builder.Append(Event("content_block_start", "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}"));
        builder.Append(Event("ping", "{\"type\": \"ping\"}"));
        foreach (string delta in deltas) builder.Append(Event("content_block_delta", Delta(delta)));
        builder.Append(Event("content_block_stop", "{\"type\":\"content_block_stop\",\"index\":0}"));
        builder.Append(Event("message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":15}}"));
        builder.Append(Event("message_stop", "{\"type\":\"message_stop\"}"));
        return builder.ToString();
    }

    private static string[] SplitIntoDeltas(string text)
    {
        var pieces = new List<string>();
        for (int index = 0; index < text.Length; index += 4) pieces.Add(text.Substring(index, Math.Min(4, text.Length - index)));
        return pieces.ToArray();
    }

    private static string Event(string name, string data) => $"event: {name}\ndata: {data}\n\n";

    private static string Delta(string text) =>
        "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonSerializer.Serialize(text) + "}}";

    private static string ErrorBody(string type, string message) =>
        "{\"type\":\"error\",\"error\":{\"type\":" + JsonSerializer.Serialize(type) + ",\"message\":" + JsonSerializer.Serialize(message) + "}}";

    private static HttpResponseMessage SseResponse(string events, int chunkSize, bool crlf = false)
    {
        string body = crlf ? events.Replace("\n", "\r\n", StringComparison.Ordinal) : events;
        var content = new StreamContent(new ChunkedStream(Encoding.UTF8.GetBytes(body), chunkSize));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;

    private static BitmapSource SampleBitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            int pixel = index / 4;
            pixels[index] = (byte)(pixel % width * 255 / Math.Max(1, width - 1));
            pixels[index + 1] = (byte)(pixel / width * 255 / Math.Max(1, height - 1));
            pixels[index + 2] = 140;
            pixels[index + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static async Task SettleAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    private static void Render(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        DpiScale dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri? Uri, Dictionary<string, string> Headers, string? ContentType, string Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal List<CapturedRequest> Requests { get; } = [];
        internal bool SawCancellation { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri, headers, request.Content?.Headers.ContentType?.ToString(), body));
            try { return await respond(request, cancellationToken); }
            catch (OperationCanceledException) { SawCancellation = true; throw; }
        }
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    /// <summary>Returns at most a few bytes per read so SSE lines and UTF-8 sequences split across reads.</summary>
    private sealed class ChunkedStream(byte[] data, int chunkSize, bool hangAtEnd = false) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => CopyNext(buffer.AsSpan(offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= data.Length && hangAtEnd) await Task.Delay(Timeout.Infinite, cancellationToken);
            await Task.Yield();
            return CopyNext(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int CopyNext(Span<byte> destination)
        {
            int count = Math.Min(Math.Min(chunkSize, destination.Length), data.Length - _position);
            if (count <= 0) return 0;
            data.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }
    }
}
