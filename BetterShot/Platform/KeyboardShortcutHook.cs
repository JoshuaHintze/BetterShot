using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BetterShot.Platform;

internal sealed class KeyboardShortcutHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly Action _onShortcut;
    private readonly HookProc _callback; // Keep the delegate rooted for the lifetime of the native hook.
    private readonly HashSet<int> _pressed = [];
    private IntPtr _hook;
    private bool _suppressSUntilReleased;
    private bool _triggeredForChord;

    internal KeyboardShortcutHook(Action onShortcut)
    {
        _onShortcut = onShortcut;
        _callback = HookCallback;
    }

    internal bool IsEnabled => _hook != IntPtr.Zero;

    internal bool Enable()
    {
        if (_hook != IntPtr.Zero) return true;
        using Process process = Process.GetCurrentProcess();
        using ProcessModule? module = process.MainModule;
        IntPtr moduleHandle = GetModuleHandle(module?.ModuleName);
        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, moduleHandle, 0);
        return _hook != IntPtr.Zero;
    }

    internal void Disable()
    {
        IntPtr hook = _hook;
        _hook = IntPtr.Zero; // Fail open before calling native code.
        _pressed.Clear();
        _suppressSUntilReleased = false;
        _triggeredForChord = false;
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || _hook == IntPtr.Zero) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        int message = wParam.ToInt32();
        bool keyDown = message is WmKeyDown or WmSysKeyDown;
        bool keyUp = message is WmKeyUp or WmSysKeyUp;
        if (!keyDown && !keyUp) return CallNextHookEx(_hook, code, wParam, lParam);

        int key = Marshal.ReadInt32(lParam);
        if (keyDown)
        {
            _pressed.Add(key);
            ShortcutClassifier.PruneReleasedKeys(_pressed, key, k => (GetAsyncKeyState(k) & 0x8000) != 0);
            if (ShortcutClassifier.ShouldSuppressKeyDown(key, _pressed))
            {
                _suppressSUntilReleased = true;
                if (!_triggeredForChord)
                {
                    _triggeredForChord = true;
                    try { _onShortcut(); }
                    catch
                    {
                        Disable(); // A failing callback must never leave interception active.
                        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam); // Fail open; do not swallow S.
                    }
                }
                return new IntPtr(1);
            }
            if (_suppressSUntilReleased && key == ShortcutClassifier.S) return new IntPtr(1);
        }
        else
        {
            bool suppress = ShortcutClassifier.ShouldSuppressKeyUp(key, _suppressSUntilReleased);
            _pressed.Remove(key);
            if (key == ShortcutClassifier.S)
            {
                _suppressSUntilReleased = false;
                _triggeredForChord = false;
            }
            if (suppress) return new IntPtr(1);
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => Disable();

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
