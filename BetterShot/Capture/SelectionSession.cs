using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterShot.Interop;

namespace BetterShot.Capture;

internal sealed class SelectionSession
{
    private readonly BitmapSource _desktop;
    private readonly Int32Rect _virtualBounds;
    private readonly CaptureMode _mode;
    private readonly List<SelectionOverlay> _overlays = [];
    private readonly TaskCompletionSource<Int32Rect?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NativeMethods.Point _start;
    private bool _pressed;
    private bool _dragging;
    private bool _finishing;

    private SelectionSession(BitmapSource desktop, Int32Rect virtualBounds, CaptureMode mode)
    {
        _desktop = desktop;
        _virtualBounds = virtualBounds;
        _mode = mode;
    }

    internal static Task<Int32Rect?> SelectAsync(BitmapSource desktop, Int32Rect virtualBounds, CaptureMode mode)
    {
        var session = new SelectionSession(desktop, virtualBounds, mode);
        session.Show();
        return session._completion.Task;
    }

    internal static async Task RunOverlaySelfTestAsync(BitmapSource desktop, Int32Rect virtualBounds)
    {
        foreach (CaptureMode mode in Enum.GetValues<CaptureMode>())
        {
            var session = new SelectionSession(desktop, virtualBounds, mode);
            session.Show();
            _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => session.ValidateAndFinishForSelfTest(closeOverlay: mode == CaptureMode.Window)));
            Int32Rect? result = await session._completion.Task;
            if (result is not null)
                throw new InvalidOperationException($"{mode} overlay self-test completed with a selection instead of cancellation.");
        }
    }

    private void ValidateAndFinishForSelfTest(bool closeOverlay)
    {
        try
        {
            foreach (var overlay in _overlays) overlay.ValidatePlacement();
            if (closeOverlay)
                _overlays[0].Close(); // Exercises the system-close/Closed cancellation route.
            else
                Cancel();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private void Show()
    {
        try
        {
            foreach (var rect in NativeMethods.GetMonitorRects())
            {
                var bounds = new Int32Rect(rect.Left, rect.Top, rect.Width, rect.Height);
                var local = ScreenGeometry.Intersect(new Int32Rect(bounds.X - _virtualBounds.X,
                    bounds.Y - _virtualBounds.Y, bounds.Width, bounds.Height),
                    new Int32Rect(0, 0, _desktop.PixelWidth, _desktop.PixelHeight));
                if (local.IsEmpty) continue;
                var image = new CroppedBitmap(_desktop, local);
                image.Freeze();
                _overlays.Add(new SelectionOverlay(this, bounds, image, _mode));
            }
            if (_overlays.Count == 0) { Complete(null); return; }
            foreach (var overlay in _overlays) overlay.Show();
            _overlays[0].Activate();
            UpdatePointer();
        }
        catch (Exception error)
        {
            _finishing = true;
            _pressed = false;
            _dragging = false;
            Mouse.Capture(null);
            foreach (var overlay in _overlays.Where(o => o.IsVisible).ToArray()) overlay.Close();
            _completion.TrySetException(error);
        }
    }

    internal void PointerDown(SelectionOverlay source)
    {
        if (!NativeMethods.GetCursorPos(out var point)) return;
        if (_mode == CaptureMode.Display)
        {
            Complete(source.Bounds);
            return;
        }
        if (_mode == CaptureMode.Window)
        {
            if (WindowAt(point) is { } window) Complete(window);
            return;
        }
        _start = point;
        _pressed = true;
        _dragging = false;
        Mouse.Capture(source, System.Windows.Input.CaptureMode.SubTree);
        UpdatePointer();
    }

    internal void PointerMove() => UpdatePointer();

    internal void PointerUp()
    {
        if (!_pressed || !NativeMethods.GetCursorPos(out var point)) return;
        _pressed = false;
        bool dragged = IsDragGesture(_start, point, _dragging, NativeMethods.GetSystemMetrics(NativeMethods.SmCxDrag), NativeMethods.GetSystemMetrics(NativeMethods.SmCyDrag));
        _dragging = false;
        Mouse.Capture(null);
        Int32Rect? result = ResolveAreaGesture(_start, point, dragged, WindowAt(point));
        if (result is not null) Complete(result);
        else UpdatePointer();
    }

    internal void Cancel() => Complete(null);

    internal void LostCapture()
    {
        if (_pressed && !_finishing)
            Cancel();
    }

    private void UpdatePointer()
    {
        if (!NativeMethods.GetCursorPos(out var point)) return;
        if (_mode == CaptureMode.Area && _pressed && !_dragging)
        {
            int dragWidth = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SmCxDrag));
            int dragHeight = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SmCyDrag));
            _dragging = IsDragGesture(_start, point, _dragging, dragWidth, dragHeight);
        }
        Int32Rect? highlight = _mode switch
        {
            CaptureMode.Window => WindowAt(point),
            CaptureMode.Display => _overlays.FirstOrDefault(o => point.X >= o.Bounds.X && point.X < o.Bounds.X + o.Bounds.Width &&
                point.Y >= o.Bounds.Y && point.Y < o.Bounds.Y + o.Bounds.Height)?.Bounds,
            _ when _dragging => ScreenGeometry.Normalize(new Point(_start.X, _start.Y), new Point(point.X, point.Y)),
            _ when !_pressed || !_dragging => WindowAt(point),
            _ => null
        };
        bool windowHighlight = _mode == CaptureMode.Window || (_mode == CaptureMode.Area && !_dragging);
        foreach (var overlay in _overlays)
            overlay.UpdateVisuals(highlight, point, _mode == CaptureMode.Area && _dragging, windowHighlight);
    }

    internal static bool IsDragGesture(NativeMethods.Point start, NativeMethods.Point current, bool alreadyDragging, int dragWidth, int dragHeight) =>
        alreadyDragging || Math.Abs(current.X - start.X) >= Math.Max(1, dragWidth) ||
        Math.Abs(current.Y - start.Y) >= Math.Max(1, dragHeight);

    internal static Int32Rect? ResolveAreaGesture(NativeMethods.Point start, NativeMethods.Point end, bool dragged,
        Int32Rect? windowAtClick)
    {
        if (!dragged) return windowAtClick;
        Int32Rect rect = ScreenGeometry.Normalize(new Point(start.X, start.Y), new Point(end.X, end.Y));
        return rect.Width >= 1 && rect.Height >= 1 ? rect : null;
    }

    internal static void RunGestureSelfTest()
    {
        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Area gesture self-test failed: " + message);
        }

        var start = new NativeMethods.Point { X = 100, Y = 80 };
        var target = new Int32Rect(40, 30, 800, 600);
        Require(!IsDragGesture(start, new NativeMethods.Point { X = 103, Y = 83 }, false, 4, 4), "movement below threshold became a drag");
        Require(IsDragGesture(start, new NativeMethods.Point { X = 104, Y = 80 }, false, 4, 4), "threshold movement did not become a drag");
        Require(IsDragGesture(start, new NativeMethods.Point { X = 101, Y = 81 }, true, 4, 4), "drag did not stay sticky");
        Require(ResolveAreaGesture(start, start, false, target) == target, "single click did not select hovered window");
        bool releaseOnlyDrag = IsDragGesture(start, new NativeMethods.Point { X = 108, Y = 85 }, false, 4, 4);
        Require(ResolveAreaGesture(start, new NativeMethods.Point { X = 108, Y = 85 }, releaseOnlyDrag, target) == new Int32Rect(100, 80, 8, 5), "release-only displacement was not classified as drag");
        Require(ResolveAreaGesture(start, new NativeMethods.Point { X = 102, Y = 81 }, false, null) is null, "no-target click should keep selector open");
        Require(ResolveAreaGesture(start, new NativeMethods.Point { X = 260, Y = 200 }, true, target) == new Int32Rect(100, 80, 160, 120), "drag did not override hovered window");
        Require(ResolveAreaGesture(start, new NativeMethods.Point { X = 101, Y = 81 }, true, target) == new Int32Rect(100, 80, 1, 1), "sticky drag state was lost after threshold");
    }

    internal static BitmapSource RenderSelectionPreviewForTest(BitmapSource desktop, bool windowHover)
    {
        var bounds = new Int32Rect(0, 0, desktop.PixelWidth, desktop.PixelHeight);
        var session = new SelectionSession(desktop, bounds, CaptureMode.Area);
        var overlay = new SelectionOverlay(session, bounds, desktop, CaptureMode.Area);
        Int32Rect highlight = windowHover ? new Int32Rect(332, 88, 340, 124) : new Int32Rect(180, 142, 310, 190);
        return overlay.RenderPreviewForTest(highlight, new NativeMethods.Point { X = 410, Y = 170 }, !windowHover, windowHover);
    }

    private Int32Rect? WindowAt(NativeMethods.Point point)
    {
        var excluded = _overlays.Select(o => o.Handle).Where(h => h != IntPtr.Zero).ToHashSet();
        var rect = NativeMethods.FindWindowAt(point, excluded);
        return rect is { Width: > 0, Height: > 0 }
            ? new Int32Rect(rect.Value.Left, rect.Value.Top, rect.Value.Width, rect.Value.Height)
            : null;
    }

    private void Complete(Int32Rect? result)
    {
        if (_finishing || _completion.Task.IsCompleted) return;
        _finishing = true;
        _pressed = false;
        _dragging = false;
        Mouse.Capture(null);
        try
        {
            foreach (var overlay in _overlays.Where(o => o.IsVisible).ToArray())
                overlay.Close();
            _completion.TrySetResult(result);
        }
        catch (Exception error)
        {
            _completion.TrySetException(error);
        }
    }

    private void Fail(Exception error)
    {
        if (_finishing || _completion.Task.IsCompleted) return;
        _finishing = true;
        _pressed = false;
        _dragging = false;
        Mouse.Capture(null);
        foreach (var overlay in _overlays.Where(o => o.IsVisible).ToArray()) overlay.Close();
        _completion.TrySetException(error);
    }
}

internal sealed class SelectionOverlay : Window
{
    private readonly SelectionSession _session;
    private readonly Canvas _canvas = new();
    private readonly Rectangle _highlight = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(255, 210, 74)),
        StrokeThickness = 1.5,
        Fill = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };
    private readonly Path _corners = new()
    {
        Stroke = Brushes.White,
        StrokeThickness = 2,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
        SnapsToDevicePixels = true
    };
    private readonly Border _dimensions;
    private readonly TextBlock _dimensionsText = new() { Foreground = Brushes.White, FontSize = 12 };
    private readonly LoupeVisual _loupe;
    private readonly BitmapSource _monitorImage;
    internal Int32Rect Bounds { get; }
    internal IntPtr Handle { get; private set; }

    internal SelectionOverlay(SelectionSession session, Int32Rect bounds, BitmapSource monitorImage, CaptureMode mode)
    {
        _session = session;
        Bounds = bounds;
        _monitorImage = monitorImage;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Content = _canvas;

        var background = new Image { Source = monitorImage, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(background, BitmapScalingMode.HighQuality);
        _canvas.Children.Add(background);
        _canvas.Children.Add(new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(72, 0, 0, 0)),
            IsHitTestVisible = false
        });
        _canvas.Children.Add(_highlight);
        _canvas.Children.Add(_corners);
        _dimensions = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 24, 24, 27)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 4, 9, 4),
            Child = _dimensionsText, Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };
        _canvas.Children.Add(_dimensions);

        string directions = mode switch
        {
            CaptureMode.Area => "Click a window or drag to select an area",
            CaptureMode.Window => "Click a window",
            _ => "Click a display"
        };
        var instructions = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(232, 24, 24, 27)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(72, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 8, 14, 8),
            Child = new TextBlock { Text = directions + "   ·   Esc to cancel", Foreground = Brushes.White, FontSize = 13 },
            IsHitTestVisible = false
        };
        Canvas.SetLeft(instructions, 16); Canvas.SetTop(instructions, 16);
        _canvas.Children.Add(instructions);

        _loupe = new LoupeVisual { Visibility = Visibility.Collapsed };
        _canvas.Children.Add(_loupe);

        SourceInitialized += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Width = Bounds.Width / dpi.DpiScaleX;
            Height = Bounds.Height / dpi.DpiScaleY;
            PositionNative();
        };
        Loaded += (_, _) => { PositionNative(); SizeChildren(); };
        SizeChanged += (_, _) => SizeChildren();
        MouseLeftButtonDown += (_, _) => _session.PointerDown(this);
        MouseMove += (_, _) => _session.PointerMove();
        MouseLeftButtonUp += (_, _) => _session.PointerUp();
        LostMouseCapture += (_, _) => _session.LostCapture();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) _session.Cancel(); };
        Closed += (_, _) => _session.Cancel();
    }

    private void PositionNative()
    {
        Handle = new WindowInteropHelper(this).Handle;
        long style = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GwlExStyle,
            new IntPtr(style | NativeMethods.WsExToolWindow));
        NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopmost, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height,
            NativeMethods.SwpNoActivate);
    }

    private void SizeChildren()
    {
        if (_canvas.Children[0] is FrameworkElement background) { background.Width = ActualWidth; background.Height = ActualHeight; }
        if (_canvas.Children[1] is FrameworkElement shade) { shade.Width = ActualWidth; shade.Height = ActualHeight; }
    }

    internal void UpdateVisuals(Int32Rect? physicalHighlight, NativeMethods.Point cursor, bool showLoupe,
        bool windowHighlight = false)
    {
        _highlight.Stroke = windowHighlight ? new SolidColorBrush(Color.FromRgb(50, 153, 255)) : new SolidColorBrush(Color.FromRgb(255, 210, 74));
        _highlight.Fill = windowHighlight ? new SolidColorBrush(Color.FromArgb(58, 50, 153, 255)) : new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
        _corners.Stroke = windowHighlight ? new SolidColorBrush(Color.FromRgb(190, 225, 255)) : Brushes.White;
        var intersection = physicalHighlight is null ? Int32Rect.Empty :
            ScreenGeometry.Intersect(physicalHighlight.Value, Bounds);
        if (intersection.IsEmpty)
        {
            _highlight.Visibility = Visibility.Collapsed;
            _corners.Visibility = Visibility.Collapsed;
            _dimensions.Visibility = Visibility.Collapsed;
        }
        else
        {
            Point topLeft = PhysicalToDip(intersection.X - Bounds.X, intersection.Y - Bounds.Y);
            Point bottomRight = PhysicalToDip(intersection.X - Bounds.X + intersection.Width, intersection.Y - Bounds.Y + intersection.Height);
            Canvas.SetLeft(_highlight, topLeft.X); Canvas.SetTop(_highlight, topLeft.Y);
            _highlight.Width = bottomRight.X - topLeft.X; _highlight.Height = bottomRight.Y - topLeft.Y;
            _highlight.Visibility = Visibility.Visible;
            _corners.Data = CreateCornerGeometry(topLeft, bottomRight);
            _corners.Visibility = Visibility.Visible;
            if (physicalHighlight is { } selected)
            {
                _dimensionsText.Text = $"{selected.Width} × {selected.Height} px";
                Canvas.SetLeft(_dimensions, Math.Max(4, Math.Min(topLeft.X, SurfaceWidth - 110)));
                Canvas.SetTop(_dimensions, topLeft.Y > 32 ? topLeft.Y - 28 : Math.Min(SurfaceHeight - 28, bottomRight.Y + 4));
                _dimensions.Visibility = Visibility.Visible;
            }
        }

        bool cursorHere = cursor.X >= Bounds.X && cursor.X < Bounds.X + Bounds.Width && cursor.Y >= Bounds.Y && cursor.Y < Bounds.Y + Bounds.Height;
        _loupe.Visibility = showLoupe && cursorHere ? Visibility.Visible : Visibility.Collapsed;
        if (!showLoupe || !cursorHere) return;
        int cx = cursor.X - Bounds.X, cy = cursor.Y - Bounds.Y;
        int selectionWidth = physicalHighlight?.Width ?? 0;
        int selectionHeight = physicalHighlight?.Height ?? 0;
        if (!_loupe.UpdateSample(_monitorImage, cx, cy, selectionWidth, selectionHeight)) return;
        Point cursorDip = PhysicalToDip(cx, cy);
        double left = cursorDip.X + 20, top = cursorDip.Y + 20;
        if (left + LoupeVisual.VisualWidth > SurfaceWidth) left = cursorDip.X - LoupeVisual.VisualWidth - 20;
        if (top + LoupeVisual.VisualHeight > SurfaceHeight) top = cursorDip.Y - LoupeVisual.VisualHeight - 20;
        Canvas.SetLeft(_loupe, Math.Max(0, left)); Canvas.SetTop(_loupe, Math.Max(0, top));
    }

    private double SurfaceWidth => ActualWidth > 0 ? ActualWidth : Bounds.Width;
    private double SurfaceHeight => ActualHeight > 0 ? ActualHeight : Bounds.Height;

    internal BitmapSource RenderPreviewForTest(Int32Rect highlight, NativeMethods.Point cursor, bool showLoupe, bool windowHighlight)
    {
        _canvas.Width = Bounds.Width; _canvas.Height = Bounds.Height;
        if (_canvas.Children[0] is FrameworkElement background) { background.Width = Bounds.Width; background.Height = Bounds.Height; }
        if (_canvas.Children[1] is FrameworkElement shade) { shade.Width = Bounds.Width; shade.Height = Bounds.Height; }
        _canvas.Measure(new Size(Bounds.Width, Bounds.Height));
        _canvas.Arrange(new Rect(0, 0, Bounds.Width, Bounds.Height));
        UpdateVisuals(highlight, cursor, showLoupe, windowHighlight);
        _canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Bounds.Width, Bounds.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_canvas); bitmap.Freeze();
        return bitmap;
    }

    private Point PhysicalToDip(double x, double y)
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformFromDevice.Transform(new Point(x, y)) ?? new Point(x, y);
    }

    private static Geometry CreateCornerGeometry(Point topLeft, Point bottomRight)
    {
        const double length = 11;
        double left = topLeft.X, top = topLeft.Y, right = bottomRight.X, bottom = bottomRight.Y;
        double horizontal = Math.Min(length, Math.Max(0, (right - left) / 2));
        double vertical = Math.Min(length, Math.Max(0, (bottom - top) / 2));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            Segment(context, left, top + vertical, left, top, left + horizontal, top);
            Segment(context, right - horizontal, top, right, top, right, top + vertical);
            Segment(context, right, bottom - vertical, right, bottom, right - horizontal, bottom);
            Segment(context, left + horizontal, bottom, left, bottom, left, bottom - vertical);
        }
        geometry.Freeze();
        return geometry;

        static void Segment(StreamGeometryContext context, double x1, double y1,
            double x2, double y2, double x3, double y3)
        {
            context.BeginFigure(new Point(x1, y1), false, false);
            context.PolyLineTo([new Point(x2, y2), new Point(x3, y3)], true, false);
        }
    }

    internal void ValidatePlacement()
    {
        if (Handle == IntPtr.Zero || !NativeMethods.GetWindowRect(Handle, out var actual))
            throw new InvalidOperationException($"Overlay has no readable HWND bounds; expected {Format(Bounds)}.");
        var expected = new NativeMethods.Rect
        {
            Left = Bounds.X, Top = Bounds.Y,
            Right = Bounds.X + Bounds.Width, Bottom = Bounds.Y + Bounds.Height
        };
        if (actual.Left != expected.Left || actual.Top != expected.Top ||
            actual.Right != expected.Right || actual.Bottom != expected.Bottom)
            throw new InvalidOperationException($"Overlay HWND bounds were {Format(actual)}; expected {Format(expected)}.");

        var dpi = VisualTreeHelper.GetDpi(this);
        double physicalWidth = ActualWidth * dpi.DpiScaleX;
        double physicalHeight = ActualHeight * dpi.DpiScaleY;
        if (Math.Abs(physicalWidth - Bounds.Width) > 1 || Math.Abs(physicalHeight - Bounds.Height) > 1)
            throw new InvalidOperationException(
                $"Overlay WPF extent was {ActualWidth:0.###}×{ActualHeight:0.###} DIPs at " +
                $"{dpi.PixelsPerInchX:0.###}×{dpi.PixelsPerInchY:0.###} DPI = " +
                $"{physicalWidth:0.###}×{physicalHeight:0.###} px; expected {Bounds.Width}×{Bounds.Height} px (±1 px).");
    }

    private static string Format(Int32Rect rect) => $"({rect.X},{rect.Y})-({rect.X + rect.Width},{rect.Y + rect.Height})";
    private static string Format(NativeMethods.Rect rect) => $"({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})";
}
