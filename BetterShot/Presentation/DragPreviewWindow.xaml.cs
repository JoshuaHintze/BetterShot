using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace BetterShot.Presentation;

internal partial class DragPreviewWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int CursorOffset = 16;
    private nint _handle;

    internal DragPreviewWindow(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        InitializeComponent();
        PreviewImage.Source = bitmap;
        Size previewSize = CalculatePreviewSize(bitmap.PixelWidth, bitmap.PixelHeight);
        Width = previewSize.Width;
        Height = previewSize.Height;
        SourceInitialized += (_, _) => ApplyNativeStyles();
    }

    internal void UpdatePosition()
    {
        if (_handle == 0 || !GetCursorPos(out NativePoint cursor)) return;
        Point position = CalculatePreviewPosition(cursor.X, cursor.Y);
        SetWindowPos(_handle, new nint(-1), (int)position.X, (int)position.Y, 0, 0,
            SwpNoActivate | SwpNoOwnerZOrder | 0x0001 /* SWP_NOSIZE */);
    }

    internal static Size CalculatePreviewSize(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return new Size(180, 120);
        double scale = Math.Min(180d / pixelWidth, 120d / pixelHeight);
        scale = Math.Min(scale, 1d);
        return new Size(Math.Max(1, Math.Round(pixelWidth * scale)), Math.Max(1, Math.Round(pixelHeight * scale)));
    }

    internal static Point CalculatePreviewPosition(int cursorX, int cursorY) =>
        new(cursorX + CursorOffset, cursorY + CursorOffset);

    private void ApplyNativeStyles()
    {
        _handle = new WindowInteropHelper(this).Handle;
        long styles = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(_handle, GwlExStyle, new nint(styles | WsExTransparent | WsExNoActivate | WsExToolWindow));
        long required = WsExTransparent | WsExNoActivate | WsExToolWindow;
        if ((GetWindowLongPtr(_handle, GwlExStyle).ToInt64() & required) != required)
            throw new Win32Exception("Could not make the drag preview click-through.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X; internal int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
