using System.Runtime.InteropServices;
using BetterShot.Capture;
using BetterShot.Interop;

namespace BetterShot.Recording;

internal enum RecordingTargetKind { Region, Window, Display }

/// <summary>
/// What to record. <see cref="ScreenArea"/> is the recorded area in virtual-screen physical pixels (for overlays and
/// click coordinates); <see cref="Crop"/> is the monitor-local crop for region recordings.
/// </summary>
internal sealed record RecordingTarget(RecordingTargetKind Kind, Int32Rect ScreenArea, IntPtr Monitor, Int32Rect MonitorBounds, IntPtr Window, Int32Rect? Crop);

/// <summary>Turns a selection from the capture overlay into a recordable target.</summary>
internal static class RecordingTargetResolver
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint GaRoot = 2;

    /// <summary>Returns the target, or throws <see cref="RecordingException"/> when the selection cannot be recorded.</summary>
    internal static RecordingTarget Resolve(CaptureMode mode, Int32Rect selection)
    {
        (IntPtr monitor, Int32Rect monitorBounds) = MonitorFor(selection);
        if (mode == CaptureMode.Display || mode == CaptureMode.Fullscreen)
            return new RecordingTarget(RecordingTargetKind.Display, monitorBounds, monitor, monitorBounds, IntPtr.Zero, null);
        if (mode == CaptureMode.Window && FindWindowWithBounds(selection) is IntPtr window && window != IntPtr.Zero)
            return new RecordingTarget(RecordingTargetKind.Window, selection, monitor, monitorBounds, window, null);

        Int32Rect crop = RecordingGeometry.ComputeCrop(selection, monitorBounds);
        if (crop.IsEmpty)
            throw new RecordingException(RecordingProblem.Failed, $"The selected area is too small to record. Choose at least {RecordingGeometry.MinimumDimension} × {RecordingGeometry.MinimumDimension} pixels.");
        var screenArea = new Int32Rect(monitorBounds.X + crop.X, monitorBounds.Y + crop.Y, crop.Width, crop.Height);
        return new RecordingTarget(RecordingTargetKind.Region, screenArea, monitor, monitorBounds, IntPtr.Zero, crop);
    }

    /// <summary>Current on-screen bounds of a recorded window (DWM frame bounds), used to place click coordinates.</summary>
    internal static Int32Rect? WindowBounds(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        if (NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DwmwaExtendedFrameBounds, out NativeMethods.Rect rect, Marshal.SizeOf<NativeMethods.Rect>()) != 0 &&
            !NativeMethods.GetWindowRect(window, out rect)) return null;
        return rect.Width > 0 && rect.Height > 0 ? new Int32Rect(rect.Left, rect.Top, rect.Width, rect.Height) : null;
    }

    private static (IntPtr Monitor, Int32Rect Bounds) MonitorFor(Int32Rect selection)
    {
        var rect = new NativeMethods.Rect { Left = selection.X, Top = selection.Y, Right = selection.X + Math.Max(1, selection.Width), Bottom = selection.Y + Math.Max(1, selection.Height) };
        IntPtr monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
            throw new RecordingException(RecordingProblem.Failed, "Windows did not report a display for the selected area.");
        return (monitor, new Int32Rect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Width, info.Monitor.Height));
    }

    /// <summary>The topmost visible top-level window (not BetterShot's) whose frame bounds match the selection.</summary>
    private static IntPtr? FindWindowWithBounds(Int32Rect selection)
    {
        IntPtr? match = null;
        uint ownProcess = (uint)Environment.ProcessId;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint process);
            if (process == ownProcess) return true;
            if (WindowBounds(hwnd) is Int32Rect bounds && bounds == selection)
            {
                match = GetAncestor(hwnd, GaRoot) is var root && root != IntPtr.Zero ? root : hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return match;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeMethods.Rect rect, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
