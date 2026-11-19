using System.Globalization;

namespace BetterShot.Platform;

public enum HotkeyAction { Area, Window, Fullscreen, Library, Record, Scrolling }

/// <summary>Values match the RegisterHotKey MOD_* flags and WPF ModifierKeys.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008
}

/// <summary>A global shortcut expressed as RegisterHotKey modifiers plus a Win32 virtual-key code. VirtualKey 0 means unassigned.</summary>
public readonly record struct HotkeyBinding(HotkeyModifiers Modifiers, int VirtualKey)
{
    private const int VkF1 = 0x70, VkF12 = 0x7B, VkF13 = 0x7C, VkF24 = 0x87;
    private const int VkPause = 0x13, VkSnapshot = 0x2C, VkS = 0x53;

    public static HotkeyBinding None => default;
    /// <summary>The always-registered Ctrl+Shift+F9 area capture fallback.</summary>
    public static HotkeyBinding AreaFallback => new(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x78);
    /// <summary>The Windows Snipping shortcut that BetterShot can optionally take over through its keyboard hook.</summary>
    public static HotkeyBinding WindowsSnip => new(HotkeyModifiers.Windows | HotkeyModifiers.Shift, VkS);

    public bool IsEmpty => VirtualKey == 0;

    public override string ToString()
    {
        if (IsEmpty) return "Not set";
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyName(VirtualKey));
        return string.Join(" + ", parts);
    }

    public static string ModifierPreview(HotkeyModifiers modifiers)
    {
        if (modifiers == HotkeyModifiers.None) return string.Empty;
        const string placeholderSuffix = " + A";
        string text = new HotkeyBinding(modifiers, 0x41).ToString();
        return text[..^placeholderSuffix.Length];
    }

    internal static bool IsModifierKey(int virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;

    internal static bool IsSupportedKey(int virtualKey) => NamedKey(virtualKey) is not null;

    /// <summary>Returns null when the binding is acceptable, otherwise a user-facing reason.</summary>
    public static string? Validate(HotkeyBinding binding)
    {
        if (binding.IsEmpty) return null;
        if (IsModifierKey(binding.VirtualKey)) return "Add a key such as a letter or F-key to the modifiers.";
        if (!IsSupportedKey(binding.VirtualKey)) return "That key can't be used for a global shortcut.";
        bool function = binding.VirtualKey is >= VkF1 and <= VkF24;
        bool bareAllowed = binding.VirtualKey is VkSnapshot or VkPause or >= VkF13 and <= VkF24;
        HotkeyModifiers strong = binding.Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows);
        if (binding.Modifiers == HotkeyModifiers.None && !bareAllowed)
            return "Use Ctrl, Alt, or Win with this key so normal typing isn't intercepted.";
        if (strong == HotkeyModifiers.None && binding.Modifiers.HasFlag(HotkeyModifiers.Shift) && !function && !bareAllowed)
            return "Shift alone would block typing capitals. Add Ctrl, Alt, or Win.";
        if (binding == WindowsSnip) return "Win + Shift + S is controlled by the toggle above.";
        return null;
    }

    public static string KeyName(int virtualKey) =>
        NamedKey(virtualKey) ?? $"Key 0x{virtualKey.ToString("X2", CultureInfo.InvariantCulture)}";

    private static string? NamedKey(int key) => key switch
    {
        >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x30 and <= 0x39 => ((char)key).ToString(),
        >= 0x60 and <= 0x69 => $"Num {key - 0x60}",
        >= VkF1 and <= VkF24 => $"F{key - VkF1 + 1}",
        VkSnapshot => "Print Screen",
        VkPause => "Pause",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        0x6A => "Num *",
        0x6B => "Num +",
        0x6D => "Num -",
        0x6E => "Num .",
        0x6F => "Num /",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => null
    };

    /// <summary>Pure checks for the binding display and validation rules used by diagnostics.</summary>
    internal static void RunSelfTest()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Hotkey self-test failed: " + message);
        }

        Require(AreaFallback.ToString() == "Ctrl + Shift + F9", "fallback display");
        Require(WindowsSnip.ToString() == "Win + Shift + S", "Win+Shift+S display");
        Require(new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x34).ToString() == "Ctrl + Alt + 4", "digit display");
        Require(None.ToString() == "Not set" && Validate(None) is null, "unassigned binding");
        Require(Validate(new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x41)) is null, "Ctrl+Shift+A accepted");
        Require(Validate(new HotkeyBinding(HotkeyModifiers.None, 0x41)) is not null, "bare letter rejected");
        Require(Validate(new HotkeyBinding(HotkeyModifiers.Shift, 0x41)) is not null, "Shift+letter rejected");
        Require(Validate(new HotkeyBinding(HotkeyModifiers.Shift, 0x78)) is null, "Shift+F9 accepted");
        Require(Validate(new HotkeyBinding(HotkeyModifiers.None, VkSnapshot)) is null, "bare Print Screen accepted");
        Require(Validate(new HotkeyBinding(HotkeyModifiers.None, 0x78)) is not null, "bare F9 rejected");
        Require(Validate(new HotkeyBinding(HotkeyModifiers.Control, 0xA2)) is not null, "modifier-only rejected");
        Require(Validate(WindowsSnip) is not null, "Win+Shift+S reserved for the hook toggle");
        Require(ModifierPreview(HotkeyModifiers.Control | HotkeyModifiers.Shift) == "Ctrl + Shift", "modifier preview");
    }
}
