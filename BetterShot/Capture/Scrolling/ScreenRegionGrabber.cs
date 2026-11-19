using BetterShot.Interop;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BetterShot.Capture.Scrolling;

/// <summary>Copies one physical-pixel screen rectangle into a top-down BGRA buffer using the same GDI path as normal captures.</summary>
internal static class ScreenRegionGrabber
{
    private const uint DibRgbColors = 0;

    internal static byte[] Grab(Int32Rect region)
    {
        if (region.Width <= 0 || region.Height <= 0) throw new ArgumentOutOfRangeException(nameof(region));
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memoryDc = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            if (screenDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, region.Width, region.Height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!NativeMethods.BitBlt(memoryDc, 0, 0, region.Width, region.Height, screenDc, region.X, region.Y,
                    NativeMethods.Srccopy | NativeMethods.Captureblt))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            NativeMethods.SelectObject(memoryDc, previous);
            previous = IntPtr.Zero;

            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = region.Width,
                Height = -region.Height, // Negative height requests top-down rows.
                Planes = 1,
                BitCount = 32,
                Compression = 0
            };
            var pixels = new byte[region.Width * region.Height * 4];
            int lines = GetDIBits(memoryDc, bitmap, 0, (uint)region.Height, pixels, ref header, DibRgbColors);
            if (lines != region.Height) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the captured pixels.");
            for (int alpha = 3; alpha < pixels.Length; alpha += 4) pixels[alpha] = 255;
            return pixels;
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1) && memoryDc != IntPtr.Zero) NativeMethods.SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) NativeMethods.DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
}
