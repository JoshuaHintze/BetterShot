using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace BetterShot.Editing;

/// <summary>
/// The inspector eyedropper: freezes the desktop, covers every display with a nearly invisible click catcher, and returns
/// the color of the pixel the user clicks (Esc or right-click cancels). The frozen copy means the overlay never tints the result.
/// </summary>
internal static class ScreenColorPicker
{
    internal static Task<Color?> PickAsync()
    {
        var completion = new TaskCompletionSource<Color?>();
        Drawing.Rectangle desktop = Forms.SystemInformation.VirtualScreen;
        Drawing.Bitmap snapshot;
        try
        {
            snapshot = new Drawing.Bitmap(desktop.Width, desktop.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
            using Drawing.Graphics graphics = Drawing.Graphics.FromImage(snapshot);
            graphics.CopyFromScreen(desktop.Location, Drawing.Point.Empty, desktop.Size);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult<Color?>(null);
        }

        var windows = new List<PickerWindow>();
        void Finish(Color? color)
        {
            if (completion.Task.IsCompleted) return;
            completion.TrySetResult(color);
            foreach (PickerWindow window in windows) window.Close();
            snapshot.Dispose();
        }

        Color? Sample()
        {
            Drawing.Point cursor = Forms.Cursor.Position;
            int x = cursor.X - desktop.Left, y = cursor.Y - desktop.Top;
            if (completion.Task.IsCompleted || x < 0 || y < 0 || x >= snapshot.Width || y >= snapshot.Height) return null;
            Drawing.Color pixel = snapshot.GetPixel(x, y);
            return Color.FromRgb(pixel.R, pixel.G, pixel.B);
        }

        foreach (Forms.Screen screen in Forms.Screen.AllScreens)
        {
            var window = new PickerWindow(screen.Bounds, Sample, Finish);
            windows.Add(window);
            window.Show();
        }
        windows.FirstOrDefault(window => window.ContainsCursor())?.Activate();
        return completion.Task;
    }

    private sealed class PickerWindow : Window
    {
        private readonly Drawing.Rectangle _bounds;
        private readonly Func<Color?> _sample;
        private readonly Action<Color?> _finish;
        private readonly Canvas _canvas = new();
        private readonly Border _loupe;
        private readonly Border _swatch = new() { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderBrush = Brushes.White, BorderThickness = new Thickness(2) };
        private readonly TextBlock _hex = new() { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) };

        internal PickerWindow(Drawing.Rectangle bounds, Func<Color?> sample, Action<Color?> finish)
        {
            _bounds = bounds; _sample = sample; _finish = finish;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; ShowInTaskbar = false; Topmost = true;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); Cursor = Cursors.Cross; ShowActivated = true;
            Left = bounds.Left; Top = bounds.Top; Width = Math.Max(1, bounds.Width); Height = Math.Max(1, bounds.Height);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_swatch); row.Children.Add(_hex);
            _loupe = new Border
            {
                Child = row, Background = new SolidColorBrush(Color.FromArgb(235, 28, 29, 32)), CornerRadius = new CornerRadius(17), Padding = new Thickness(5, 4, 12, 4),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 72, 76)), BorderThickness = new Thickness(1), IsHitTestVisible = false, Visibility = Visibility.Collapsed
            };
            _canvas.Children.Add(_loupe);
            Content = _canvas;
            SourceInitialized += (_, _) => SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, 0x0010 | 0x0004);
            MouseMove += (_, e) => UpdateLoupe(e.GetPosition(this));
            MouseLeftButtonDown += (_, e) => { e.Handled = true; _finish(_sample()); };
            MouseRightButtonDown += (_, e) => { e.Handled = true; _finish(null); };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; _finish(null); } };
        }

        internal bool ContainsCursor() => _bounds.Contains(Forms.Cursor.Position);

        private void UpdateLoupe(Point position)
        {
            if (_sample() is not Color color) { _loupe.Visibility = Visibility.Collapsed; return; }
            _swatch.Background = new SolidColorBrush(color);
            _hex.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            _loupe.Visibility = Visibility.Visible;
            double left = position.X + 18, top = position.Y + 18;
            if (left + 130 > ActualWidth) left = position.X - 140;
            if (top + 40 > ActualHeight) top = position.Y - 50;
            Canvas.SetLeft(_loupe, left);
            Canvas.SetTop(_loupe, top);
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
