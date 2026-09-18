using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterShot.Interop;

namespace BetterShot.Capture;

public enum CaptureMode { Area, Window, Display }

/// <summary>
/// Captures the currently composed virtual desktop, then selects from that frozen frame.
/// The first prototype uses GDI BitBlt: output is SDR and protected, occluded, or
/// hardware-overlay content can be absent. Window mode captures visible pixels only.
/// </summary>
public sealed class CaptureService
{
    /// <summary>Renders the real loupe at center and edge positions for deterministic visual diagnostics.</summary>
    public static BitmapSource RenderLoupePreviewForTest()
    {
        const int sourceWidth = 48, sourceHeight = 36;
        var pixels = new byte[sourceWidth * sourceHeight * 4];
        for (int y = 0; y < sourceHeight; y++)
        for (int x = 0; x < sourceWidth; x++)
        {
            int offset = (y * sourceWidth + x) * 4;
            bool tile = ((x / 4) + (y / 4)) % 2 == 0;
            pixels[offset] = (byte)(tile ? 52 : 92);
            pixels[offset + 1] = (byte)(70 + y * 4);
            pixels[offset + 2] = (byte)(80 + x * 3);
            pixels[offset + 3] = 255;
        }
        var source = BitmapSource.Create(sourceWidth, sourceHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, sourceWidth * 4);
        source.Freeze();

        var surface = new Canvas
        {
            Width = 520, Height = 226,
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 23))
        };
        AddPreview(surface, source, 20, "LEFT EDGE", 0, 18);
        AddPreview(surface, source, 190, "CENTER", 25, 18);
        AddPreview(surface, source, 360, "BOTTOM RIGHT", 47, 35);
        surface.Measure(new Size(surface.Width, surface.Height));
        surface.Arrange(new Rect(0, 0, surface.Width, surface.Height));
        surface.UpdateLayout();
        var result = new RenderTargetBitmap((int)surface.Width, (int)surface.Height,
            96, 96, PixelFormats.Pbgra32);
        result.Render(surface);
        result.Freeze();
        return result;
    }

    private static void AddPreview(Canvas surface, BitmapSource source, double x,
        string label, int cursorX, int cursorY)
    {
        var loupe = new LoupeVisual();
        loupe.UpdateSample(source, cursorX, cursorY, 384, 216);
        Canvas.SetLeft(loupe, x); Canvas.SetTop(loupe, 42);
        surface.Children.Add(loupe);
        var caption = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(174, 174, 180)),
            FontFamily = new FontFamily("Segoe UI Semibold"), FontSize = 10
        };
        Canvas.SetLeft(caption, x + 26); Canvas.SetTop(caption, 14);
        surface.Children.Add(caption);
    }

    /// <summary>Renders a synthetic selector state without reading the desktop.</summary>
    public static BitmapSource RenderSelectionPreviewForTest(bool windowHover)
    {
        const int width = 720, height = 420;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(31, 35, 43)), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(47, 53, 65)), null, new Rect(24, 28, 672, 364));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 28, 35)), null, new Rect(24, 28, 672, 34));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 93, 89)), null, new Point(43, 45), 5, 5);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 190, 61)), null, new Point(60, 45), 5, 5);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(54, 201, 91)), null, new Point(77, 45), 5, 5);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(57, 64, 78)), null, new Rect(48, 88, 260, 270));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(67, 75, 91)), null, new Rect(332, 88, 340, 124));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(67, 75, 91)), null, new Rect(332, 234, 340, 124));
        }
        var desktop = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        desktop.Render(visual); desktop.Freeze();
        return SelectionSession.RenderSelectionPreviewForTest(desktop, windowHover);
    }
    /// <summary>Captures a frozen frame without opening selection UI, for diagnostics.</summary>
    public static BitmapSource CaptureDesktopForSmokeTest() => CaptureVirtualDesktop().Image;

    /// <summary>
    /// Opens the real per-monitor selector overlays and automatically cancels them once
    /// WPF has loaded them. Intended for an interactive-session smoke test only.
    /// </summary>
    public static async Task RunOverlaySelfTestAsync()
    {
        if (Application.Current?.Dispatcher is not { } dispatcher)
            throw new InvalidOperationException("Overlay self-test requires a running WPF Application.");
        if (!dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(RunOverlaySelfTestAsync).Task.Unwrap();
            return;
        }

        var (desktop, bounds) = CaptureVirtualDesktop();
        await SelectionSession.RunOverlaySelfTestAsync(desktop, bounds);
    }

    public async Task<BitmapSource?> CaptureAsync(CaptureMode mode)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            return await dispatcher.InvokeAsync(() => CaptureAsync(mode)).Task.Unwrap();

        var (desktop, virtualBounds) = CaptureVirtualDesktop();
        var selected = await SelectionSession.SelectAsync(desktop, virtualBounds, mode);
        if (selected is null || selected.Value.Width <= 0 || selected.Value.Height <= 0)
            return null;

        Int32Rect local = new(selected.Value.X - virtualBounds.X, selected.Value.Y - virtualBounds.Y,
            selected.Value.Width, selected.Value.Height);
        local = ScreenGeometry.Intersect(local, new Int32Rect(0, 0, desktop.PixelWidth, desktop.PixelHeight));
        if (local.IsEmpty) return null;
        var result = new CroppedBitmap(desktop, local);
        result.Freeze();
        return result;
    }

    private static (BitmapSource Image, Int32Rect Bounds) CaptureVirtualDesktop()
    {
        int x = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
        int width = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
        int height = NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Windows reported an empty virtual desktop.");

        IntPtr sourceDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memoryDc = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            if (sourceDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            memoryDc = NativeMethods.CreateCompatibleDC(sourceDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(sourceDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!NativeMethods.BitBlt(memoryDc, 0, 0, width, height, sourceDc, x, y,
                    NativeMethods.Srccopy | NativeMethods.Captureblt))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return (image, new Int32Rect(x, y, width, height));
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1) && memoryDc != IntPtr.Zero)
                NativeMethods.SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) NativeMethods.DeleteDC(memoryDc);
            if (sourceDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, sourceDc);
        }
    }
}
