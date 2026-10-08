using System.IO;
using System.Windows.Controls;
using System.Windows.Input;

namespace BetterShot.Editing;

public sealed partial class EditorWindow
{
    private static readonly Cursor GrabCursor = LoadNavigationCursor("Grab.cur");
    private static readonly Cursor GrabbingCursor = LoadNavigationCursor("Grabbing.cur");
    private bool _spaceHeld;
    private bool _isPanning;
    private Point _panStart;
    private double _panHorizontalOffset;
    private double _panVerticalOffset;

    internal bool SpaceNavigationActiveForTest => _spaceHeld;
    internal bool IsPanningForTest => _isPanning;
    internal ScrollViewer ScrollerForTest => _scroller;

    private void InitializeNavigation()
    {
        _scroller.PreviewMouseWheel += Scroller_PreviewMouseWheel;
        _scroller.PreviewMouseLeftButtonDown += Scroller_PreviewMouseLeftButtonDown;
        _scroller.PreviewMouseMove += Scroller_PreviewMouseMove;
        _scroller.PreviewMouseLeftButtonUp += Scroller_PreviewMouseLeftButtonUp;
        _scroller.LostMouseCapture += Scroller_LostMouseCapture;
    }

    private void Editor_Deactivated(object? sender, EventArgs e)
    {
        CommitTextEdit();
        ResetNavigation();
    }

    private void BeginSpaceNavigation()
    {
        if (_spaceHeld) return;
        if (_start is not null) { _image.ReleaseMouseCapture(); CancelDrag(); }
        if (_textGestureStart is not null) CancelTextGesture();
        _spaceHeld = true;
        UpdateNavigationCursor();
    }

    private bool TryBeginSpaceNavigation(bool textInputFocused)
    {
        if (textInputFocused) return false;
        BeginSpaceNavigation();
        return true;
    }

    private void EndSpaceNavigation()
    {
        _spaceHeld = false;
        EndPan();
        UpdateNavigationCursor();
    }

    private void EndPan()
    {
        _isPanning = false;
        if (_scroller.IsMouseCaptured) _scroller.ReleaseMouseCapture();
        UpdateNavigationCursor();
    }

    private void ResetNavigation()
    {
        _spaceHeld = false;
        _isPanning = false;
        if (_scroller.IsMouseCaptured) _scroller.ReleaseMouseCapture();
        UpdateNavigationCursor();
    }

    private void UpdateNavigationCursor()
    {
        _scroller.ForceCursor = _spaceHeld;
        _scroller.Cursor = _spaceHeld ? (_isPanning ? GrabbingCursor : GrabCursor) : null;
        _image.Cursor = _scroller.Cursor;
    }

    private static Cursor LoadNavigationCursor(string fileName)
    {
        Uri resource = new($"pack://application:,,,/Assets/Cursors/{fileName}", UriKind.Absolute);
        var stream = Application.GetResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing navigation cursor resource: {fileName}");
        using Stream cursorStream = stream.Stream;
        return new Cursor(cursorStream, scaleWithDpi: true);
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || e.Delta == 0) return;
        EndPan();
        if (_start is not null) { _image.ReleaseMouseCapture(); CancelDrag(); }
        if (_textGestureStart is not null) CancelTextGesture();
        Point anchorInScroller = e.GetPosition(_scroller);
        Point anchorInImage = e.GetPosition(_imageSurface);
        double nextZoom = _zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2);
        ZoomAt(anchorInScroller, anchorInImage, nextZoom);
        e.Handled = true;
    }

    private void ZoomAt(Point anchorInScroller, Point anchorInImage, double zoom)
    {
        double oldZoom = _zoom;
        double nextZoom = Math.Clamp(zoom, .1, 4);
        if (Math.Abs(nextZoom - oldZoom) < .0001) return;
        _fit = false;
        Point contentAnchor = new(anchorInImage.X / Math.Max(oldZoom, .0001), anchorInImage.Y / Math.Max(oldZoom, .0001));
        _zoom = nextZoom;
        ApplyZoom();
        UpdateLayout();
        Point after = _imageSurface.TranslatePoint(new Point(contentAnchor.X * nextZoom, contentAnchor.Y * nextZoom), _scroller);
        Vector correction = after - anchorInScroller;
        _scroller.ScrollToHorizontalOffset(_scroller.HorizontalOffset + correction.X);
        _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + correction.Y);
    }

    private void Scroller_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_spaceHeld) return;
        BeginPan(e.GetPosition(_scroller));
        e.Handled = true;
    }

    private void BeginPan(Point position)
    {
        _isPanning = true;
        _panStart = position;
        _panHorizontalOffset = _scroller.HorizontalOffset;
        _panVerticalOffset = _scroller.VerticalOffset;
        _scroller.CaptureMouse();
        UpdateNavigationCursor();
    }

    private void Scroller_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning || e.LeftButton != MouseButtonState.Pressed) return;
        MovePan(e.GetPosition(_scroller));
        e.Handled = true;
    }

    private void MovePan(Point current)
    {
        Vector delta = current - _panStart;
        _scroller.ScrollToHorizontalOffset(_panHorizontalOffset - delta.X);
        _scroller.ScrollToVerticalOffset(_panVerticalOffset - delta.Y);
    }

    private void Scroller_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning) return;
        EndPan();
        UpdateNavigationCursor();
        e.Handled = true;
    }

    private void Scroller_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        UpdateNavigationCursor();
    }

    internal bool BeginSpaceNavigationForTest(bool textInputFocused = false) => TryBeginSpaceNavigation(textInputFocused);
    internal void BeginPanForTest(Point position) => BeginPan(position);
    internal void MovePanForTest(Point position) => MovePan(position);
    internal void EndPanForTest() => EndPan();
    internal void EndSpaceNavigationForTest() => EndSpaceNavigation();
    internal void DeactivateForTest() => Editor_Deactivated(this, EventArgs.Empty);
    internal bool NavigationCursorForcedForTest => _scroller.ForceCursor;
    internal Cursor? NavigationCursorForTest => _scroller.Cursor;
    internal Cursor GrabCursorForTest => GrabCursor;
    internal Cursor GrabbingCursorForTest => GrabbingCursor;

    internal void ZoomAtForTest(Point anchorInScroller, Point anchorInImage, double zoom) =>
        ZoomAt(anchorInScroller, anchorInImage, zoom);
}
