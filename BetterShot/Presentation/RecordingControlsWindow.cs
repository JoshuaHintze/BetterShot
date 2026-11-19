using BetterShot.Controls;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace BetterShot.Presentation;

/// <summary>
/// Small floating pill shown while recording: elapsed time, pause/resume, microphone mute, camera toggle, and stop.
/// Excluded from capture, never takes focus, and can be dragged out of the way.
/// </summary>
internal sealed class RecordingControlsWindow : Window
{
    private const double BaseHeight = 52;
    private static readonly Brush TextColor = new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF3));
    private static readonly Brush RecordingRed = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D));
    private static readonly Brush PausedAmber = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x4A));
    private readonly Func<TimeSpan> _elapsed;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly System.Windows.Shapes.Ellipse _indicator = new() { Width = 10, Height = 10, Fill = RecordingRed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 9, 0) };
    private readonly TextBlock _elapsedText = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = TextColor, VerticalAlignment = VerticalAlignment.Center, MinWidth = 52, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
    private readonly Button _pauseButton;
    private readonly LucideIcon _pauseIcon = CreateIcon(LucideIconKind.Pause);
    private readonly Button? _micButton;
    private readonly LucideIcon _micIcon = CreateIcon(LucideIconKind.Mic);
    private readonly Button? _cameraButton;
    private readonly LucideIcon _cameraIcon = CreateIcon(LucideIconKind.Camera);
    private bool _paused, _muted, _cameraHidden;

    internal event EventHandler? StopRequested;
    internal event EventHandler? PauseToggled;
    internal event EventHandler? MicrophoneToggled;
    internal event EventHandler? CameraToggled;

    internal RecordingControlsWindow(Func<TimeSpan> elapsed, bool showMicrophone, bool showCamera)
    {
        _elapsed = elapsed;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI");
        Title = "BetterShot recording";

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_indicator);
        row.Children.Add(_elapsedText);
        _pauseButton = ControlButton(_pauseIcon, "Pause recording", (_, _) => PauseToggled?.Invoke(this, EventArgs.Empty));
        row.Children.Add(_pauseButton);
        if (showMicrophone)
        {
            _micButton = ControlButton(_micIcon, "Mute microphone", (_, _) => MicrophoneToggled?.Invoke(this, EventArgs.Empty));
            row.Children.Add(_micButton);
        }
        if (showCamera)
        {
            _cameraButton = ControlButton(_cameraIcon, "Hide camera in the edited video", (_, _) => CameraToggled?.Invoke(this, EventArgs.Empty));
            row.Children.Add(_cameraButton);
        }
        var stopSquare = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2.5), Background = Brushes.White };
        var stopButton = new Button
        {
            Content = stopSquare, Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0),
            Background = RecordingRed, BorderThickness = new Thickness(0), ToolTip = "Stop recording", Focusable = false
        };
        System.Windows.Automation.AutomationProperties.SetName(stopButton, "Stop recording");
        stopButton.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        row.Children.Add(stopButton);

        var pill = new Border
        {
            Margin = new Thickness(8),
            MinHeight = BaseHeight - 16,
            Padding = new Thickness(12, 5, 6, 5),
            CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1B, 0x1C, 0x21)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x4D, 0x55)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.5 },
            Child = row,
            Cursor = Cursors.SizeAll,
            ToolTip = "Drag to move. Recording controls are not included in the video."
        };
        pill.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not DependencyObject source || FindButton(source) is null) DragMove(); };
        Content = pill;
        UpdateElapsed();
        _timer.Tick += (_, _) => UpdateElapsed();
        SourceInitialized += (_, _) =>
        {
            RecordingWindowStyles.MakeNonActivating(this, clickThrough: false);
            RecordingWindowStyles.ExcludeFromCapture(this);
        };
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    internal void SetPaused(bool paused)
    {
        _paused = paused;
        _pauseIcon.Icon = paused ? LucideIconKind.Play : LucideIconKind.Pause;
        _pauseButton.ToolTip = paused ? "Resume recording" : "Pause recording";
        System.Windows.Automation.AutomationProperties.SetName(_pauseButton, (string)_pauseButton.ToolTip);
        _indicator.Fill = paused ? PausedAmber : RecordingRed;
        UpdateElapsed();
    }

    internal void SetMicrophoneMuted(bool muted)
    {
        _muted = muted;
        _micIcon.Icon = muted ? LucideIconKind.MicOff : LucideIconKind.Mic;
        _micIcon.Foreground = muted ? RecordingRed : TextColor;
        if (_micButton is not null) _micButton.ToolTip = muted ? "Unmute microphone" : "Mute microphone";
    }

    internal void SetCameraHidden(bool hidden)
    {
        _cameraHidden = hidden;
        _cameraIcon.Icon = hidden ? LucideIconKind.CameraOff : LucideIconKind.Camera;
        _cameraIcon.Foreground = hidden ? RecordingRed : TextColor;
        if (_cameraButton is not null) _cameraButton.ToolTip = hidden ? "Show camera in the edited video" : "Hide camera in the edited video";
    }

    internal bool IsMicrophoneMuted => _muted;
    internal bool IsCameraHidden => _cameraHidden;

    /// <summary>Below the recorded area when there is room on its monitor, else above it, else inside the bottom edge.</summary>
    internal void PlaceNear(Int32Rect recordedArea, Int32Rect workArea)
    {
        double scale = RecordingWindowStyles.ScaleOf(this);
        UpdateLayout();
        int width = (int)Math.Ceiling(Math.Max(ActualWidth, 260) * scale), height = (int)Math.Ceiling(Math.Max(ActualHeight, BaseHeight) * scale);
        int gap = (int)Math.Round(10 * scale);
        int x = Math.Clamp(recordedArea.X + (recordedArea.Width - width) / 2, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - width));
        int below = recordedArea.Y + recordedArea.Height + gap, above = recordedArea.Y - gap - height;
        int y = below + height <= workArea.Y + workArea.Height ? below
            : above >= workArea.Y ? above
            : workArea.Y + workArea.Height - height - gap;
        RecordingWindowStyles.PlacePhysical(this, new Int32Rect(x, y, width, height));
    }

    private void UpdateElapsed()
    {
        TimeSpan elapsed = _elapsed();
        _elapsedText.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        _elapsedText.Opacity = _paused && DateTime.Now.Millisecond < 500 ? 0.45 : 1;
    }

    private static LucideIcon CreateIcon(LucideIconKind kind) => new() { Icon = kind, Width = 17, Height = 17, Foreground = TextColor };

    private static Button ControlButton(LucideIcon icon, string label, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = icon, Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x32)), BorderThickness = new Thickness(0),
            ToolTip = label, Focusable = false
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += click;
        return button;
    }

    private static Button? FindButton(DependencyObject? element)
    {
        while (element is not null and not Button) element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as Button;
    }
}
