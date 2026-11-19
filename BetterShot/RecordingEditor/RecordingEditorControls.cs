using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterShot.RecordingEditor;

/// <summary>
/// A slim slider (rounded track, filled part, white round thumb) for volume and timeline zoom. Value is 0..1.
/// Arrow keys nudge it when focused.
/// </summary>
internal sealed class PillSlider : FrameworkElement
{
    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x4C)));
    private static readonly Brush FillBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE4, 0xE5, 0xE8)));
    private static readonly Pen ThumbPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 1));
    private double _value;

    internal PillSlider(string name)
    {
        Focusable = true;
        Height = 22;
        Cursor = Cursors.Hand;
        AutomationProperties.SetName(this, name);
    }

    internal bool ShowFill { get; init; } = true;

    internal double Value
    {
        get => _value;
        set
        {
            double clamped = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);
            if (Math.Abs(clamped - _value) < 1e-9) return;
            _value = clamped;
            InvalidateVisual();
        }
    }

    /// <summary>Raised when the user moves the slider (not when <see cref="Value"/> is set in code).</summary>
    internal event EventHandler? ValueChanged;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double left = 8, right = Math.Max(left + 1, ActualWidth - 8), middle = ActualHeight / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(left, middle - 2, right - left, 4), 2, 2);
        double x = left + (right - left) * _value;
        if (ShowFill) dc.DrawRoundedRectangle(FillBrush, null, new Rect(left, middle - 2, Math.Max(0, x - left), 4), 2, 2);
        dc.DrawEllipse(Brushes.White, ThumbPen, new Point(x, middle), 7.5, 7.5);
        if (IsKeyboardFocused) dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xF0, 0x7A)), 2), new Point(x, middle), 9.5, 9.5);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        MoveTo(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) MoveTo(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = e.Key switch { Key.Left or Key.Down => -0.05, Key.Right or Key.Up => 0.05, _ => 0 };
        if (step == 0) return;
        Value += step;
        ValueChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private void MoveTo(double x)
    {
        double left = 8, right = Math.Max(left + 1, ActualWidth - 8);
        Value = (x - left) / (right - left);
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>An on/off switch (yellow when on), like the macOS toggle in KhingShot's trim panel.</summary>
internal sealed class ToggleSwitch : FrameworkElement
{
    private static readonly Brush OnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD4, 0x00)));
    private static readonly Brush OffBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x4C)));
    private static readonly Brush KnobOn = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x1D)));
    private bool _isOn;

    internal ToggleSwitch(string name)
    {
        Width = 50;
        Height = 28;
        Focusable = true;
        Cursor = Cursors.Hand;
        AutomationProperties.SetName(this, name);
    }

    internal bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            _isOn = value;
            InvalidateVisual();
        }
    }

    /// <summary>Raised when the user flips the switch.</summary>
    internal event EventHandler? Toggled;

    protected override void OnRender(DrawingContext dc)
    {
        var track = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(_isOn ? OnBrush : OffBrush, IsKeyboardFocused ? new Pen(Brushes.White, 1.5) : null, track, track.Height / 2, track.Height / 2);
        double radius = track.Height / 2 - 3;
        double x = _isOn ? track.Right - 3 - radius : 3 + radius;
        dc.DrawEllipse(_isOn ? KnobOn : Brushes.White, null, new Point(x, track.Height / 2), radius, radius);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsEnabled) return;
        Focus();
        Flip();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Space or Key.Enter)) return;
        Flip();
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private void Flip()
    {
        IsOn = !IsOn;
        Toggled?.Invoke(this, EventArgs.Empty);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
