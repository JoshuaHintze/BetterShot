using BetterShot.Editing;
using System.IO;
using System.Threading;
using System.Windows.Threading;

namespace BetterShot.Ocr;

internal enum OcrIndexState { Off, Idle, Working, Unavailable }

/// <summary>
/// Reads text from library captures in the background, one at a time and only while the app is idle,
/// and stores it in <see cref="OcrTextIndex"/> for search. Lives on the UI thread (masking renders there);
/// recognition runs on the thread pool. Cancelled on shutdown or when text reading is turned off.
/// </summary>
internal sealed class OcrIndexer : IDisposable
{
    private static readonly TimeSpan PauseBetweenCaptures = TimeSpan.FromMilliseconds(400);
    private readonly OcrTextIndex _index;
    private readonly Dispatcher _dispatcher;
    private readonly LinkedList<CaptureDocument> _queue = new();
    private CancellationTokenSource? _cancellation;
    private bool _running;
    private bool _enabled;

    internal OcrIndexer(OcrTextIndex index, Dispatcher dispatcher)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>Raised on the UI thread after a capture's text is stored, and when <see cref="State"/> changes.</summary>
    internal event EventHandler? Changed;

    internal OcrTextIndex Index => _index;
    internal OcrIndexState State { get; private set; } = OcrIndexState.Off;
    internal string? UnavailableReason { get; private set; }
    internal int Pending => _queue.Count;

    internal bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value)
            {
                _cancellation?.Cancel();
                _queue.Clear();
                SetState(OcrIndexState.Off);
            }
            else
            {
                UnavailableReason = null;
                SetState(OcrIndexState.Idle);
            }
        }
    }

    /// <summary>Queues a capture if its stored text is missing or stale. Newest captures are read first.</summary>
    internal void Enqueue(CaptureDocument document, bool priority = false)
    {
        if (!_enabled || State == OcrIndexState.Unavailable) return;
        if (_index.IsCurrent(document.Id, CaptureTextReader.IndexStamp(document))) return;
        if (_queue.Contains(document)) { if (!priority) return; _queue.Remove(document); }
        if (priority) _queue.AddFirst(document); else _queue.AddLast(document);
        if (!_running) _ = RunAsync();
    }

    internal void Remove(CaptureDocument document)
    {
        _queue.Remove(document);
        if (_index.Remove(document.Id)) TrySave();
    }

    private async Task RunAsync()
    {
        _running = true;
        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        SetState(OcrIndexState.Working);
        try
        {
            while (_queue.First is { } node && !token.IsCancellationRequested)
            {
                await Task.Delay(PauseBetweenCaptures, token);
                await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
                if (_queue.First != node) continue;
                _queue.RemoveFirst();
                await IndexAsync(node.Value, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (OcrUnavailableException ex)
        {
            _queue.Clear();
            UnavailableReason = ex.Message;
            _running = false;
            SetState(OcrIndexState.Unavailable);
            return;
        }
        finally { _running = false; }
        if (!_enabled) return;
        // Text reading was turned off and on again while the previous pass was finishing.
        if (_queue.Count > 0) { _ = RunAsync(); return; }
        SetState(OcrIndexState.Idle);
    }

    private async Task IndexAsync(CaptureDocument document, CancellationToken token)
    {
        string stamp = CaptureTextReader.IndexStamp(document);
        if (_index.IsCurrent(document.Id, stamp)) return;
        RecognizedText text;
        try { text = await CaptureTextReader.ReadVisibleAsync(document, token); }
        catch (Exception ex) when (ex is not (OperationCanceledException or OcrUnavailableException))
        {
            // One unreadable capture must not stop the rest of the library from being indexed.
            return;
        }
        if (token.IsCancellationRequested || !_enabled) return;
        _index.Set(document.Id, stamp, text.FullText);
        TrySave();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TrySave()
    {
        try { _index.Save(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void SetState(OcrIndexState state)
    {
        if (State == state) return;
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _queue.Clear();
        _enabled = false;
        TrySave();
    }
}
