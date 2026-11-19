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
    Escape
}

/// <summary>
/// Keyboard shortcuts of the recording editor (CapCut-style editing keys). Pure mapping so it can be tested;
/// the window ignores it while a text box has focus.
/// </summary>
internal static class RecordingEditorKeys
{
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
