using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BetterShot.Presentation;

internal static class DragPreviewDiagnostics
{
    internal static void VerifyVisible(Window preview)
    {
        var handle = new WindowInteropHelper(preview).Handle;
        const int required = 0x20 | 0x80 | 0x08000000 | 0x00080000;
        int styles = GetWindowLong(handle, -20);
        if (handle == IntPtr.Zero || !IsWindowVisible(handle) || (styles & required) != required)
            throw new InvalidOperationException("Drag preview must be visible, layered, click-through, nonactivating, and a tool window.");
        if (preview.ShowInTaskbar || preview.ShowActivated || preview.IsHitTestVisible)
            throw new InvalidOperationException("Drag preview must not become an input target or taskbar entry.");
    }

    internal static void VerifyClosed(IntPtr handle)
    {
        if (IsWindow(handle)) throw new InvalidOperationException("Drag preview leaked its native window after disposal.");
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);
}
