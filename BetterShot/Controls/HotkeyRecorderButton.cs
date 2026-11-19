using BetterShot.Platform;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterShot.Controls;

public sealed class HotkeyRecordedEventArgs(HotkeyBinding binding) : EventArgs
{
    public HotkeyBinding Binding { get; } = binding;
}

/// <summary>
/// Click, then press a key combination. Esc cancels, Backspace/Delete clears. The owner validates and
/// registers the result; while recording, the owner should suspend global hotkeys so the combination reaches this control.
/// </summary>
public sealed class HotkeyRecorderButton : Button
{
    private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF3));
    private static readonly Brush UnsetBrush = new SolidColorBrush(Color.FromRgb(0x9B, 0x9E, 0xA5));
    private static readonly Brush RecordingBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x4A));
    private static readonly Brush ProblemBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x87, 0x87));
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private HotkeyBinding _binding;
    private bool _unavailable;
    /// <summary>Catches Win-key combinations while listening; Windows handles many of them before this control sees the keys.</summary>
    private WinKeyChordHook? _winHook;

    public event EventHandler? RecordingStarted;
    public event EventHandler? RecordingEnded;
    public event EventHandler<HotkeyRecordedEventArgs>? Recorded;

    public HotkeyRecorderButton()
    {
        Content = _label;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        UpdateLabel();
    }

    public bool IsRecording { get; private set; }

    public HotkeyBinding Binding
    {
        get => _binding;
        set { _binding = value; UpdateLabel(); }
    }

    /// <summary>Marks a saved shortcut that Windows refused to register (shown in red until fixed).</summary>
    public bool IsUnavailable
    {
        get => _unavailable;
        set { _unavailable = value; UpdateLabel(); }
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (IsRecording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        IsRecording = true;
        Focus();
        Keyboard.Focus(this);
        RecordingStarted?.Invoke(this, EventArgs.Empty);
        ShowPrompt(HotkeyModifiers.None);
        _winHook = new WinKeyChordHook(binding =>
        {
            Dispatcher.BeginInvoke(() => { if (IsRecording) Finish(binding); });
            return true;
        });
        if (!_winHook.Enable()) _winHook = null;
    }

    private void StopRecording()
    {
        if (!IsRecording) return;
        IsRecording = false;
        _winHook?.Dispose();
        _winHook = null;
        UpdateLabel();
        RecordingEnded?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsRecording) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        Key key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key
        };
        HotkeyModifiers modifiers = (HotkeyModifiers)(uint)Keyboard.Modifiers;
        if (key == Key.Escape && modifiers == HotkeyModifiers.None) { StopRecording(); return; }
        if (key is Key.Back or Key.Delete && modifiers == HotkeyModifiers.None) { Finish(HotkeyBinding.None); return; }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || HotkeyBinding.IsModifierKey(virtualKey)) { ShowPrompt(modifiers); return; }
        Finish(new HotkeyBinding(modifiers, virtualKey));
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (!IsRecording) { base.OnPreviewKeyUp(e); return; }
        e.Handled = true;
        ShowPrompt((HotkeyModifiers)(uint)Keyboard.Modifiers);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        StopRecording();
    }

    private void Finish(HotkeyBinding binding)
    {
        StopRecording();
        Recorded?.Invoke(this, new HotkeyRecordedEventArgs(binding));
    }

    private void ShowPrompt(HotkeyModifiers modifiers)
    {
        string held = HotkeyBinding.ModifierPreview(modifiers);
        _label.Text = held.Length == 0 ? "Press keys… (Esc cancels)" : held + " + …";
        _label.Foreground = RecordingBrush;
    }

    private void UpdateLabel()
    {
        if (IsRecording) return;
        _label.Text = _binding.IsEmpty ? "Not set" : _unavailable ? _binding + " (unavailable)" : _binding.ToString();
        _label.Foreground = _unavailable ? ProblemBrush : _binding.IsEmpty ? UnsetBrush : IdleBrush;
        ToolTip = _binding.IsEmpty
            ? "Click, then press a shortcut. Esc cancels."
            : "Click, then press a new shortcut. Esc cancels; Backspace clears.";
    }
}
