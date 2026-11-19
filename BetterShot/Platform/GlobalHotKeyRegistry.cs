using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BetterShot.Platform;

/// <summary>
/// Registers the user-configurable RegisterHotKey shortcuts on the main window. The Ctrl+Shift+F9
/// fallback stays in <see cref="GlobalHotKey"/> with its own id and is never touched here.
/// </summary>
internal sealed class GlobalHotKeyRegistry : IDisposable
{
    private const int FirstId = 0x4260;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private readonly Dictionary<HotkeyAction, HotkeyBinding> _bindings = new();
    private readonly HashSet<HotkeyAction> _registered = new();
    private IntPtr _window;
    private bool _suspended;
    private WinKeyChordHook? _winHook;

    /// <summary>
    /// Raised on the UI thread's hook callback when a Win-key shortcut is pressed. Win combinations go through a low-level
    /// hook instead of RegisterHotKey because Windows reserves many of them (Win + Shift + R is Snipping Tool recording).
    /// </summary>
    internal event Action<HotkeyAction>? HookShortcutPressed;

    internal void Attach(IntPtr window) => _window = window;

    private static bool UsesHook(HotkeyBinding binding) => binding.Modifiers.HasFlag(HotkeyModifiers.Windows);

    internal static int IdFor(HotkeyAction action) => FirstId + (int)action;

    internal bool TryGetAction(int id, out HotkeyAction action)
    {
        action = (HotkeyAction)(id - FirstId);
        return id >= FirstId && Enum.IsDefined(action) && _registered.Contains(action);
    }

    internal HotkeyBinding BindingFor(HotkeyAction action) => _bindings.GetValueOrDefault(action);
    internal bool IsRegistered(HotkeyAction action) => _registered.Contains(action);

    /// <summary>Replaces the binding for an action. Returns null on success, or a user-facing reason; on failure the action is left unregistered.</summary>
    internal string? Register(HotkeyAction action, HotkeyBinding binding)
    {
        Unregister(action);
        _bindings[action] = binding;
        if (binding.IsEmpty || _suspended) return null;
        return RegisterNative(action, binding);
    }

    /// <summary>Temporarily releases every configurable shortcut, e.g. while the recorder listens for a new combination.</summary>
    internal void Suspend()
    {
        if (_suspended) return;
        _suspended = true;
        foreach (HotkeyAction action in _registered.ToArray()) UnregisterNative(action);
    }

    /// <summary>Restores shortcuts released by <see cref="Suspend"/>. Returns failures by action.</summary>
    internal IReadOnlyDictionary<HotkeyAction, string> Resume()
    {
        var failures = new Dictionary<HotkeyAction, string>();
        if (!_suspended) return failures;
        _suspended = false;
        foreach ((HotkeyAction action, HotkeyBinding binding) in _bindings)
        {
            if (binding.IsEmpty || _registered.Contains(action)) continue;
            if (RegisterNative(action, binding) is string error) failures[action] = error;
        }
        return failures;
    }

    private string? RegisterNative(HotkeyAction action, HotkeyBinding binding)
    {
        if (_window == IntPtr.Zero) return "BetterShot's window is not ready yet.";
        if (UsesHook(binding))
        {
            _winHook ??= new WinKeyChordHook(OnWinChord);
            if (!_winHook.Enable()) return $"Windows refused the keyboard hook needed for {binding}.";
            _registered.Add(action);
            return null;
        }
        if (RegisterHotKey(_window, IdFor(action), (uint)binding.Modifiers | ModNoRepeat, (uint)binding.VirtualKey))
        {
            _registered.Add(action);
            return null;
        }
        int error = Marshal.GetLastWin32Error();
        return error == ErrorHotkeyAlreadyRegistered
            ? $"{binding} is already used by Windows or another app."
            : $"Windows refused {binding}: {new Win32Exception(error).Message}";
    }

    private void Unregister(HotkeyAction action)
    {
        UnregisterNative(action);
        _bindings.Remove(action);
    }

    private void UnregisterNative(HotkeyAction action)
    {
        if (!_registered.Remove(action) || _window == IntPtr.Zero) return;
        if (UsesHook(_bindings.GetValueOrDefault(action)))
        {
            if (!_registered.Any(other => UsesHook(_bindings.GetValueOrDefault(other)))) _winHook?.Disable();
            return;
        }
        UnregisterHotKey(_window, IdFor(action));
    }

    private bool OnWinChord(HotkeyBinding pressed)
    {
        foreach (HotkeyAction action in _registered)
        {
            if (_bindings.GetValueOrDefault(action) != pressed) continue;
            HookShortcutPressed?.Invoke(action);
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        foreach (HotkeyAction action in _registered.ToArray()) UnregisterNative(action);
        _bindings.Clear();
        _winHook?.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
