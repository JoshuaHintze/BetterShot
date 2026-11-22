using BetterShot.Controls;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static BetterShot.RecordingEditor.RecordingEditorChrome;

namespace BetterShot.RecordingEditor;

/// <summary>Camera picture-in-picture preview, timeline controls, and direct manipulation on the video stage.</summary>
internal sealed partial class RecordingEditorWindow
{
    private CameraSource _cameraSource = new(null, 0, []);
    private readonly MediaElement _cameraMedia = new()
    {
        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true,
        Stretch = Stretch.Fill, IsMuted = true, Volume = 0
    };
    private readonly Canvas _cameraLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _cameraPip = new() { Background = Brushes.Black, ClipToBounds = true, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private Button? _cameraVisibilityButton, _cameraShapeButton;
    private bool _cameraReady, _cameraPlaying, _cameraDragging;
    private double _cameraDuration = double.PositiveInfinity;
    private Point _cameraDragStart;
    private CameraPlacement _cameraDragOrigin = CameraPlacement.Default;
    private Rect _lastCameraRect = Rect.Empty;
    private CameraShape? _lastCameraShape;

    public bool HasCamera => _cameraSource.IsAvailable;
    public bool CameraEnabled => State.Camera.Visible;
    public bool CameraVisibleAt(double sourceTime) => _cameraSource.IsVisibleAt(sourceTime, State.Camera) && (!double.IsFinite(_cameraDuration) || _cameraSource.CameraTimeAt(sourceTime, _cameraDuration) is not null);

    private void InitializeCameraOverlay()
    {
        _cameraPip.Children.Add(_cameraMedia);
        _cameraLayer.Children.Add(_cameraPip);
        _composedBase.Children.Add(_cameraLayer);
        _surface.PreviewMouseLeftButtonDown += CameraPip_MouseLeftButtonDown;
        _surface.PreviewMouseMove += CameraPip_MouseMove;
        _surface.PreviewMouseLeftButtonUp += CameraPip_MouseLeftButtonUp;
        _surface.LostMouseCapture += (_, _) => FinishCameraDrag();
        _cameraMedia.MediaOpened += async (_, _) =>
        {
            Uri? opened = _cameraMedia.Source;
            if (opened is null) return;
            // WPF may round NaturalDuration down to whole seconds on camera MP4s. Use the export
            // reader's timestamp range so that scrubbing near the end does not hide the camera early.
            double fallback = _cameraMedia.NaturalDuration.HasTimeSpan ? _cameraMedia.NaturalDuration.TimeSpan.TotalSeconds : double.PositiveInfinity;
            double duration = await MeasureDurationAsync(opened.LocalPath, fallback);
            if (_cameraMedia.Source != opened) return;
            _cameraDuration = duration;
            _cameraReady = true;
            _lastCameraRect = Rect.Empty;
            _timeline.InvalidateVisual();
            SyncCameraPreview(forceSeek: true);
        };
        _cameraMedia.MediaEnded += (_, _) => HoldLastCameraFrame();
        _cameraMedia.MediaFailed += (_, e) =>
        {
            _cameraReady = false;
            _cameraPlaying = false;
            _cameraPip.Visibility = Visibility.Collapsed;
            SetStatus($"Windows could not play the camera recording: {e.ErrorException?.Message ?? "Unknown media error."}", true);
        };
    }

    private void LoadCameraSource()
    {
        _cameraSource = CameraSource.Resolve(Entry, _events);
        if (_timelineRow.Height.Value == DefaultTimelineHeight && HasCamera)
            _timelineRow.Height = new GridLength(DefaultTimelineHeight + RecordingTimeline.CameraHeight + RecordingTimeline.LaneGap);
        RebuildTrackLabels();
        if (!HasCamera) { _cameraPip.Visibility = Visibility.Collapsed; return; }
        try
        {
            _cameraMedia.Source = new Uri(_cameraSource.Path!, UriKind.Absolute);
            _cameraMedia.Pause();
        }
        catch (Exception error) when (error is UriFormatException or InvalidOperationException)
        {
            SetStatus($"Windows could not open the camera recording: {error.Message}", true);
        }
    }

    private void ShutDownCameraPlayer()
    {
        _cameraPlaying = false;
        try
        {
            _cameraMedia.Stop();
            _cameraMedia.Close();
            _cameraMedia.Source = null;
        }
        catch (InvalidOperationException) { }
        _cameraReady = false;
    }

    private void SyncCameraPreview(bool forceSeek = false)
    {
        if (!HasCamera || !_cameraReady || _info is null)
        {
            _cameraPip.Visibility = Visibility.Collapsed;
            return;
        }

        Rect rect = _cameraSource.OverlayRectAt(_playhead, State.Camera, _info.Width, _info.Height);
        double? mappedTime = double.IsFinite(_cameraDuration)
            ? _cameraSource.CameraTimeAt(_playhead, _cameraDuration)
            : _cameraSource.CameraTimeAt(_playhead);
        bool visible = !rect.IsEmpty && mappedTime is not null;
        _cameraPip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            if (rect != _lastCameraRect || State.Camera.Shape != _lastCameraShape) PositionCameraOverlay(rect);
            double target = mappedTime!.Value;
            // MediaElement commonly clears its surface at NaturalDuration. Hold the last decodable frame instead,
            // including the normal few-millisecond encoder mismatch at the end of a screen recording.
            if (double.IsFinite(_cameraDuration) && target >= _cameraDuration - CameraSource.EndFrameToleranceSeconds)
                target = Math.Max(0, _cameraDuration - CameraSource.EndFrameToleranceSeconds);
            double current = _cameraMedia.Position.TotalSeconds;
            if (forceSeek || Math.Abs(current - target) > 0.12) _cameraMedia.Position = TimeSpan.FromSeconds(target);
            if (_playing)
            {
                _cameraMedia.SpeedRatio = _speed;
                if (!_cameraPlaying) { _cameraMedia.Play(); _cameraPlaying = true; }
            }
            else if (_cameraPlaying) { _cameraMedia.Pause(); _cameraPlaying = false; }
        }
        else if (_cameraPlaying)
        {
            _cameraMedia.Pause();
            _cameraPlaying = false;
        }
    }

    private void HoldLastCameraFrame()
    {
        _cameraPlaying = false;
        _cameraMedia.Pause();
        if (double.IsFinite(_cameraDuration))
            _cameraMedia.Position = TimeSpan.FromSeconds(Math.Max(0, _cameraDuration - CameraSource.EndFrameToleranceSeconds));
    }

    private void PositionCameraOverlay(Rect rect)
    {
        _cameraPip.Width = rect.Width;
        _cameraPip.Height = rect.Height;
        // Use a centered source crop even when the camera and screen have different aspect ratios.
        double width = Math.Max(1, _cameraMedia.NaturalVideoWidth);
        double height = Math.Max(1, _cameraMedia.NaturalVideoHeight);
        double scale = Math.Max(rect.Width / width, rect.Height / height);
        _cameraMedia.Width = width * scale;
        _cameraMedia.Height = height * scale;
        Canvas.SetLeft(_cameraMedia, (rect.Width - _cameraMedia.Width) / 2);
        Canvas.SetTop(_cameraMedia, (rect.Height - _cameraMedia.Height) / 2);
        Canvas.SetLeft(_cameraPip, rect.X);
        Canvas.SetTop(_cameraPip, rect.Y);
        _cameraPip.Clip = State.Camera.Shape == CameraShape.Circle
            ? new EllipseGeometry(new Point(rect.Width / 2, rect.Height / 2), rect.Width / 2, rect.Height / 2)
            : new RectangleGeometry(new Rect(0, 0, rect.Width, rect.Height));
        _lastCameraRect = rect;
        _lastCameraShape = State.Camera.Shape;
    }

    private void ToggleCameraVisibility()
    {
        bool visible = !State.Camera.Visible;
        if (Apply(state => state.WithCameraVisibility(visible)))
            SetStatus(visible ? "Camera picture-in-picture shown." : "Camera picture-in-picture hidden.");
    }

    private void OpenCameraShapeMenu(FrameworkElement target)
    {
        ContextMenu menu = Menu();
        menu.Items.Add(MenuItem("Circle", (_, _) => SetCameraShape(CameraShape.Circle), LucideIconKind.Circle, isChecked: State.Camera.Shape == CameraShape.Circle));
        menu.Items.Add(MenuItem("Square", (_, _) => SetCameraShape(CameraShape.Square), LucideIconKind.Square, isChecked: State.Camera.Shape == CameraShape.Square));
        OpenBelow(menu, target);
    }

    private void SetCameraShape(CameraShape shape)
    {
        if (Apply(state => state.WithCameraShape(shape))) SetStatus($"Camera shape: {shape.ToString().ToLowerInvariant()}.");
    }

    private void UpdateCameraControls()
    {
        if (_cameraVisibilityButton?.Content is LucideIcon eye)
        {
            eye.Icon = State.Camera.Visible ? LucideIconKind.Eye : LucideIconKind.EyeOff;
            _cameraVisibilityButton.ToolTip = State.Camera.Visible ? "Hide camera picture-in-picture" : "Show camera picture-in-picture";
            SetLit(_cameraVisibilityButton, State.Camera.Visible);
        }
        if (_cameraShapeButton?.Content is LucideIcon shape)
        {
            shape.Icon = State.Camera.Shape == CameraShape.Circle ? LucideIconKind.Circle : LucideIconKind.Square;
            _cameraShapeButton.ToolTip = $"Camera shape: {State.Camera.Shape.ToString().ToLowerInvariant()} (click to change)";
        }
    }

    private void CameraPip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_tool != VideoMarkTool.Select || !State.Camera.Visible || _info is null || _cameraPip.Visibility != Visibility.Visible) return;
        Point point = e.GetPosition(_surface);
        if (!CameraHit(point) || HasMarkupHitAt(point)) return;
        e.Handled = true;
        _cameraDragging = true;
        _cameraDragStart = point;
        _cameraDragOrigin = State.Camera.Placement;
        _surface.CaptureMouse();
        _surface.Cursor = Cursors.SizeAll;
    }

    private void CameraPip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_cameraDragging)
        {
            Point point = e.GetPosition(_surface);
            if (_tool == VideoMarkTool.Select && _info is not null && CameraHit(point) && !HasMarkupHitAt(point))
            {
                _surface.Cursor = Cursors.SizeAll;
                e.Handled = true;
            }
            else _surface.ClearValue(CursorProperty);
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed || _info is null) return;
        Point now = e.GetPosition(_surface);
        Rect original = _cameraDragOrigin.ToPixels(_info.Width, _info.Height);
        double availableX = Math.Max(1, _info.Width - original.Width), availableY = Math.Max(1, _info.Height - original.Height);
        var placement = new CameraPlacement((original.X + now.X - _cameraDragStart.X) / availableX,
            (original.Y + now.Y - _cameraDragStart.Y) / availableY, _cameraDragOrigin.Size).Clamp();
        Apply(state => state.WithCameraPlacement(placement), "camera-placement");
        e.Handled = true;
    }

    private bool CameraHit(Point point)
    {
        if (_cameraPip.Visibility != Visibility.Visible || _info is null) return false;
        Rect rect = State.Camera.Placement.ToPixels(_info.Width, _info.Height);
        if (!rect.Contains(point)) return false;
        if (State.Camera.Shape != CameraShape.Circle) return true;
        double dx = (point.X - (rect.X + rect.Width / 2)) / Math.Max(1, rect.Width / 2);
        double dy = (point.Y - (rect.Y + rect.Height / 2)) / Math.Max(1, rect.Height / 2);
        return dx * dx + dy * dy <= 1;
    }

    private bool HasMarkupHitAt(Point point)
    {
        double tolerance = 6 / Math.Max(0.01, DisplayScale);
        if (SelectedMark is VideoMark selected && selected.IsActiveAt(_playhead))
        {
            if (Handles(selected.ShapeAt(_playhead)).Any(handle => (handle - point).Length <= tolerance * 1.6)) return true;
            if (selected.ShapeAt(_playhead).Hit(point, tolerance)) return true;
        }
        return State.VisibleMarksAt(_playhead).Reverse().Any(mark => mark.ShapeAt(_playhead).Hit(point, tolerance));
    }

    private void CameraPip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_cameraDragging) return;
        e.Handled = true;
        FinishCameraDrag();
    }

    private void FinishCameraDrag()
    {
        if (!_cameraDragging) return;
        _cameraDragging = false;
        if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        _surface.ClearValue(CursorProperty);
        _history.EndGroup();
    }
}
