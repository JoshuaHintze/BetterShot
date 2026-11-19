using BetterShot.Controls;
using BetterShot.Interop;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Forms = System.Windows.Forms;

namespace BetterShot.Presentation;

/// <summary>
/// Small always-on-top pill shown during a capture delay. It never takes focus and ignores the
/// mouse, so menus opened in other apps stay open until the frame is captured.
/// </summary>
internal sealed class CaptureCountdownWindow : Window
{
    private const int BaseWidth = 176, BaseHeight = 52, BaseBottomInset = 40;
    private const int WsExTransparent = 0x00000020;
    private readonly TextBlock _secondsText = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x4A)), VerticalAlignment = VerticalAlignment.Center };

    internal CaptureCountdownWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Width = BaseWidth;
        Height = BaseHeight;
        FontFamily = new FontFamily("Segoe UI");

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new LucideIcon { Icon = LucideIconKind.Timer, Width = 18, Height = 18, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x4A)), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = "Capturing in", FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0xE8, 0xEB)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(_secondsText);
        Content = new Border
        {
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1B, 0x1C, 0x21)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x4D, 0x55)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.5 },
            Child = row
        };
        SourceInitialized += (_, _) => MakeClickThroughAndNonActivating();
    }

    internal void SetSeconds(int seconds) => _secondsText.Text = seconds.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Shows the pill, counts down once per second, and closes it. Returns false if canceled.</summary>
    internal static async Task<bool> RunAsync(int seconds, CancellationToken cancellation)
    {
        var window = new CaptureCountdownWindow();
        try
        {
            window.SetSeconds(seconds);
            window.Show();
            window.PlaceOnDisplayUnderCursor();
            for (int remaining = seconds; remaining > 0; remaining--)
            {
                window.SetSeconds(remaining);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
            }
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally { window.Close(); }
    }

    private void MakeClickThroughAndNonActivating()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        long style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        style |= NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow | WsExTransparent;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(style));
    }

    /// <summary>Bottom-center of the working area on the display under the cursor, in physical pixels.</summary>
    private void PlaceOnDisplayUnderCursor()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        System.Drawing.Rectangle area = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, area.Left, area.Top, 0, 0, NativeMethods.SwpNoActivate | 0x0001);
        double scale = Math.Max(96u, GetDpiForWindow(handle)) / 96d;
        int width = (int)Math.Round(BaseWidth * scale), height = (int)Math.Round(BaseHeight * scale);
        int x = area.Left + (area.Width - width) / 2;
        int y = area.Bottom - height - (int)Math.Round(BaseBottomInset * scale);
        NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, x, y, width, height, NativeMethods.SwpNoActivate);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
