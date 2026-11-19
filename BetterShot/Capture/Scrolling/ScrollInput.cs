using BetterShot.Interop;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace BetterShot.Capture.Scrolling;

/// <summary>
/// Scrolls the content under a screen point. The primary path injects mouse-wheel input at that point and
/// immediately returns the pointer to where the user left it, so the Stop button stays reachable. The fallback
/// uses UI Automation's ScrollPattern for apps that ignore synthesized wheel input.
/// </summary>
internal static class ScrollInput
{
    private const int WheelDelta = 120;
    private const uint InputMouse = 0;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventVirtualDesk = 0x4000;

    /// <summary>Sends <paramref name="notches"/> downward wheel notches at the physical-pixel point.</summary>
    internal static bool SendWheel(int x, int y, int notches)
    {
        if (notches <= 0) return true;
        if (!NativeMethods.GetCursorPos(out NativeMethods.Point original)) original = new NativeMethods.Point { X = x, Y = y };
        Input[] inputs =
        [
            MoveTo(x, y),
            new Input { Type = InputMouse, Mouse = new MouseInput { MouseData = unchecked((uint)(-WheelDelta * notches)), Flags = MouseEventWheel } },
            MoveTo(original.X, original.Y)
        ];
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    /// <summary>Scrolls the nearest UI Automation scroll container under the point by small increments. Returns false if none supports it.</summary>
    internal static bool TryScrollWithAutomation(int x, int y, int increments)
    {
        try
        {
            AutomationElement? element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            TreeWalker walker = TreeWalker.ControlViewWalker;
            for (int depth = 0; element is not null && depth < 12; depth++)
            {
                if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out object pattern) &&
                    pattern is ScrollPattern scroll && scroll.Current.VerticallyScrollable)
                {
                    for (int step = 0; step < Math.Max(1, increments); step++) scroll.ScrollVertical(ScrollAmount.SmallIncrement);
                    return true;
                }
                element = walker.GetParent(element);
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (COMException) { }
        return false;
    }

    private static Input MoveTo(int x, int y)
    {
        int left = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        int top = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
        int width = Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen));
        int height = Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));
        return new Input
        {
            Type = InputMouse,
            Mouse = new MouseInput
            {
                X = (int)Math.Round((x - left) * 65535.0 / (width - 1)),
                Y = (int)Math.Round((y - top) * 65535.0 / (height - 1)),
                Flags = MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal IntPtr ExtraInfo;
    }

    /// <summary>INPUT with the union sized for MOUSEINPUT, the largest member on x64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal MouseInput Mouse;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
