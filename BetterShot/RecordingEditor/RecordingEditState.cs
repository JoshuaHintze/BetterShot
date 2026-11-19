namespace BetterShot.RecordingEditor;

/// <summary>Everything the recording editor can undo: the cut list, the marks, and whether markup is hidden.</summary>
internal sealed record RecordingEditState(EditTimeline Timeline, IReadOnlyList<VideoMark> Marks, bool HideMarkup)
{
    internal static RecordingEditState Create(double duration) => new(EditTimeline.Create(duration), [], false);

    internal bool HasRedaction => Marks.Any(mark => mark.IsRedaction);

    /// <summary>
    /// Marks drawn in the player and burned into exports. "Hide markup" hides drawn marks, but redactions always stay
    /// covered so hiding markup can never reveal what a redaction was hiding.
    /// </summary>
    internal IEnumerable<VideoMark> VisibleMarks => HideMarkup ? Marks.Where(mark => mark.IsRedaction) : Marks;

    internal bool HasVisibleMarks => VisibleMarks.Any();

    /// <summary>True when an export differs from the original movie (cuts, in/out, or visible marks).</summary>
    internal bool HasEdits => Timeline.HasCuts || HasVisibleMarks;

    internal VideoMark? Find(Guid id) => Marks.FirstOrDefault(mark => mark.Id == id);

    internal RecordingEditState WithMark(VideoMark updated) =>
        this with { Marks = Marks.Select(mark => mark.Id == updated.Id ? updated : mark).ToArray() };

    internal RecordingEditState AddMark(VideoMark mark) => this with { Marks = Marks.Append(mark).ToArray() };

    internal RecordingEditState RemoveMark(Guid id) => this with { Marks = Marks.Where(mark => mark.Id != id).ToArray() };

    /// <summary>Marks visible at a source time, in drawing order.</summary>
    internal IEnumerable<VideoMark> VisibleMarksAt(double time) => VisibleMarks.Where(mark => mark.IsActiveAt(time));

    internal bool BlursWholePictureAt(double time) => Marks.Any(mark => mark.BlursWholePictureAt(time));
}

/// <summary>Undo/redo history of <see cref="RecordingEditState"/> snapshots and the saved/unsaved flag.</summary>
internal sealed class RecordingEditHistory
{
    private const int Limit = 200;
    private readonly LinkedList<RecordingEditState> _undo = new();
    private readonly Stack<RecordingEditState> _redo = new();
    private RecordingEditState _saved;
    private string? _coalesceKey;

    internal RecordingEditHistory(RecordingEditState initial)
    {
        Current = initial;
        _saved = initial;
    }

    internal RecordingEditState Current { get; private set; }
    internal bool CanUndo => _undo.Count > 0;
    internal bool CanRedo => _redo.Count > 0;
    internal bool IsDirty => !ReferenceEquals(Current, _saved);
    internal event EventHandler? Changed;

    /// <summary>
    /// Applies an edit. Consecutive edits with the same <paramref name="coalesceKey"/> (for example, one drag) share
    /// one undo step. Edits that change nothing are ignored.
    /// </summary>
    internal bool Apply(Func<RecordingEditState, RecordingEditState> edit, string? coalesceKey = null)
    {
        RecordingEditState next = edit(Current);
        if (ReferenceEquals(next, Current) || SameContent(next, Current)) return false;
        if (coalesceKey is null || coalesceKey != _coalesceKey || _undo.Count == 0)
        {
            _undo.AddLast(Current);
            if (_undo.Count > Limit) _undo.RemoveFirst();
        }
        _redo.Clear();
        _coalesceKey = coalesceKey;
        Current = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Ends the current coalescing group so the next edit gets its own undo step.</summary>
    internal void EndGroup() => _coalesceKey = null;

    internal bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(Current);
        Current = _undo.Last!.Value;
        _undo.RemoveLast();
        _coalesceKey = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.AddLast(Current);
        Current = _redo.Pop();
        _coalesceKey = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal void MarkSaved() => _saved = Current;

    /// <summary>Replaces the whole state without an undo step (after the movie itself was trimmed).</summary>
    internal void Reset(RecordingEditState state)
    {
        _undo.Clear();
        _redo.Clear();
        _coalesceKey = null;
        Current = state;
        _saved = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameContent(RecordingEditState first, RecordingEditState second) =>
        ReferenceEquals(first.Timeline, second.Timeline) && first.HideMarkup == second.HideMarkup && first.Marks.SequenceEqual(second.Marks);
}
