using BetterShot.Controls;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BetterShot.Editing;

public sealed partial class EditorSurface
{
    private const double CropHandleSize = 12;
    private const double MinimumCropSize = 8;
    private readonly Canvas _cropOverlay = new() { Background = null, ClipToBounds = false };
    private readonly Dictionary<CropHandle, Thumb> _cropThumbs = [];
    private readonly List<Line> _cropGrid = [];
    private bool _cropping;
    private Rect _pendingCrop;
    private Point? _cropDragStart;
    private Rect _cropBeforeDrag;
    private Rect _cropGestureStart;
    private Vector _cropGestureDelta;
    private Path? _cropDim;
    private Rectangle? _cropBorder;
    private TextBlock? _cropLabel;
    private Border? _cropLabelChrome;
    private StackPanel? _cropActions;

    internal bool IsCroppingForTest => _cropping;
    internal Rect PendingCropForTest => _pendingCrop;
    internal Canvas CropOverlayForTest => _cropOverlay;
    internal void BeginCropForTest() => SelectTool(EditorTool.Crop);
    internal void SetPendingCropForTest(Rect crop) { _pendingCrop = crop; UpdateCropOverlay(); }
    internal void DragCropHandleForTest(CropHandle handle, Vector displayDelta)
    {
        Thumb thumb = _cropThumbs[handle];
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(displayDelta.X, displayDelta.Y) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(displayDelta.X, displayDelta.Y, false) { RoutedEvent = Thumb.DragCompletedEvent });
    }
    internal void DoneForTest() => Done();

    private void BeginCrop()
    {
        CommitTextEdit();
        if (_cropping) return;
        if (_tool != EditorTool.Crop) _toolBeforeCrop = _tool;
        _selected = null;
        _cropping = true;
        _tool = EditorTool.Crop;
        _pendingCrop = _document.Crop ?? _document.CanvasRect;
        BuildCropOverlay();
        UpdateToolVisuals();
        _status.Text = Info(EditorTool.Crop).Hint;
        Refresh();
    }

    private void ApplyCrop()
    {
        if (!_cropping) return;
        Rect pending = _pendingCrop;
        EndCrop();
        Rect? before = _document.Crop;
        _document.SetCrop(pending);
        if (Nullable.Equals(before, _document.Crop)) Refresh();
        _status.Text = _document.Crop is Rect crop ? $"Cropped to {crop.Width:0} × {crop.Height:0} px. The original is kept; crop again to adjust." : "Showing the full capture.";
    }

    private void CancelCrop()
    {
        if (!_cropping) return;
        EndCrop();
        Refresh();
        _status.Text = "Crop cancelled.";
    }

    private void EndCrop()
    {
        if (_cropDragStart is not null) { _cropDragStart = null; _image.ReleaseMouseCapture(); }
        _cropping = false;
        _cropOverlay.Children.Clear();
        _cropThumbs.Clear();
        _cropGrid.Clear();
        _cropDim = null; _cropBorder = null; _cropLabel = null; _cropLabelChrome = null; _cropActions = null;
        _tool = _toolBeforeCrop == EditorTool.Crop ? EditorTool.Select : _toolBeforeCrop;
        UpdateToolVisuals();
        UpdateInspector();
        UpdateToolbarExtras();
        _image.Focus();
    }

    private void BuildCropOverlay()
    {
        _cropOverlay.Children.Clear();
        _cropThumbs.Clear();
        _cropGrid.Clear();
        _cropDim = new Path { Fill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), IsHitTestVisible = false };
        _cropOverlay.Children.Add(_cropDim);
        for (int index = 0; index < 4; index++)
        {
            var line = new Line { Stroke = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), StrokeThickness = 1, IsHitTestVisible = false };
            _cropGrid.Add(line);
            _cropOverlay.Children.Add(line);
        }
        _cropBorder = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
        _cropOverlay.Children.Add(_cropBorder);
        AddCropThumb(CropHandle.Move, Cursors.SizeAll);
        AddCropThumb(CropHandle.Left, Cursors.SizeWE); AddCropThumb(CropHandle.Right, Cursors.SizeWE);
        AddCropThumb(CropHandle.Top, Cursors.SizeNS); AddCropThumb(CropHandle.Bottom, Cursors.SizeNS);
        AddCropThumb(CropHandle.TopLeft, Cursors.SizeNWSE); AddCropThumb(CropHandle.BottomRight, Cursors.SizeNWSE);
        AddCropThumb(CropHandle.TopRight, Cursors.SizeNESW); AddCropThumb(CropHandle.BottomLeft, Cursors.SizeNESW);
        _cropLabel = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
        _cropLabelChrome = new Border { Background = Brush("#CC1B1C1D"), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 3, 7, 3), Child = _cropLabel, IsHitTestVisible = false };
        _cropOverlay.Children.Add(_cropLabelChrome);
        var apply = IconTextButton(LucideIconKind.Check, "Apply", "Apply crop (Enter)", (_, _) => ApplyCrop(), 92);
        apply.Background = Brush("#FFD400"); apply.Foreground = Brushes.Black;
        var cancel = IconTextButton(LucideIconKind.X, "Cancel", "Cancel crop (Esc)", (_, _) => CancelCrop(), 100);
        _cropActions = new StackPanel { Orientation = Orientation.Horizontal };
        _cropActions.Children.Add(apply); _cropActions.Children.Add(cancel);
        _cropOverlay.Children.Add(_cropActions);
    }

    private void AddCropThumb(CropHandle handle, Cursor cursor)
    {
        bool move = handle == CropHandle.Move;
        var thumb = new Thumb { Tag = handle, Cursor = cursor, Width = CropHandleSize, Height = CropHandleSize, Background = Brushes.White, BorderBrush = Brushes.Black, Opacity = move ? .01 : 1 };
        thumb.DragStarted += (_, _) => { _cropGestureStart = _pendingCrop; _cropGestureDelta = default; };
        thumb.DragDelta += (_, e) =>
        {
            _cropGestureDelta += new Vector(e.HorizontalChange / _zoom, e.VerticalChange / _zoom);
            _pendingCrop = CropTransform.Apply(_cropGestureStart, handle, _cropGestureDelta, _document.CanvasRect, MinimumCropSize);
            UpdateCropOverlay();
        };
        thumb.DragCompleted += (_, e) => { if (e.Canceled) { _pendingCrop = _cropGestureStart; UpdateCropOverlay(); } };
        _cropThumbs[handle] = thumb;
        if (!move) Panel.SetZIndex(thumb, 2);
        _cropOverlay.Children.Add(thumb);
    }

    private void UpdateCropOverlay()
    {
        if (!_cropping || _cropDim is null || _cropBorder is null || _cropLabel is null || _cropLabelChrome is null || _cropActions is null) return;
        var full = new Rect(0, 0, _layout.OutputWidth * _zoom, _layout.OutputHeight * _zoom);
        Rect crop = new(_pendingCrop.X * _zoom, _pendingCrop.Y * _zoom, _pendingCrop.Width * _zoom, _pendingCrop.Height * _zoom);
        _cropDim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(crop));
        Place(_cropBorder, crop.Left, crop.Top, crop.Width, crop.Height);
        for (int index = 0; index < 2; index++)
        {
            double x = crop.Left + crop.Width * (index + 1) / 3, y = crop.Top + crop.Height * (index + 1) / 3;
            SetLine(_cropGrid[index], x, crop.Top, x, crop.Bottom);
            SetLine(_cropGrid[index + 2], crop.Left, y, crop.Right, y);
        }
        foreach ((CropHandle handle, Thumb thumb) in _cropThumbs)
        {
            if (handle == CropHandle.Move) { Place(thumb, crop.Left, crop.Top, crop.Width, crop.Height); continue; }
            double x = handle is CropHandle.Left or CropHandle.TopLeft or CropHandle.BottomLeft ? crop.Left : handle is CropHandle.Right or CropHandle.TopRight or CropHandle.BottomRight ? crop.Right : crop.Left + crop.Width / 2;
            double y = handle is CropHandle.Top or CropHandle.TopLeft or CropHandle.TopRight ? crop.Top : handle is CropHandle.Bottom or CropHandle.BottomLeft or CropHandle.BottomRight ? crop.Bottom : crop.Top + crop.Height / 2;
            Place(thumb, x - CropHandleSize / 2, y - CropHandleSize / 2, CropHandleSize, CropHandleSize);
        }
        _cropLabel.Text = $"{Math.Round(_pendingCrop.Width):0} × {Math.Round(_pendingCrop.Height):0}";
        Canvas.SetLeft(_cropLabelChrome, crop.Left + 8);
        Canvas.SetTop(_cropLabelChrome, crop.Top + 8);
        const double actionsWidth = 196, actionsHeight = 36;
        double left = Math.Clamp(crop.Left + crop.Width / 2 - actionsWidth / 2, 4, Math.Max(4, full.Width - actionsWidth - 4));
        double top = crop.Bottom + 10 + actionsHeight <= full.Height ? crop.Bottom + 10 : Math.Max(crop.Top + 4, crop.Bottom - actionsHeight - 10);
        Canvas.SetLeft(_cropActions, left);
        Canvas.SetTop(_cropActions, Math.Clamp(top, 4, Math.Max(4, full.Height - actionsHeight - 4)));
    }

    private static void Place(FrameworkElement element, double left, double top, double width, double height)
    {
        element.Width = Math.Max(0, width); element.Height = Math.Max(0, height);
        Canvas.SetLeft(element, left); Canvas.SetTop(element, top);
    }

    private static void SetLine(Line line, double x1, double y1, double x2, double y2)
    {
        line.X1 = x1; line.Y1 = y1; line.X2 = x2; line.Y2 = y2;
    }

    private void BeginCropDrag(Point point)
    {
        _cropDragStart = point;
        _cropBeforeDrag = _pendingCrop;
        _image.CaptureMouse();
    }

    private void UpdateCropDrag(Point point)
    {
        if (_cropDragStart is not Point start) return;
        _pendingCrop = new Rect(start, point);
        UpdateCropOverlay();
    }

    private void EndCropDrag()
    {
        if (_cropDragStart is null) return;
        _cropDragStart = null;
        if (_pendingCrop.Width < MinimumCropSize || _pendingCrop.Height < MinimumCropSize) _pendingCrop = _cropBeforeDrag;
        UpdateCropOverlay();
    }
}

internal enum CropHandle { Move, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

internal static class CropTransform
{
    /// <summary>Moves the edges named by <paramref name="handle"/>, keeping the crop inside the capture and above a minimum size.</summary>
    internal static Rect Apply(Rect start, CropHandle handle, Vector delta, Rect canvas, double minimum)
    {
        if (handle == CropHandle.Move)
        {
            double dx = Clamp(delta.X, canvas.Left - start.Left, canvas.Right - start.Right);
            double dy = Clamp(delta.Y, canvas.Top - start.Top, canvas.Bottom - start.Bottom);
            return new Rect(start.X + dx, start.Y + dy, start.Width, start.Height);
        }
        double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        if (handle is CropHandle.Left or CropHandle.TopLeft or CropHandle.BottomLeft) left = Clamp(left + delta.X, canvas.Left, right - minimum);
        if (handle is CropHandle.Right or CropHandle.TopRight or CropHandle.BottomRight) right = Clamp(right + delta.X, left + minimum, canvas.Right);
        if (handle is CropHandle.Top or CropHandle.TopLeft or CropHandle.TopRight) top = Clamp(top + delta.Y, canvas.Top, bottom - minimum);
        if (handle is CropHandle.Bottom or CropHandle.BottomLeft or CropHandle.BottomRight) bottom = Clamp(bottom + delta.Y, top + minimum, canvas.Bottom);
        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    private static double Clamp(double value, double minimum, double maximum) => maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);
}
