using BetterShot.Platform;

namespace BetterShot.Recording;

/// <summary>
/// Decides which keystrokes are shown and logged while recording. Only shortcut combinations that include Ctrl, Alt,
/// or Win are shown (e.g. "Ctrl+Shift+K"); plain typing and Shift+letter are never shown or logged.
/// </summary>
internal static class KeystrokeFormatter
{
    private const int VkBack = 0x08, VkTab = 0x09, VkReturn = 0x0D, VkEscape = 0x1B;

    internal static string? Format(int virtualKey, bool control, bool alt, bool shift, bool windows)
    {
        if (HotkeyBinding.IsModifierKey(virtualKey) || !(control || alt || windows)) return null;
        string? key = KeyLabel(virtualKey);
        if (key is null) return null;
        var parts = new List<string>(5);
        if (windows) parts.Add("Win");
        if (control) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(key);
        return string.Join("+", parts);
    }

    private static string? KeyLabel(int virtualKey) => virtualKey switch
    {
        VkBack => "Backspace",
        VkTab => "Tab",
        VkReturn => "Enter",
        VkEscape => "Esc",
        _ when HotkeyBinding.IsSupportedKey(virtualKey) => HotkeyBinding.KeyName(virtualKey),
        _ => null
    };
}
