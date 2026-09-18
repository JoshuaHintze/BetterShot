using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterShot.Interop;

internal static class NativeMethods
{
    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;
    internal const int SmCxDrag = 68;
    internal const int SmCyDrag = 69;
    internal const int Srccopy = 0x00CC0020;
    internal const int Captureblt = 0x40000000;
    internal const int SwpNoActivate = 0x0010;
    internal const int GwlExStyle = -20;
    internal const int WsExToolWindow = 0x00000080;
    internal const int WsExNoActivate = 0x08000000;
    internal const int DwmwaCloaked = 14;
    internal const int DwmwaExtendedFrameBounds = 9;

    internal static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { internal int X; internal int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left; internal int Top; internal int Right; internal int Bottom;
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
        internal bool Contains(Point p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
    }

    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
    }

    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, int rop);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    internal static IReadOnlyList<Rect> GetMonitorRects()
    {
        var result = new List<Rect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref Rect __, IntPtr ___) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info)) result.Add(info.Monitor);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsShellSurface(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        if (GetClassName(hwnd, name, name.Capacity) == 0) return false;
        return name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    internal static Rect? FindWindowAt(Point point, ISet<IntPtr> excluded)
    {
        Rect? match = null;
        uint ownProcess = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (excluded.Contains(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd) || GetWindowTextLength(hwnd) == 0 || IsShellSurface(hwnd))
                return true;
            GetWindowThreadProcessId(hwnd, out uint process);
            if (process == ownProcess)
                return true;
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            Rect rect;
            if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(hwnd, out rect))
                return true;
            if (rect.Width > 0 && rect.Height > 0 && rect.Contains(point))
            {
                match = rect;
                return false; // EnumWindows is in top-to-bottom Z order.
            }
            return true;
        }, IntPtr.Zero);
        return match;
    }
}
