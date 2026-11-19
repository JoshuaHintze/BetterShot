using System.Windows.Input;

namespace BetterShot.RecordingEditor;

internal enum RecordingEditorCommand
{
    None,
    PlayPause,
    PreviousFrame,
    NextFrame,
    FirstFrame,
    LastFrame,
    JumpToIn,
    JumpToOut,
    ToggleSplitMode,
    SplitAtPlayhead,
    SetIn,
    SetOut,
    DeleteToClipStart,
    DeleteToClipEnd,
    DeleteSelection,
    ToggleSnapping,
    ToggleLoop,
    Undo,
    Redo,
    Save,
    Escape,
    ShowShortcuts
}

/// <summary>
/// Keyboard shortcuts of the recording editor (CapCut-style editing keys). Pure mapping so it can be tested;
/// the window ignores it while a text box has focus.
/// </summary>
internal static class RecordingEditorKeys
{
    /// <summary>The keyboard shortcut sheet, in the order KhingShot lists them.</summary>
    internal static readonly IReadOnlyList<(string Keys, string Action)> Descriptions =
    [
        ("Space", "Play or pause"),
        ("← →   , .", "Back or forward one frame"),
        ("Home   End", "First or last frame"),
        ("I   O", "In or out point at the playhead"),
        ("Shift+I   Shift+O", "Jump to the in or out point"),
        ("B", "Split mode: clicks cut"),
        ("Ctrl+B", "Split at the playhead"),
        ("Q", "Delete from the clip's start to the playhead"),
        ("W", "Delete from the playhead to the clip's end"),
        ("Delete", "Delete the selected clip or mark"),
        ("N", "Snapping on or off"),
        ("L", "Loop the selection"),
        ("Ctrl+Z   Ctrl+Y", "Undo or redo"),
        ("Ctrl+S", "Save the edits"),
        ("Esc", "Stop typing, leave split mode, then deselect"),
        ("Ctrl+wheel", "Zoom the timeline"),
        ("?", "Show this sheet")
    ];

    internal static RecordingEditorCommand Map(Key key, ModifierKeys modifiers)
    {
        bool control = modifiers.HasFlag(ModifierKeys.Control), shift = modifiers.HasFlag(ModifierKeys.Shift), alt = modifiers.HasFlag(ModifierKeys.Alt);
        if (alt || modifiers.HasFlag(ModifierKeys.Windows)) return RecordingEditorCommand.None;
        if (control)
        {
            return key switch
            {
                Key.B when !shift => RecordingEditorCommand.SplitAtPlayhead,
                Key.Z when shift => RecordingEditorCommand.Redo,
                Key.Z => RecordingEditorCommand.Undo,
                Key.Y when !shift => RecordingEditorCommand.Redo,
                Key.S when !shift => RecordingEditorCommand.Save,
                _ => RecordingEditorCommand.None
            };
        }
        if (shift)
        {
            return key switch
            {
                Key.I => RecordingEditorCommand.JumpToIn,
                Key.O => RecordingEditorCommand.JumpToOut,
                Key.OemQuestion => RecordingEditorCommand.ShowShortcuts,
                _ => RecordingEditorCommand.None
            };
        }
        return key switch
        {
            Key.Space => RecordingEditorCommand.PlayPause,
            Key.Left or Key.OemComma => RecordingEditorCommand.PreviousFrame,
            Key.Right or Key.OemPeriod => RecordingEditorCommand.NextFrame,
            Key.Home => RecordingEditorCommand.FirstFrame,
            Key.End => RecordingEditorCommand.LastFrame,
            Key.B => RecordingEditorCommand.ToggleSplitMode,
            Key.I => RecordingEditorCommand.SetIn,
            Key.O => RecordingEditorCommand.SetOut,
            Key.Q => RecordingEditorCommand.DeleteToClipStart,
            Key.W => RecordingEditorCommand.DeleteToClipEnd,
            Key.Delete or Key.Back => RecordingEditorCommand.DeleteSelection,
            Key.N => RecordingEditorCommand.ToggleSnapping,
            Key.L => RecordingEditorCommand.ToggleLoop,
            Key.Escape => RecordingEditorCommand.Escape,
            _ => RecordingEditorCommand.None
        };
    }
}
