using BetterShot.Interop;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BetterShot.Presentation;

/// <summary>Native window tweaks shared by the recording overlays and controls.</summary>
internal static class RecordingWindowStyles
{
    private const int WsExTransparent = 0x00000020;
    private const uint WdaNone = 0x0, WdaExcludeFromCapture = 0x11;

    /// <summary>Tool window that never takes focus; optionally lets every click fall through to the window below.</summary>
    internal static void MakeNonActivating(Window window, bool clickThrough)
    {
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        long style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        style |= NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow;
        if (clickThrough) style |= WsExTransparent;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(style));
    }

    /// <summary>
    /// Hides the window from screen capture (Windows 10 2004+), so recording controls and the dim overlay never appear
    /// in the movie. Returns false on older Windows; the caller keeps recording with the window visible.
    /// </summary>
    internal static bool ExcludeFromCapture(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        return SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
    }

    internal static void IncludeInCapture(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) SetWindowDisplayAffinity(handle, WdaNone);
    }

    /// <summary>Moves/sizes the window in physical pixels and returns its DPI scale on the destination monitor.</summary>
    internal static double PlacePhysical(Window window, Int32Rect bounds)
    {
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        // Move first so WPF picks up the destination monitor's DPI, then size in physical pixels.
        NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, bounds.X, bounds.Y, 0, 0, NativeMethods.SwpNoActivate | 0x0001);
        NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height, NativeMethods.SwpNoActivate);
        return ScaleOf(handle);
    }

    internal static double ScaleOf(Window window) => ScaleOf(new WindowInteropHelper(window).EnsureHandle());

    private static double ScaleOf(IntPtr handle) => Math.Max(96u, GetDpiForWindow(handle)) / 96d;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
