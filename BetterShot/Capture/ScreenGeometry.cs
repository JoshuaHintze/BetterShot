using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace BetterShot.Capture;

/// <summary>Pure physical-pixel rectangle operations used by selection and capture.</summary>
public static class ScreenGeometry
{
    public static Int32Rect Normalize(Point first, Point second)
    {
        int left = (int)Math.Floor(Math.Min(first.X, second.X));
        int top = (int)Math.Floor(Math.Min(first.Y, second.Y));
        int right = (int)Math.Ceiling(Math.Max(first.X, second.X));
        int bottom = (int)Math.Ceiling(Math.Max(first.Y, second.Y));
        return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        int left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width);
        int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right <= left || bottom <= top ? Int32Rect.Empty : new Int32Rect(left, top, right - left, bottom - top);
    }

    /// <summary>Returns the monitor containing the point, otherwise the nearest monitor, or null when there are none.</summary>
    public static Int32Rect? MonitorAt(IReadOnlyList<Int32Rect> monitors, int x, int y)
    {
        Int32Rect? nearest = null;
        long nearestDistance = long.MaxValue;
        foreach (Int32Rect monitor in monitors)
        {
            if (monitor.Width <= 0 || monitor.Height <= 0) continue;
            long dx = x < monitor.X ? monitor.X - x : x >= monitor.X + monitor.Width ? x - (monitor.X + monitor.Width - 1) : 0;
            long dy = y < monitor.Y ? monitor.Y - y : y >= monitor.Y + monitor.Height ? y - (monitor.Y + monitor.Height - 1) : 0;
            long distance = dx * dx + dy * dy;
            if (distance == 0) return monitor;
            if (distance < nearestDistance) { nearestDistance = distance; nearest = monitor; }
        }
        return nearest;
    }

    /// <summary>Converts a monitor-local physical-pixel rectangle to WPF DIPs.</summary>
    public static Rect PhysicalToDip(Int32Rect pixels, double dpiX, double dpiY)
    {
        if (dpiX <= 0 || dpiY <= 0) throw new ArgumentOutOfRangeException(nameof(dpiX));
        return new Rect(pixels.X * 96.0 / dpiX, pixels.Y * 96.0 / dpiY,
            pixels.Width * 96.0 / dpiX, pixels.Height * 96.0 / dpiY);
    }

    /// <summary>Throws if the core negative-origin, clipping, and mixed-DPI invariants fail.</summary>
    public static void RunSelfTest()
    {
        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Capture geometry self-test failed: " + message);
        }

        var negative = Normalize(new Point(100, 80), new Point(-1920, -200));
        Require(negative == new Int32Rect(-1920, -200, 2020, 280), "negative virtual-screen origin");
        var clipped = Intersect(new Int32Rect(-200, -100, 500, 300), new Int32Rect(0, 0, 1920, 1080));
        Require(clipped == new Int32Rect(0, 0, 300, 200), "virtual-screen clamping");
        Require(Intersect(new Int32Rect(-10, -10, 5, 5), new Int32Rect(0, 0, 10, 10)).IsEmpty,
            "disjoint clamping");
        var dips = PhysicalToDip(new Int32Rect(300, 150, 600, 300), 144, 144);
        Require(dips == new Rect(200, 100, 400, 200), "150 percent DPI conversion");
        Int32Rect[] monitors = [new(-1920, 0, 1920, 1080), new(0, 0, 2560, 1440)];
        Require(MonitorAt(monitors, -5, 500) == monitors[0], "cursor on negative-origin monitor");
        Require(MonitorAt(monitors, 0, 0) == monitors[1], "cursor on primary origin");
        Require(MonitorAt(monitors, -100, 1300) == monitors[1], "cursor below shorter monitor is nearer the taller monitor");
        Require(MonitorAt(monitors, -300, 1300) == monitors[0], "cursor farther to the left is nearer the shorter monitor");
        Require(MonitorAt([], 0, 0) is null, "no monitors");
        SelectionSession.RunGestureSelfTest();
    }
}
