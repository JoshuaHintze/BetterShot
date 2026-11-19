using BetterShot.Controls;
using BetterShot.Interop;
using BetterShot.Persistence;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace BetterShot.Presentation;

/// <summary>
/// Floating card shown after a recording finishes, like screenshot thumbnails: click to play, drag to share the MP4
/// file, hover for Open / Show in folder / Delete. Recording cards stack in a column just right of the screenshot
/// column at the bottom-left of the display, so the two never overlap. Always on top.
/// </summary>
internal sealed class RecordingThumbnailWindow : Window
{
    private const int BaseWidth = 300, BaseHeight = 200, BaseInset = 18, BaseGap = 10, ScreenshotColumnWidth = 332;
    private static readonly List<RecordingThumbnailWindow> Open = [];
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly Border _actions;
    private readonly string _screen;
    private Point _pointerDown;
    private bool _armed;

    internal event EventHandler? DeleteRequested;

    internal RecordingThumbnailWindow(RecordingEntry entry)
    {
        Entry = entry;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = BaseWidth;
        Height = BaseHeight;
        UseLayoutRounding = true;
        _screen = Forms.Screen.FromPoint(Forms.Cursor.Position).DeviceName;

        var placeholder = new LucideIcon { Icon = LucideIconKind.Video, Width = 40, Height = 40, Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0x72, 0x7A)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var duration = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8),
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x14, 0x15, 0x18)), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 3),
            Child = new TextBlock { Text = RecordingFiles.FormatDuration(entry.DurationSeconds), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White }
        };
        _actions = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 9),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x28, 0x2A, 0x30)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x4E, 0x56)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(3), Opacity = 0, IsHitTestVisible = false,
            Child = ActionRow()
        };
        var close = IconButton(LucideIconKind.X, "Dismiss thumbnail (the recording stays in the library)", (_, _) => Close(), 28, 16);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(6);
        close.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x28, 0x2A, 0x30));

        var grid = new Grid();
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(9), ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0D, 0x10)), Child = new Grid { Children = { placeholder, _image } } });
        grid.Children.Add(duration);
        grid.Children.Add(_actions);
        grid.Children.Add(close);
        var card = new Border
        {
            Margin = new Thickness(8), Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1B, 0x1C, 0x21)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x4D, 0x55)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13),
            Padding = new Thickness(7), Cursor = Cursors.Hand, ToolTip = "Click to play; drag to share the MP4 file",
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Opacity = 0.52 }, Child = grid
        };
        card.PreviewMouseLeftButtonDown += Card_MouseDown;
        card.PreviewMouseMove += Card_MouseMove;
        card.PreviewMouseLeftButtonUp += Card_MouseUp;
        Content = card;
        MouseEnter += (_, _) => ShowActions(true);
        MouseLeave += (_, _) => ShowActions(false);
        SourceInitialized += (_, _) => RecordingWindowStyles.MakeNonActivating(this, clickThrough: false);
        Loaded += async (_, _) => _image.Source = await RecordingFiles.LoadThumbnailAsync(entry.MoviePath);
        Closed += (_, _) => { Open.Remove(this); Reflow(); };
    }

    internal RecordingEntry Entry { get; }

    internal static IReadOnlyList<RecordingThumbnailWindow> Visible => Open;

    internal static RecordingThumbnailWindow Present(RecordingEntry entry)
    {
        var window = new RecordingThumbnailWindow(entry);
        Open.Insert(0, window);
        window.Show();
        Reflow();
        return window;
    }

    internal static void CloseFor(Guid id)
    {
        foreach (RecordingThumbnailWindow window in Open.Where(window => window.Entry.Id == id).ToArray()) window.Close();
    }

    internal static void CloseAll()
    {
        foreach (RecordingThumbnailWindow window in Open.ToArray()) window.Close();
    }

    private StackPanel ActionRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(IconButton(LucideIconKind.Play, "Play recording", (_, _) => TryRun(() => RecordingFiles.Open(Entry.MoviePath))));
        row.Children.Add(IconButton(LucideIconKind.FolderOpen, "Show in folder", (_, _) => TryRun(() => RecordingFiles.ShowInFolder(Entry.MoviePath))));
        row.Children.Add(IconButton(LucideIconKind.Trash2, "Move recording to the Recycle Bin", (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty)));
        return row;
    }

    private static Button IconButton(LucideIconKind kind, string label, RoutedEventHandler click, double size = 38, double iconSize = 18)
    {
        var button = new Button
        {
            Width = size, Height = size == 38 ? 34 : size, Padding = new Thickness(size == 38 ? 8 : 6), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ToolTip = label, Content = new LucideIcon { Icon = kind, Width = iconSize, Height = iconSize, Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xED, 0xE8)) }
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += click;
        return button;
    }

    private void ShowActions(bool show)
    {
        _actions.IsHitTestVisible = show;
        _actions.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(130)));
    }

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        _pointerDown = e.GetPosition(this);
        _armed = true;
        e.Handled = true;
        CaptureMouse();
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_armed || e.LeftButton != MouseButtonState.Pressed) return;
        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _pointerDown.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _pointerDown.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _armed = false;
        ReleaseMouseCapture();
        try { DragDrop.DoDragDrop(this, RecordingFiles.CreateDragData(Entry.MoviePath), DragDropEffects.Copy); }
        catch (Exception) { } // A failing drop target must not take the thumbnail down; the file is unaffected.
    }

    private void Card_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_armed) return;
        _armed = false;
        ReleaseMouseCapture();
        TryRun(() => RecordingFiles.Open(Entry.MoviePath));
    }

    private static bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private static void TryRun(Action action)
    {
        try { action(); }
        catch (Exception error) { MessageBox.Show(error.Message, "BetterShot", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static void Reflow()
    {
        foreach (IGrouping<string, RecordingThumbnailWindow> group in Open.GroupBy(window => window._screen))
        {
            Forms.Screen screen = Forms.Screen.AllScreens.FirstOrDefault(item => item.DeviceName == group.Key) ?? Forms.Screen.PrimaryScreen!;
            int index = 0;
            foreach (RecordingThumbnailWindow window in group)
            {
                IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
                NativeMethods.SetWindowPos(handle, IntPtr.Zero, screen.WorkingArea.Left, screen.WorkingArea.Top, 0, 0, NativeMethods.SwpNoActivate | 0x0004 | 0x0001);
                double scale = RecordingWindowStyles.ScaleOf(window);
                int width = (int)Math.Round(BaseWidth * scale), height = (int)Math.Round(BaseHeight * scale);
                int gap = (int)Math.Round(BaseGap * scale), inset = (int)Math.Round(BaseInset * scale);
                int capacity = Math.Max(1, Math.Min(4, (screen.WorkingArea.Height - inset) / (height + gap)));
                if (index >= capacity) { window.Hide(); index++; continue; }
                if (!window.IsVisible) window.Show();
                int x = screen.WorkingArea.Left + inset + (int)Math.Round((ScreenshotColumnWidth + BaseGap) * scale);
                int y = screen.WorkingArea.Bottom - inset - height - index * (height + gap);
                NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, x, y, width, height, NativeMethods.SwpNoActivate);
                index++;
            }
        }
    }
}
