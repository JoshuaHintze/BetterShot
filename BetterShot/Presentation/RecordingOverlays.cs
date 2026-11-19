using BetterShot.Recording;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace BetterShot.Presentation;

/// <summary>Transparent, borderless, click-through, topmost window used by both recording overlays.</summary>
internal abstract class ClickThroughOverlayWindow : Window
{
    private readonly bool _excludeFromCapture;

    protected ClickThroughOverlayWindow(bool excludeFromCapture)
    {
        _excludeFromCapture = excludeFromCapture;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        SourceInitialized += (_, _) =>
        {
            RecordingWindowStyles.MakeNonActivating(this, clickThrough: true);
            if (_excludeFromCapture) RecordingWindowStyles.ExcludeFromCapture(this);
        };
    }

    /// <summary>Physical-pixel bounds the window covers, and the resulting DPI scale.</summary>
    protected Int32Rect Bounds { get; private set; }
    protected double Scale { get; private set; } = 1;

    internal void ShowCovering(Int32Rect bounds)
    {
        Bounds = bounds;
        Show();
        Scale = RecordingWindowStyles.PlacePhysical(this, bounds);
        OnPlaced();
    }

    protected abstract void OnPlaced();
}

/// <summary>Dims one monitor outside the recorded region and outlines the region. Never appears in the recording.</summary>
internal sealed class RecordingDimOverlay : ClickThroughOverlayWindow
{
    private readonly Int32Rect _recordedArea;
    private readonly Canvas _canvas = new();

    internal RecordingDimOverlay(Int32Rect recordedArea) : base(excludeFromCapture: true)
    {
        _recordedArea = recordedArea;
        Content = _canvas;
    }

    protected override void OnPlaced()
    {
        _canvas.Children.Clear();
        var monitorDip = new Rect(0, 0, Bounds.Width / Scale, Bounds.Height / Scale);
        Point topLeft = RecordingGeometry.ScreenToOverlay(_recordedArea.X, _recordedArea.Y, Bounds, Scale);
        var holeDip = new Rect(topLeft, new Size(_recordedArea.Width / Scale, _recordedArea.Height / Scale));
        _canvas.Children.Add(new Path { Data = RecordingGeometry.BuildDimGeometry(monitorDip, holeDip), Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)) });
        var outline = new Rectangle
        {
            Width = holeDip.Width + 4, Height = holeDip.Height + 4,
            Stroke = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0x4D, 0x4D)), StrokeThickness = 2, StrokeDashArray = [4, 3]
        };
        Canvas.SetLeft(outline, holeDip.X - 2);
        Canvas.SetTop(outline, holeDip.Y - 2);
        _canvas.Children.Add(outline);
    }
}

/// <summary>
/// Draws click ripples and shortcut keystroke pills over the recorded area. Unlike the dim overlay it IS captured, so
/// the highlights are part of the movie (window recordings capture only the window, so they rely on the event log).
/// </summary>
internal sealed class RecordingInputOverlay : ClickThroughOverlayWindow
{
    private static readonly Brush RingBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x4A));
    private readonly Canvas _canvas = new();
    private Border? _keystroke;

    internal RecordingInputOverlay() : base(excludeFromCapture: false) => Content = _canvas;

    protected override void OnPlaced() { }

    internal void ShowClick(int screenX, int screenY)
    {
        if (screenX < Bounds.X || screenY < Bounds.Y || screenX >= Bounds.X + Bounds.Width || screenY >= Bounds.Y + Bounds.Height) return;
        Point center = RecordingGeometry.ScreenToOverlay(screenX, screenY, Bounds, Scale);
        const double size = 44;
        var ring = new Ellipse
        {
            Width = size, Height = size, Stroke = RingBrush, StrokeThickness = 3,
            Fill = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xD8, 0x4A)),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.35, 0.35)
        };
        Canvas.SetLeft(ring, center.X - size / 2);
        Canvas.SetTop(ring, center.Y - size / 2);
        _canvas.Children.Add(ring);
        var duration = new Duration(TimeSpan.FromMilliseconds(520));
        var grow = new DoubleAnimation(0.35, 1, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(1, 0, duration) { BeginTime = TimeSpan.FromMilliseconds(120) };
        fade.Completed += (_, _) => _canvas.Children.Remove(ring);
        ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(OpacityProperty, fade);
    }

    internal void ShowKeystroke(string text)
    {
        if (_keystroke is not null) _canvas.Children.Remove(_keystroke);
        var label = new TextBlock { Text = text, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI") };
        var pill = new Border
        {
            Child = label, Padding = new Thickness(16, 8, 16, 9), CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1B, 0x1C, 0x21)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1)
        };
        pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = Bounds.Width / Scale, height = Bounds.Height / Scale;
        Canvas.SetLeft(pill, Math.Max(0, (width - pill.DesiredSize.Width) / 2));
        Canvas.SetTop(pill, Math.Max(0, height - pill.DesiredSize.Height - Math.Min(48, height * 0.08)));
        _canvas.Children.Add(pill);
        _keystroke = pill;
        var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(350))) { BeginTime = TimeSpan.FromMilliseconds(1100) };
        fade.Completed += (_, _) => { _canvas.Children.Remove(pill); if (ReferenceEquals(_keystroke, pill)) _keystroke = null; };
        pill.BeginAnimation(OpacityProperty, fade);
    }
}
