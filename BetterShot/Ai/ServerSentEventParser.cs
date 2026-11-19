using System.Text;

namespace BetterShot.Ai;

internal sealed record ServerSentEvent(string EventName, string Data);

/// <summary>
/// Incremental text/event-stream parser. Network reads can split anywhere (mid-line, mid-CRLF), so text is
/// buffered until a full line arrives and events are emitted only at their terminating blank line.
/// </summary>
internal sealed class ServerSentEventParser
{
    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _data = new();
    private string _eventName = string.Empty;
    private bool _hasData;
    private bool _skipLeadingLineFeed;

    internal IReadOnlyList<ServerSentEvent> Feed(ReadOnlySpan<char> chunk)
    {
        var events = new List<ServerSentEvent>();
        foreach (char character in chunk)
        {
            if (_skipLeadingLineFeed)
            {
                _skipLeadingLineFeed = false;
                if (character == '\n') continue;
            }
            if (character is '\r' or '\n')
            {
                _skipLeadingLineFeed = character == '\r';
                ProcessLine(_pending.ToString(), events);
                _pending.Clear();
            }
            else _pending.Append(character);
        }
        return events;
    }

    /// <summary>Call at end of stream: a final event without a trailing blank line is still delivered.</summary>
    internal IReadOnlyList<ServerSentEvent> Complete()
    {
        var events = new List<ServerSentEvent>();
        if (_pending.Length > 0) { ProcessLine(_pending.ToString(), events); _pending.Clear(); }
        ProcessLine(string.Empty, events);
        return events;
    }

    private void ProcessLine(string line, List<ServerSentEvent> events)
    {
        if (line.Length == 0)
        {
            if (_hasData) events.Add(new ServerSentEvent(_eventName, _data.ToString()));
            _data.Clear();
            _eventName = string.Empty;
            _hasData = false;
            return;
        }
        if (line[0] == ':') return;

        int colon = line.IndexOf(':');
        string field = colon < 0 ? line : line[..colon];
        string value = colon < 0 ? string.Empty : line[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..];

        switch (field)
        {
            case "event": _eventName = value; break;
            case "data":
                if (_hasData) _data.Append('\n');
                _data.Append(value);
                _hasData = true;
                break;
        }
    }
}
