using System.Runtime.InteropServices;

namespace BetterShot.Platform;

internal static class GlobalHotKey
{
    internal const int WmHotKey = 0x0312;
    internal const int Id = 0x4253;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF9 = 0x78;

    internal static bool Register(IntPtr hwnd) => RegisterHotKey(hwnd, Id, ModControl | ModShift | ModNoRepeat, VkF9);
    internal static void Unregister(IntPtr hwnd) => UnregisterHotKey(hwnd, Id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
