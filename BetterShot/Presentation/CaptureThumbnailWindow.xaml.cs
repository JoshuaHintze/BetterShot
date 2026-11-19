using BetterShot.Editing;
using BetterShot.Export;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterShot.Presentation;

public partial class CaptureThumbnailWindow : Window
{
    private readonly CapturePresentationManager _manager;
    private Point _pointerDown;
    private bool _clickArmed;
    private bool _dragInProgress;
    private readonly DispatcherTimer _autoDismissTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private TimeSpan _autoDismissRemaining;
    private DateTime _autoDismissLastTickUtc;
    internal CaptureDocument Document { get; }

    internal CaptureThumbnailWindow(CapturePresentationManager manager, CaptureDocument document)
    {
        _manager = manager;
        Document = document;
        InitializeComponent();
        ActionTray.IsHitTestVisible = false;
        Topmost = true;
        ThumbnailImage.Source = document.Render();
        document.Changed += Document_Changed;
        MouseEnter += (_, _) => ShowActions(true);
        MouseLeave += (_, _) => ShowActions(IsKeyboardFocusWithin);
        GotKeyboardFocus += (_, _) => ShowActions(true);
        LostKeyboardFocus += (_, _) => ShowActions(IsMouseOver);
        _autoDismissTimer.Tick += AutoDismissTimer_Tick;
        Closed += (_, _) => { _autoDismissTimer.Stop(); document.Changed -= Document_Changed; };
    }

    /// <summary>Starts (or restarts) the auto-dismiss countdown from the manager's current setting.</summary>
    internal void RestartAutoDismiss()
    {
        _autoDismissTimer.Stop();
        if (_manager.AutoDismissAfter <= TimeSpan.Zero) return;
        _autoDismissRemaining = _manager.AutoDismissAfter;
        _autoDismissLastTickUtc = DateTime.UtcNow;
        _autoDismissTimer.Start();
    }

    /// <summary>Hover, keyboard focus, an in-progress click or drag, or being hidden (capture in progress, over capacity) pause the countdown.</summary>
    private bool AutoDismissPaused => IsMouseOver || IsKeyboardFocusWithin || _clickArmed || _dragInProgress || !IsVisible;

    private void AutoDismissTimer_Tick(object? sender, EventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan elapsed = now - _autoDismissLastTickUtc;
        _autoDismissLastTickUtc = now;
        if (AutoDismissPaused) return;
        _autoDismissRemaining -= elapsed;
        if (_autoDismissRemaining > TimeSpan.Zero) return;
        _autoDismissTimer.Stop();
        _manager.Dismiss(this);
    }

    internal void AdvanceAutoDismissForTest(TimeSpan elapsed)
    {
        _autoDismissLastTickUtc = DateTime.UtcNow - elapsed;
        AutoDismissTimer_Tick(this, EventArgs.Empty);
    }

    internal bool AutoDismissActiveForTest => _autoDismissTimer.IsEnabled;

    private void Document_Changed(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => ThumbnailImage.Source = Document.Render(), System.Windows.Threading.DispatcherPriority.Background);

    private void ShowActions(bool show)
    {
        ActionTray.IsHitTestVisible = show;
        var animation = new System.Windows.Media.Animation.DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(130));
        ActionTray.BeginAnimation(OpacityProperty, animation);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<Button>(e.OriginalSource as DependencyObject) is not null) return;

        _pointerDown = e.GetPosition(Card);
        _clickArmed = Card.CaptureMouse();
        if (_clickArmed) e.Handled = true;
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_clickArmed) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _clickArmed = false;
            Card.ReleaseMouseCapture();
            return;
        }

        Point current = e.GetPosition(Card);
        if (Math.Abs(current.X - _pointerDown.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _pointerDown.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _clickArmed = false;
        _dragInProgress = true;
        Card.ReleaseMouseCapture();
        DragPreviewWindow? preview = null;
        GiveFeedbackEventHandler? updatePreview = null;
        try
        {
            DataObject data = CaptureDragData.CreateData(Document, _manager.DragAsFile, _manager.Export);
            try
            {
                if ((data.GetImage() ?? Document.Render()) is BitmapSource bitmap)
                {
                    preview = new DragPreviewWindow(bitmap);
                    new System.Windows.Interop.WindowInteropHelper(preview).EnsureHandle();
                    preview.UpdatePosition();
                    preview.Show();
                    preview.UpdatePosition();
                    updatePreview = (_, _) =>
                    {
                        try { preview.UpdatePosition(); }
                        catch { }
                    };
                    Card.AddHandler(DragDrop.GiveFeedbackEvent, updatePreview);
                }
            }
            catch
            {
                try { preview?.Close(); }
                catch { }
                preview = null;
            }
            DragDrop.DoDragDrop(Card, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not drag capture", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (updatePreview is not null) Card.RemoveHandler(DragDrop.GiveFeedbackEvent, updatePreview);
            try { preview?.Close(); }
            catch { }
            _dragInProgress = false;
            _clickArmed = false;
        }
        e.Handled = true;
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_clickArmed) return;

        Point released = e.GetPosition(Card);
        _clickArmed = false;
        Card.ReleaseMouseCapture();
        e.Handled = true;

        if (Math.Abs(released.X - _pointerDown.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(released.Y - _pointerDown.Y) < SystemParameters.MinimumVerticalDragDistance &&
            released.X >= 0 && released.Y >= 0 && released.X <= Card.ActualWidth && released.Y <= Card.ActualHeight)
            _manager.OpenEditor(Document);
    }

    private void Card_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragInProgress) _clickArmed = false;
    }

    private void Edit_Click(object sender, RoutedEventArgs e) { e.Handled = true; _manager.OpenEditor(Document); }
    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        BitmapSource rendered = ImageExporter.PrepareForClipboard(Document.Render(), _manager.Export);
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try { Clipboard.SetImage(rendered); return; }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5) { await Task.Delay(attempt * 60); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Could not copy capture", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        try { ImageExporter.SaveWithDialog(null, Document.Render(), _manager.Export, Document.CreatedUtc.ToLocalTime(), _manager.FileNameCounter()); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not save capture", MessageBoxButton.OK, MessageBoxImage.Error); }
    }


    private void AskClaude_Click(object sender, RoutedEventArgs e) { e.Handled = true; _manager.AskClaude(Document); }
    private void Dismiss_Click(object sender, RoutedEventArgs e) { e.Handled = true; _manager.Dismiss(this); }
    private void Delete_Click(object sender, RoutedEventArgs e) { e.Handled = true; _manager.RequestDelete(Document); }

    internal void RenderTo(string path) => SavePng(Document.Render(), path);
    internal void ShowActionsForTest(bool show) => ShowActions(show);
    internal bool ActionsFitForTest() => ActionTray.ActualWidth <= Card.ActualWidth && ActionTray.ActualHeight <= Card.ActualHeight;

    private static void SavePng(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string fullPath = Path.GetFullPath(path);
        string tempPath = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(stream);
            File.Move(tempPath, fullPath, true);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null) { if (child is T match) return match; child = VisualTreeHelper.GetParent(child); }
        return null;
    }
}
