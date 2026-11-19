using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BetterShot.Recording;

/// <summary>
/// Low-level mouse and keyboard hooks used only while recording, for click highlights and shortcut keystrokes. The
/// hooks never block or alter input; callbacks just queue work and pass the event on. Install on the UI thread
/// (low-level hooks are serviced by the installing thread's message loop).
/// </summary>
internal sealed class InputActivityHook : IDisposable
{
    private const int WhKeyboardLl = 13, WhMouseLl = 14;
    private const int WmKeyDown = 0x0100, WmSysKeyDown = 0x0104;
    private const int WmLButtonDown = 0x0201, WmRButtonDown = 0x0204, WmMButtonDown = 0x0207;
    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C;
    private const int VkLShift = 0xA0, VkLControl = 0xA2, VkRMenu = 0xA5;
    private const byte KeyPressed = 0x80;
    /// <summary>ToUnicodeEx flag (Windows 10 1607+): do not change the keyboard state, so pending dead keys survive.</summary>
    private const uint ToUnicodeKeepKeyboardState = 0x4;
    private readonly HookProc _mouseCallback;
    private readonly HookProc _keyboardCallback;
    private readonly Action<int, int> _onClick;
    private readonly Action<string> _onKeyCombo;
    private IntPtr _mouseHook, _keyboardHook;

    internal InputActivityHook(Action<int, int> onClick, Action<string> onKeyCombo)
    {
        _onClick = onClick;
        _onKeyCombo = onKeyCombo;
        _mouseCallback = MouseCallback;
        _keyboardCallback = KeyboardCallback;
    }

    /// <summary>Installs the requested hooks. Returns false if Windows refused one (recording continues without it).</summary>
    internal bool Install(bool clicks, bool keys)
    {
        using Process process = Process.GetCurrentProcess();
        using ProcessModule? module = process.MainModule;
        IntPtr moduleHandle = GetModuleHandle(module?.ModuleName);
        bool ok = true;
        if (clicks) { _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseCallback, moduleHandle, 0); ok &= _mouseHook != IntPtr.Zero; }
        if (keys) { _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardCallback, moduleHandle, 0); ok &= _keyboardHook != IntPtr.Zero; }
        return ok;
    }

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _mouseHook != IntPtr.Zero && wParam.ToInt32() is WmLButtonDown or WmRButtonDown or WmMButtonDown)
        {
            int x = Marshal.ReadInt32(lParam), y = Marshal.ReadInt32(lParam, 4); // MSLLHOOKSTRUCT.pt, physical pixels.
            try { _onClick(x, y); }
            catch (Exception) { } // Never let a highlight failure disturb the user's input.
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _keyboardHook != IntPtr.Zero && wParam.ToInt32() is WmKeyDown or WmSysKeyDown)
        {
            int key = Marshal.ReadInt32(lParam);
            int scanCode = Marshal.ReadInt32(lParam, 4); // KBDLLHOOKSTRUCT.scanCode
            bool shift = IsDown(VkShift);
            string? combo = KeystrokeFormatter.Format(key, IsDown(VkControl), IsDown(VkMenu), shift, IsDown(VkLWin) || IsDown(VkRWin),
                () => TypesCharacterWithAltGr(key, scanCode, shift));
            if (combo is not null)
            {
                try { _onKeyCombo(combo); }
                catch (Exception) { }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// True when the key with Ctrl+Alt (AltGr), plus Shift if held, types a printable character or a dead key on the
    /// foreground window's keyboard layout. Uses a private keyboard state and leaves the system dead-key state untouched.
    /// </summary>
    private static bool TypesCharacterWithAltGr(int virtualKey, int scanCode, bool shift)
    {
        try
        {
            var state = new byte[256];
            state[VkControl] = state[VkLControl] = KeyPressed;
            state[VkMenu] = state[VkRMenu] = KeyPressed;
            if (shift) state[VkShift] = state[VkLShift] = KeyPressed;
            IntPtr layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero));
            var buffer = new char[8];
            int count = ToUnicodeEx((uint)virtualKey, (uint)scanCode, state, buffer, buffer.Length, ToUnicodeKeepKeyboardState, layout);
            if (count < 0) return true; // A dead key: the start of a typed character.
            for (int index = 0; index < Math.Min(count, buffer.Length); index++)
                if (!char.IsControl(buffer[index])) return true;
            return false;
        }
        catch (Exception)
        {
            return true; // If the layout cannot be checked, err on the side of not showing what may be typed text.
        }
    }

    public void Dispose()
    {
        IntPtr mouse = _mouseHook, keyboard = _keyboardHook;
        _mouseHook = IntPtr.Zero;
        _keyboardHook = IntPtr.Zero;
        if (mouse != IntPtr.Zero) UnhookWindowsHookEx(mouse);
        if (keyboard != IntPtr.Zero) UnhookWindowsHookEx(keyboard);
    }

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
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);
    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] char[] buffer, int bufferSize, uint flags, IntPtr layout);
}
