using BetterShot.Controls;
using BetterShot.Interop;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace BetterShot.Capture.Scrolling;

/// <summary>
/// Floating "Stop and keep what's captured" pill shown during a scrolling capture. It asks Windows to exclude it from
/// screen capture (WDA_EXCLUDEFROMCAPTURE); when that is unavailable the session hides it for each frame instead.
/// Esc is registered as a temporary global hotkey on this window so it works while another app has focus.
/// </summary>
internal sealed class ScrollingCaptureControlWindow : Window
{
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const int WmHotKey = 0x0312;
    private const int EscapeHotKeyId = 0x4BF0;
    private const int VkEscape = 0x1B;
    private const int BaseWidth = 430, BaseHeight = 58, BaseBottomInset = 36;
    private readonly TextBlock _progress = new() { FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0xE8, 0xEB)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), MinWidth = 150 };
    private HwndSource? _source;
    private bool _escapeRegistered;

    internal ScrollingCaptureControlWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Width = BaseWidth;
        Height = BaseHeight;
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1C, 0x21));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x4D, 0x55));
        BorderThickness = new Thickness(1);
        FontFamily = new FontFamily("Segoe UI");
        Title = "BetterShot scrolling capture";

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new LucideIcon { Icon = LucideIconKind.ScrollText, Width = 18, Height = 18, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x4A)), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_progress);
        var stopContent = new StackPanel { Orientation = Orientation.Horizontal };
        stopContent.Children.Add(new LucideIcon { Icon = LucideIconKind.CircleStop, Width = 15, Height = 15, Foreground = new SolidColorBrush(Color.FromRgb(0x17, 0x15, 0x0B)), Margin = new Thickness(0, 0, 7, 0) });
        stopContent.Children.Add(new TextBlock { Text = "Stop and keep what's captured", Foreground = new SolidColorBrush(Color.FromRgb(0x17, 0x15, 0x0B)), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var stop = new Button
        {
            Content = stopContent, Height = 34, Padding = new Thickness(12, 4, 12, 4),
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x4A)), BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x4A)),
            ToolTip = "Stop scrolling and keep the captured part (Esc)"
        };
        System.Windows.Automation.AutomationProperties.SetName(stop, "Stop and keep what's captured");
        stop.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        row.Children.Add(stop);
        Content = row;
        SetProgress(0);
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => Cleanup();
    }

    internal event EventHandler? StopRequested;

    /// <summary>True when Windows confirmed this window is invisible to screen capture.</summary>
    internal bool ExcludedFromCapture { get; private set; }

    internal void SetProgress(int capturedHeight) =>
        _progress.Text = capturedHeight <= 0 ? "Scrolling…" : $"Scrolling… {capturedHeight.ToString("N0", CultureInfo.CurrentCulture)} px";

    /// <summary>Bottom-center of the working area on the display containing the region, in physical pixels.</summary>
    internal void PlaceFor(Int32Rect region)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var center = new System.Drawing.Point(region.X + region.Width / 2, region.Y + region.Height / 2);
        System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromPoint(center).WorkingArea;
        NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, area.Left, area.Top, 0, 0, NativeMethods.SwpNoActivate | 0x0001);
        double scale = Math.Max(96u, GetDpiForWindow(handle)) / 96d;
        int width = (int)Math.Round(BaseWidth * scale), height = (int)Math.Round(BaseHeight * scale);
        int x = area.Left + (area.Width - width) / 2;
        int y = area.Bottom - height - (int)Math.Round(BaseBottomInset * scale);
        NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, x, y, width, height, NativeMethods.SwpNoActivate);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        long style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(style | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate));
        ExcludedFromCapture = SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(WndProc);
        _escapeRegistered = RegisterHotKey(handle, EscapeHotKeyId, 0x4000 /* MOD_NOREPEAT */, VkEscape);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == EscapeHotKeyId)
        {
            handled = true;
            StopRequested?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    private void Cleanup()
    {
        if (_source is null) return;
        if (_escapeRegistered) UnregisterHotKey(_source.Handle, EscapeHotKeyId);
        _escapeRegistered = false;
        _source.RemoveHook(WndProc);
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
