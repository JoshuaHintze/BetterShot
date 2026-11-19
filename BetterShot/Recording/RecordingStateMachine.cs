namespace BetterShot.Recording;

internal enum RecordingState { Idle, Selecting, Countdown, Starting, Recording, Paused, Stopping, Completed, Failed, Canceled }

/// <summary>
/// Single source of truth for where a recording is in its lifecycle. Illegal transitions are refused (return false)
/// rather than thrown, so UI events that race (double-clicked Stop, hotkey during countdown) are harmless.
/// </summary>
internal sealed class RecordingStateMachine
{
    private static readonly Dictionary<RecordingState, RecordingState[]> Allowed = new()
    {
        [RecordingState.Idle] = [RecordingState.Selecting, RecordingState.Countdown, RecordingState.Starting],
        [RecordingState.Selecting] = [RecordingState.Countdown, RecordingState.Starting, RecordingState.Canceled, RecordingState.Failed],
        [RecordingState.Countdown] = [RecordingState.Starting, RecordingState.Canceled],
        [RecordingState.Starting] = [RecordingState.Recording, RecordingState.Failed, RecordingState.Canceled],
        [RecordingState.Recording] = [RecordingState.Paused, RecordingState.Stopping, RecordingState.Failed],
        [RecordingState.Paused] = [RecordingState.Recording, RecordingState.Stopping, RecordingState.Failed],
        [RecordingState.Stopping] = [RecordingState.Completed, RecordingState.Failed],
        [RecordingState.Completed] = [RecordingState.Idle, RecordingState.Selecting, RecordingState.Countdown, RecordingState.Starting],
        [RecordingState.Failed] = [RecordingState.Idle, RecordingState.Selecting, RecordingState.Countdown, RecordingState.Starting],
        [RecordingState.Canceled] = [RecordingState.Idle, RecordingState.Selecting, RecordingState.Countdown, RecordingState.Starting]
    };

    internal RecordingState State { get; private set; } = RecordingState.Idle;

    internal event EventHandler? Changed;

    /// <summary>True from the moment a recording is requested until it has completed, failed, or been canceled.</summary>
    internal bool IsBusy => State is not (RecordingState.Idle or RecordingState.Completed or RecordingState.Failed or RecordingState.Canceled);

    /// <summary>True while frames are being captured or the capture is paused.</summary>
    internal bool IsCapturing => State is RecordingState.Recording or RecordingState.Paused;

    internal static bool CanMove(RecordingState from, RecordingState to) =>
        Allowed.TryGetValue(from, out RecordingState[]? targets) && targets.Contains(to);

    internal bool TryMove(RecordingState next)
    {
        if (!CanMove(State, next)) return false;
        State = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
