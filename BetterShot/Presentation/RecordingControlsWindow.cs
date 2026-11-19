using BetterShot.Controls;
using BetterShot.Recording;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BetterShot.Presentation;

/// <summary>
/// Floating pill shown while recording, centered at the top of the recorded display (KhingShot parity): red dot,
/// elapsed time with tenths, microphone level bars, divider, pause/resume, microphone mute, camera toggle, and a round
/// stop button. Excluded from capture, never takes focus, and can be dragged out of the way.
/// </summary>
internal sealed class RecordingControlsWindow : Window
{
    private const double ShadowMargin = 12;
    private const int LevelBarCount = 5;
    private static readonly Brush TextColor = Frozen(Color.FromRgb(0xF4, 0xF4, 0xF5));
    private static readonly Brush RecordingRed = Frozen(Color.FromRgb(0xF2, 0x66, 0x5F));
    private static readonly Brush StopCoral = Frozen(Color.FromRgb(0xEC, 0x71, 0x6A));
    private static readonly Brush PausedAmber = Frozen(Color.FromRgb(0xFF, 0xD6, 0x0A));
    private static readonly Brush LevelOn = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly Brush LevelOff = Frozen(Color.FromRgb(0x4A, 0x4A, 0x50));
    private static readonly Brush RoundButtonGray = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3F));
    private readonly Func<TimeSpan> _elapsed;
    private readonly Func<float>? _microphonePeak;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Ellipse _indicator = new() { Width = 10, Height = 10, Fill = RecordingRed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 0) };
    private readonly TextBlock _elapsedText = new()
    {
        FontSize = 24, Foreground = TextColor, VerticalAlignment = VerticalAlignment.Center, MinWidth = 88,
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.Normal
    };
    private readonly Rectangle[] _levelBars = new Rectangle[LevelBarCount];
    private readonly Button _pauseButton;
    private readonly LucideIcon _pauseIcon = CreateIcon(LucideIconKind.Pause, 18);
    private readonly Button _micButton;
    private readonly LucideIcon _micIcon = CreateIcon(LucideIconKind.Mic, 18);
    private readonly Button _cameraButton;
    private readonly LucideIcon _cameraIcon = CreateIcon(LucideIconKind.Video, 19);
    private readonly bool _hasMicrophone, _hasCamera;
    private bool _paused, _muted, _cameraHidden;

    internal event EventHandler? StopRequested;
    internal event EventHandler? PauseToggled;
    internal event EventHandler? MicrophoneToggled;
    internal event EventHandler? CameraToggled;

    /// <param name="microphonePeak">Live microphone peak 0..1 for the level bars, or null when no meter is available.</param>
    internal RecordingControlsWindow(Func<TimeSpan> elapsed, bool showMicrophone, bool showCamera, Func<float>? microphonePeak = null)
    {
        _elapsed = elapsed;
        _microphonePeak = microphonePeak;
        _hasMicrophone = showMicrophone;
        _hasCamera = showCamera;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        Title = "BetterShot recording";
        System.Windows.Documents.Typography.SetNumeralAlignment(_elapsedText, FontNumeralAlignment.Tabular);

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_indicator);
        row.Children.Add(_elapsedText);
        row.Children.Add(CreateLevelMeter());
        row.Children.Add(new Rectangle { Width = 1, Height = 24, Fill = Frozen(Color.FromRgb(0x3F, 0x3F, 0x46)), Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });

        _pauseButton = RoundButton(_pauseIcon, "Pause recording", Brushes.Transparent, 38, (_, _) => PauseToggled?.Invoke(this, EventArgs.Empty));
        row.Children.Add(_pauseButton);
        _micButton = RoundButton(_micIcon, showMicrophone ? "Mute the microphone" : "Microphone is off for this recording", RoundButtonGray, 40, (_, _) => MicrophoneToggled?.Invoke(this, EventArgs.Empty));
        row.Children.Add(_micButton);
        _cameraButton = RoundButton(_cameraIcon, showCamera ? "Turn the camera off" : "Camera is off for this recording", Brushes.Transparent, 38, (_, _) => CameraToggled?.Invoke(this, EventArgs.Empty));
        row.Children.Add(_cameraButton);
        if (!showMicrophone) { _micIcon.Icon = LucideIconKind.MicOff; _micButton.IsEnabled = false; }
        if (!showCamera) { _cameraIcon.Icon = LucideIconKind.VideoOff; _cameraButton.IsEnabled = false; }

        var stopSquare = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = Brushes.White };
        Button stopButton = RoundButton(stopSquare, "Stop recording", StopCoral, 42, (_, _) => StopRequested?.Invoke(this, EventArgs.Empty));
        stopButton.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(stopButton);

        var pill = new Border
        {
            Margin = new Thickness(ShadowMargin),
            Padding = new Thickness(12, 6, 6, 6),
            CornerRadius = new CornerRadius(30),
            Background = Frozen(Color.FromArgb(0xF4, 0x1B, 0x1B, 0x1E)),
            BorderBrush = Frozen(Color.FromRgb(0x3A, 0x3A, 0x40)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.5 },
            Child = row,
            Cursor = Cursors.SizeAll,
            ToolTip = "Drag to move. Recording controls are not included in the video."
        };
        pill.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not DependencyObject source || FindButton(source) is null) DragMove(); };
        Content = pill;
        Tick();
        _timer.Tick += (_, _) => Tick();
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
        SetLabel(_pauseButton, paused ? "Resume recording" : "Pause recording");
        _indicator.Fill = paused ? PausedAmber : RecordingRed;
        Tick();
    }

    internal void SetMicrophoneMuted(bool muted)
    {
        _muted = muted;
        _micIcon.Icon = muted ? LucideIconKind.MicOff : LucideIconKind.Mic;
        _micIcon.Foreground = muted ? RecordingRed : TextColor;
        SetLabel(_micButton, muted ? "Unmute the microphone" : "Mute the microphone");
        Tick();
    }

    internal void SetCameraHidden(bool hidden)
    {
        _cameraHidden = hidden;
        _cameraIcon.Icon = hidden ? LucideIconKind.VideoOff : LucideIconKind.Video;
        _cameraIcon.Foreground = hidden ? RecordingRed : TextColor;
        SetLabel(_cameraButton, hidden ? "Turn the camera on" : "Turn the camera off");
    }

    internal bool IsMicrophoneMuted => _muted;
    internal bool IsCameraHidden => _cameraHidden;
    internal bool HasMicrophone => _hasMicrophone;
    internal bool HasCamera => _hasCamera;

    /// <summary>Centered at the top of the recorded display's work area.</summary>
    internal void PlaceAtTop(Int32Rect workArea)
    {
        Int32Rect placed = ComputeTopPlacement(workArea, RecordingWindowStyles.ScaleOf(this));
        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        Interop.NativeMethods.SetWindowPos(handle, Interop.NativeMethods.HwndTopmost, placed.X, placed.Y, 0, 0, 0x0001 | Interop.NativeMethods.SwpNoActivate);
        // A move onto a monitor with another DPI rescales the window, so place once more with the new size.
        double scale = RecordingWindowStyles.ScaleOf(this);
        UpdateLayout();
        Int32Rect settled = ComputeTopPlacement(workArea, scale);
        if (settled.X != placed.X || settled.Y != placed.Y)
            Interop.NativeMethods.SetWindowPos(handle, Interop.NativeMethods.HwndTopmost, settled.X, settled.Y, 0, 0, 0x0001 | Interop.NativeMethods.SwpNoActivate);
    }

    private Int32Rect ComputeTopPlacement(Int32Rect workArea, double scale)
    {
        UpdateLayout();
        int width = (int)Math.Ceiling(ActualWidth * scale), height = (int)Math.Ceiling(ActualHeight * scale);
        return TopPlacement(workArea, width, height, (int)Math.Round(ShadowMargin * scale), (int)Math.Round(10 * scale));
    }

    /// <summary>Pure placement: horizontally centered, <paramref name="gap"/> below the work-area top, shadow margin included.</summary>
    internal static Int32Rect TopPlacement(Int32Rect workArea, int windowWidth, int windowHeight, int shadowMargin, int gap)
    {
        int x = workArea.X + (workArea.Width - windowWidth) / 2;
        int y = workArea.Y + gap - shadowMargin;
        x = Math.Clamp(x, workArea.X - shadowMargin, Math.Max(workArea.X - shadowMargin, workArea.X + workArea.Width - windowWidth + shadowMargin));
        return new Int32Rect(x, y, windowWidth, windowHeight);
    }

    /// <summary>mm:ss.t, or h:mm:ss.t past an hour ("00:00.8").</summary>
    internal static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        long tenths = (long)Math.Floor(elapsed.TotalMilliseconds / 100);
        long seconds = tenths / 10, minutes = seconds / 60, hours = minutes / 60;
        return hours > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3}", hours, minutes % 60, seconds % 60, tenths % 10)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}.{2}", minutes, seconds % 60, tenths % 10);
    }

    private void Tick()
    {
        _elapsedText.Text = FormatElapsed(_elapsed());
        _elapsedText.Opacity = _paused && DateTime.Now.Millisecond < 500 ? 0.45 : 1;
        bool live = _hasMicrophone && !_muted && !_paused && _microphonePeak is not null;
        int lit = live ? MicrophoneLevelMeter.LitBars(_microphonePeak!(), LevelBarCount) : 0;
        for (int index = 0; index < _levelBars.Length; index++) _levelBars[index].Fill = index < lit ? LevelOn : LevelOff;
    }

    private StackPanel CreateLevelMeter()
    {
        var meter = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), ToolTip = "Microphone level" };
        meter.Children.Add(CreateIcon(LucideIconKind.Mic, 15));
        double[] heights = [6, 10, 14, 10, 6];
        for (int index = 0; index < LevelBarCount; index++)
        {
            _levelBars[index] = new Rectangle { Width = 3, Height = heights[index], RadiusX = 1.5, RadiusY = 1.5, Fill = LevelOff, Margin = new Thickness(index == 0 ? 6 : 2.5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            meter.Children.Add(_levelBars[index]);
        }
        if (!_hasMicrophone) meter.Opacity = 0.5;
        return meter;
    }

    private static LucideIcon CreateIcon(LucideIconKind kind, double size) => new() { Icon = kind, Width = size, Height = size, Foreground = TextColor };

    private static Button RoundButton(UIElement content, string label, Brush background, double size, RoutedEventHandler click)
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2));
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85, "Chrome"));
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, PausedAmber, "Chrome"));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "Chrome"));
        template.Triggers.Add(hover);
        template.Triggers.Add(focus);
        template.Triggers.Add(disabled);

        var button = new Button
        {
            Content = content, Width = size, Height = size, Margin = new Thickness(4, 0, 0, 0), Background = background,
            Template = template, Cursor = Cursors.Hand, FocusVisualStyle = null, VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetShowOnDisabled(button, true);
        SetLabel(button, label);
        button.Click += click;
        return button;
    }

    private static void SetLabel(Button button, string label)
    {
        button.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(button, label);
    }

    private static Button? FindButton(DependencyObject? element)
    {
        while (element is not null and not ButtonBase) element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as Button;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
