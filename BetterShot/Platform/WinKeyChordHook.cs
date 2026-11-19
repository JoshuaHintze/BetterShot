using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BetterShot.Platform;

/// <summary>
/// Low-level keyboard hook for shortcuts that include the Windows key. Windows keeps many Win combinations for itself
/// (for example Win + Shift + R starts Snipping Tool recording), so RegisterHotKey refuses them and the keys never reach
/// a focused window. A low-level hook sees the keys before the shell does and can claim the chord.
/// </summary>
internal sealed class WinKeyChordHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const int LlkhfInjected = 0x10;
    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C;
    /// <summary>An unassigned virtual key, sent so releasing Win after a claimed chord does not open Start.</summary>
    private const ushort VkStartMenuMask = 0xE8;

    private readonly Func<HotkeyBinding, bool> _onChord;
    private readonly HookProc _callback; // Keep the delegate rooted for the lifetime of the native hook.
    private IntPtr _hook;
    private int _claimedKey;

    /// <param name="onChord">Called on the hook thread for each Win + key press; return true to claim (swallow) it. Keep it fast.</param>
    internal WinKeyChordHook(Func<HotkeyBinding, bool> onChord)
    {
        _onChord = onChord;
        _callback = HookCallback;
    }

    internal bool IsEnabled => _hook != IntPtr.Zero;

    internal bool Enable()
    {
        if (_hook != IntPtr.Zero) return true;
        using Process process = Process.GetCurrentProcess();
        using ProcessModule? module = process.MainModule;
        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(module?.ModuleName), 0);
        return _hook != IntPtr.Zero;
    }

    internal void Disable()
    {
        IntPtr hook = _hook;
        _hook = IntPtr.Zero; // Fail open before calling native code.
        _claimedKey = 0;
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
    }

    /// <summary>The modifiers held right now, in RegisterHotKey terms.</summary>
    internal static HotkeyModifiers CurrentModifiers()
    {
        static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        if (Down(VkLWin) || Down(VkRWin)) modifiers |= HotkeyModifiers.Windows;
        if (Down(VkControl)) modifiers |= HotkeyModifiers.Control;
        if (Down(VkMenu)) modifiers |= HotkeyModifiers.Alt;
        if (Down(VkShift)) modifiers |= HotkeyModifiers.Shift;
        return modifiers;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || _hook == IntPtr.Zero) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        int message = wParam.ToInt32();
        bool keyDown = message is WmKeyDown or WmSysKeyDown;
        bool keyUp = message is WmKeyUp or WmSysKeyUp;
        int key = Marshal.ReadInt32(lParam);
        int flags = Marshal.ReadInt32(lParam, 8);
        if ((!keyDown && !keyUp) || (flags & LlkhfInjected) != 0) return CallNextHookEx(_hook, code, wParam, lParam);

        if (keyUp)
        {
            if (key != _claimedKey) return CallNextHookEx(_hook, code, wParam, lParam);
            _claimedKey = 0;
            return new IntPtr(1); // Pair the swallowed key-down with a swallowed key-up.
        }

        if (key == _claimedKey) return new IntPtr(1); // Auto-repeat of a claimed chord.
        if (HotkeyBinding.IsModifierKey(key)) return CallNextHookEx(_hook, code, wParam, lParam);
        HotkeyModifiers modifiers = CurrentModifiers();
        if (!modifiers.HasFlag(HotkeyModifiers.Windows)) return CallNextHookEx(_hook, code, wParam, lParam);

        bool claimed;
        try { claimed = _onChord(new HotkeyBinding(modifiers, key)); }
        catch
        {
            Disable(); // A failing callback must never leave interception active.
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }
        if (!claimed) return CallNextHookEx(_hook, code, wParam, lParam);
        _claimedKey = key;
        MaskStartMenu();
        return new IntPtr(1);
    }

    private static void MaskStartMenu()
    {
        var inputs = new Input[]
        {
            new() { Type = 1, Keyboard = new KeyboardInput { VirtualKey = VkStartMenuMask } },
            new() { Type = 1, Keyboard = new KeyboardInput { VirtualKey = VkStartMenuMask, Flags = 0x0002 } }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    public void Dispose() => Disable();

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        private InputUnion _union;
        public KeyboardInput Keyboard { readonly get => _union.Keyboard; set => _union.Keyboard = value; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
