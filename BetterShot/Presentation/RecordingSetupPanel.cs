using BetterShot.Controls;
using BetterShot.Interop;
using BetterShot.Recording;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace BetterShot.Presentation;

/// <summary>
/// The floating "New recording" window shown after the area is picked, plus the backdrops that keep the area visible
/// (dimmed outside, outlined, "W × H" label below). Everything here is excluded from screen capture and is closed
/// before the countdown and the recording start.
/// </summary>
internal sealed class RecordingSetupPanel : Window
{
    private const double ShadowMargin = 22;
    private const double CardGap = 16;
    private readonly TaskCompletionSource<RecordingSetupChoice> _choice = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<RecordingSetupBackdrop> _backdrops = [];
    private bool _closed;

    internal RecordingSetupPanel(RecordingSetupCard card)
    {
        Card = card;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Opacity = 0;
        Title = "New recording";
        Content = new Border
        {
            Margin = new Thickness(ShadowMargin),
            Child = card,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.55 }
        };
        card.Chosen += (_, choice) => Finish(choice);
        PreviewKeyDown += (_, e) => { if (card.HandleKey(e.Key)) e.Handled = true; };
        SourceInitialized += (_, _) => RecordingWindowStyles.ExcludeFromCapture(this);
        Closed += (_, _) =>
        {
            _closed = true;
            foreach (RecordingSetupBackdrop backdrop in _backdrops) backdrop.Close();
            _backdrops.Clear();
            _choice.TrySetResult(RecordingSetupChoice.Cancel);
        };
    }

    internal RecordingSetupCard Card { get; }
    internal Task<RecordingSetupChoice> Choice => _choice.Task;

    /// <summary>Shows the backdrops and the card for <paramref name="area"/> and waits for Start, Cancel, or Reselect.</summary>
    internal async Task<RecordingSetupChoice> RunAsync(Int32Rect area)
    {
        if (_closed) return await Choice;
        Forms.Screen areaScreen = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(area.X, area.Y, area.Width, area.Height));
        Int32Rect monitor = ToInt32Rect(areaScreen.Bounds), workArea = ToInt32Rect(areaScreen.WorkingArea);
        foreach (Forms.Screen screen in Forms.Screen.AllScreens)
        {
            var backdrop = new RecordingSetupBackdrop(area, monitor);
            backdrop.Clicked += (_, _) => { Activate(); Card.FocusStart(); };
            backdrop.ShowCovering(ToInt32Rect(screen.Bounds));
            _backdrops.Add(backdrop);
        }
        int labelReserve = _backdrops.FirstOrDefault(backdrop => backdrop.ShowsLabel)?.LabelReservePhysical ?? 0;
        ShowPlaced(area, workArea, labelReserve);
        return await Choice;
    }

    /// <summary>
    /// Shows the window invisibly on the area's monitor first (so WPF adopts that monitor's DPI), then measures it in
    /// physical pixels and moves the card to its final spot.
    /// </summary>
    private void ShowPlaced(Int32Rect area, Int32Rect workArea, int labelReserve)
    {
        Show();
        MovePhysical(workArea.X + 8, workArea.Y + 8);
        UpdateLayout();
        double scale = RecordingWindowStyles.ScaleOf(this);
        int margin = (int)Math.Round(ShadowMargin * scale);
        int width = (int)Math.Ceiling(ActualWidth * scale) - margin * 2, height = (int)Math.Ceiling(ActualHeight * scale) - margin * 2;
        Int32Rect card = RecordingSetupLayout.PlaceCard(area, workArea, width, height, (int)Math.Round(CardGap * scale), labelReserve);
        MovePhysical(card.X - margin, card.Y - margin);
        Opacity = 1;
        Activate();
        Card.FocusStart();
    }

    /// <summary>Closes the panel and its backdrops if still open (an unanswered panel counts as Cancel).</summary>
    internal void Dismiss()
    {
        if (!_closed) Close();
    }

    private void Finish(RecordingSetupChoice choice)
    {
        _choice.TrySetResult(choice);
        Dismiss();
    }

    private void MovePhysical(int x, int y)
    {
        IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
        const int swpNoSize = 0x0001;
        NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, x, y, 0, 0, swpNoSize | NativeMethods.SwpNoActivate);
    }

    private static Int32Rect ToInt32Rect(System.Drawing.Rectangle rectangle) => new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    /// <summary>The Area row text for a target: "Area", "Window — Title", or "Display N", with its size in pixels.</summary>
    internal static (string Title, string Detail, LucideIconKind Icon) Describe(RecordingTarget target)
    {
        string dimensions = RecordingSetupLayout.Dimensions(target.ScreenArea.Width, target.ScreenArea.Height);
        return target.Kind switch
        {
            RecordingTargetKind.Window => (WindowTitle(target.Window) is { Length: > 0 } title ? $"Window — {title}" : "Window", dimensions, LucideIconKind.AppWindow),
            RecordingTargetKind.Display => ($"Display {DisplayNumber(target.MonitorBounds)}", dimensions, LucideIconKind.Monitor),
            _ => ("Area", dimensions, LucideIconKind.SquareDashed)
        };
    }

    private static int DisplayNumber(Int32Rect monitorBounds)
    {
        Forms.Screen[] screens = Forms.Screen.AllScreens;
        int index = Array.FindIndex(screens, screen => ToInt32Rect(screen.Bounds) == monitorBounds);
        return index < 0 ? 1 : index + 1;
    }

    private static string WindowTitle(IntPtr window)
    {
        if (window == IntPtr.Zero) return string.Empty;
        int length = NativeMethods.GetWindowTextLength(window);
        if (length <= 0) return string.Empty;
        var text = new StringBuilder(length + 1);
        return GetWindowText(window, text, text.Capacity) > 0 ? text.ToString().Trim() : string.Empty;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
}

/// <summary>
/// Covers one monitor while the setup card is open: dims everything outside the area, outlines the area in the
/// selection yellow, and (on the area's monitor) shows the "W × H" label. Absorbs clicks without taking focus.
/// </summary>
internal sealed class RecordingSetupBackdrop : Window
{
    private const double LabelGap = 8;
    private readonly Int32Rect _area;
    private readonly Int32Rect _areaMonitor;
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private Int32Rect _bounds;

    internal RecordingSetupBackdrop(Int32Rect area, Int32Rect areaMonitor)
    {
        _area = area;
        _areaMonitor = areaMonitor;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "BetterShot recording area";
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            RecordingWindowStyles.MakeNonActivating(this, clickThrough: false);
            RecordingWindowStyles.ExcludeFromCapture(this);
        };
        MouseDown += (_, e) => { e.Handled = true; Clicked?.Invoke(this, EventArgs.Empty); };
    }

    internal event EventHandler? Clicked;
    internal bool ShowsLabel => _bounds == _areaMonitor;
    /// <summary>Physical pixels the label needs below the area (0 when it sits inside the area).</summary>
    internal int LabelReservePhysical { get; private set; }

    internal void ShowCovering(Int32Rect bounds)
    {
        _bounds = bounds;
        Show();
        double scale = RecordingWindowStyles.PlacePhysical(this, bounds);
        Draw(scale);
    }

    private void Draw(double scale)
    {
        _canvas.Children.Clear();
        var monitorDip = new Rect(0, 0, _bounds.Width / scale, _bounds.Height / scale);
        Point topLeft = RecordingGeometry.ScreenToOverlay(_area.X, _area.Y, _bounds, scale);
        var holeDip = new Rect(topLeft, new Size(_area.Width / scale, _area.Height / scale));
        _canvas.Children.Add(new Path { Data = RecordingGeometry.BuildDimGeometry(monitorDip, holeDip), Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)) });
        var outline = new Rectangle { Width = Math.Max(0, holeDip.Width - 2), Height = Math.Max(0, holeDip.Height - 2), Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x4A)), StrokeThickness = 2 };
        Canvas.SetLeft(outline, holeDip.X + 1);
        Canvas.SetTop(outline, holeDip.Y + 1);
        _canvas.Children.Add(outline);
        if (ShowsLabel) DrawLabel(scale);
    }

    private void DrawLabel(double scale)
    {
        Border label = CreateDimensionLabel(RecordingSetupLayout.Dimensions(_area.Width, _area.Height));
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int width = (int)Math.Ceiling(label.DesiredSize.Width * scale), height = (int)Math.Ceiling(label.DesiredSize.Height * scale);
        int gap = (int)Math.Round(LabelGap * scale);
        Int32Rect placed = RecordingSetupLayout.PlaceDimensionLabel(_area, _areaMonitor, width, height, gap);
        LabelReservePhysical = RecordingSetupLayout.LabelReserve(_area, _areaMonitor, height, gap);
        Point position = RecordingGeometry.ScreenToOverlay(placed.X, placed.Y, _bounds, scale);
        Canvas.SetLeft(label, position.X);
        Canvas.SetTop(label, position.Y);
        _canvas.Children.Add(label);
    }

    /// <summary>The dark "2960 × 1840" pill (also rendered by diagnostics).</summary>
    internal static Border CreateDimensionLabel(string text) => new()
    {
        Padding = new Thickness(12, 4, 12, 5),
        CornerRadius = new CornerRadius(9),
        Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x1B, 0x1B, 0x1E)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        }
    };
}
