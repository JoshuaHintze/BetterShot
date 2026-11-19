using System.Threading;

namespace BetterShot.Ai;

/// <summary>
/// Multi-turn history for one capture. The image travels only in the first user turn; follow-ups resend the
/// history (as the API requires) without repeating the image. Failed turns are removed so a retry stays valid.
/// </summary>
internal sealed class AskClaudeConversation
{
    private readonly List<ClaudeMessage> _messages = [];
    private int _version;

    internal IReadOnlyList<ClaudeMessage> Messages => _messages;
    internal bool IsEmpty => _messages.Count == 0;

    /// <summary>Also detaches any in-flight ask, so its late result cannot leak into the new conversation.</summary>
    internal void Reset()
    {
        _messages.Clear();
        _version++;
    }

    /// <param name="imagePngBase64">Requested only for the first turn, so encoding happens on the first explicit ask.</param>
    internal async Task<string> AskAsync(AnthropicClient client, string apiKey, string model, string question,
        Func<string> imagePngBase64, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(imagePngBase64);
        string? image = _messages.Count == 0 ? imagePngBase64() : null;
        var userTurn = new ClaudeMessage(ClaudeMessage.UserRole, question, image);
        _messages.Add(userTurn);
        int version = _version;
        var recorder = new LatestTextProgress(progress);
        try
        {
            string answer = await client.StreamAsync(apiKey, model, _messages.ToArray(), recorder, cancellationToken);
            if (version == _version) _messages.Add(new ClaudeMessage(ClaudeMessage.AssistantRole, answer));
            return answer;
        }
        catch (OperationCanceledException) when (version == _version && !string.IsNullOrWhiteSpace(recorder.Latest))
        {
            // Keep what was already shown so the next follow-up still has a valid user/assistant alternation.
            _messages.Add(new ClaudeMessage(ClaudeMessage.AssistantRole, recorder.Latest!));
            throw;
        }
        catch
        {
            if (version == _version) _messages.Remove(userTurn);
            throw;
        }
    }

    private sealed class LatestTextProgress(IProgress<string>? inner) : IProgress<string>
    {
        private volatile string? _latest;
        internal string? Latest => _latest;

        public void Report(string value)
        {
            _latest = value;
            inner?.Report(value);
        }
    }
}
