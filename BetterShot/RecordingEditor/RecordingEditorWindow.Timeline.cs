using BetterShot.Controls;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using static BetterShot.RecordingEditor.RecordingEditorChrome;

namespace BetterShot.RecordingEditor;

/// <summary>
/// The timeline panel (KhingShot layout): a toolbar (select, split mode, undo/redo, split at playhead, trim to the
/// playhead, delete; snapping, keyboard shortcuts, zoom out/slider/in, fit), the track label column (Video, System and
/// Mic with mute buttons, Events, Markup with hide/fold toggles), and the timeline itself.
/// </summary>
internal sealed partial class RecordingEditorWindow
{
    // Markup is the widest label row: icon + full name + hide/fold controls. Keep enough room that the
    // controls never squeeze the accessible label to "Mark" at the minimum editor width.
    private const double LabelColumnWidth = 152;
    private readonly Canvas _trackLabels = new() { Width = LabelColumnWidth, ClipToBounds = true };
    private readonly PillSlider _zoomSlider = new("Timeline zoom") { Width = 112, ShowFill = false };
    private Button? _selectModeButton, _splitModeButton, _undoButton, _redoButton, _deleteButton, _snapButton, _rowsButton;
    private bool _syncingZoom;

    private UIElement BuildTimelinePanel()
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.Children.Add(BuildTimelineToolbar());
        var body = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(_trackLabels);
        var lane = new Border { Background = Brush("#1A1B1D"), CornerRadius = new CornerRadius(0, 0, 13, 0), Child = _timeline, ClipToBounds = true };
        Grid.SetColumn(lane, 1);
        body.Children.Add(lane);
        Grid.SetRow(body, 1);
        panel.Children.Add(body);
        _timeline.ViewChanged += (_, _) => SyncZoomSlider();
        _zoomSlider.ValueChanged += (_, _) => { if (!_syncingZoom) _timeline.ZoomFraction = _zoomSlider.Value; };
        RebuildTrackLabels();
        return Panel(panel, new Thickness(0, 0, 0, 0));
    }

    private UIElement BuildTimelineToolbar()
    {
        var bar = new Grid { Height = 38, Margin = new Thickness(10, 1, 10, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _selectModeButton = FlatIconButton(LucideIconKind.MousePointer2, "Select (Esc leaves split mode)", (_, _) => { if (_splitMode) ToggleSplitMode(); }, 38, 18);
        left.Children.Add(_selectModeButton);
        _splitModeButton = FlatIconButton(LucideIconKind.Scissors, "Split mode: every click on a clip cuts it (B)", (_, _) => ToggleSplitMode(), 38, 18);
        left.Children.Add(_splitModeButton);
        left.Children.Add(VerticalRule());
        _undoButton = FlatIconButton(LucideIconKind.Undo2, "Undo (Ctrl+Z)", (_, _) => UndoEdit(), 36, 17);
        left.Children.Add(_undoButton);
        _redoButton = FlatIconButton(LucideIconKind.Redo2, "Redo (Ctrl+Y)", (_, _) => RedoEdit(), 36, 17);
        left.Children.Add(_redoButton);
        left.Children.Add(VerticalRule());
        left.Children.Add(FlatIconButton(LucideIconKind.Columns2, "Split at the playhead (Ctrl+B)", (_, _) => TimelineSplit(_playhead), 36, 17));
        left.Children.Add(FlatIconButton(LucideIconKind.ArrowLeftToLine, "Delete from the clip's start to the playhead (Q)", (_, _) => DeleteToPlayhead(fromClipStart: true), 36, 17));
        left.Children.Add(FlatIconButton(LucideIconKind.ArrowRightToLine, "Delete from the playhead to the clip's end (W)", (_, _) => DeleteToPlayhead(fromClipStart: false), 36, 17));
        _deleteButton = FlatIconButton(LucideIconKind.Trash2, "Delete the selected clip or mark (Delete)", (_, _) => DeleteOrPutBack(), 36, 17);
        left.Children.Add(_deleteButton);
        bar.Children.Add(left);

        var middle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        _status.MaxWidth = 900;
        middle.Children.Add(_progress);
        middle.Children.Add(_cancelOperation);
        middle.Children.Add(_status);
        Grid.SetColumn(middle, 1);
        bar.Children.Add(middle);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _snapButton = FlatIconButton(LucideIconKind.FoldHorizontal, "Snapping is on (N)", (_, _) => ToggleSnapping(), 38, 18);
        right.Children.Add(_snapButton);
        right.Children.Add(FlatIconButton(LucideIconKind.Keyboard, "Keyboard shortcuts (?)", (_, _) => ShowShortcutSheet(), 38, 18));
        right.Children.Add(VerticalRule());
        right.Children.Add(FlatIconButton(LucideIconKind.ZoomOut, "Zoom out (Ctrl+wheel)", (_, _) => _timeline.ZoomBy(0.8), 34, 17));
        _zoomSlider.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(_zoomSlider);
        right.Children.Add(FlatIconButton(LucideIconKind.ZoomIn, "Zoom in (Ctrl+wheel)", (_, _) => _timeline.ZoomBy(1.25), 34, 17));
        right.Children.Add(FlatIconButton(LucideIconKind.MoveHorizontal, "Fit the whole recording", (_, _) => _timeline.Fit(), 34, 17));
        Grid.SetColumn(right, 2);
        bar.Children.Add(right);
        return bar;
    }

    private void SyncZoomSlider()
    {
        _syncingZoom = true;
        _zoomSlider.Value = _timeline.ZoomFraction;
        _syncingZoom = false;
    }

    // ---------- Track labels ----------

    /// <summary>Rebuilds the label column so each name sits beside its lane (the lanes depend on which audio tracks exist).</summary>
    private void RebuildTrackLabels()
    {
        _trackLabels.Children.Clear();
        AddTrackLabel(LucideIconKind.Film, "Video", RecordingTimeline.VideoTop, RecordingTimeline.VideoHeight, null);
        for (int lane = 0; lane < _audioLanes.Count; lane++)
        {
            TimelineAudioLane audio = _audioLanes[lane];
            Button mute = FlatIconButton(audio.Muted ? LucideIconKind.VolumeX : LucideIconKind.Volume2, MuteTip(audio), (_, _) => ToggleTrackMute(audio.Key), 30, 16);
            mute.Tag = audio.Key;
            mute.Height = 22;
            mute.Padding = new Thickness(0);
            AddTrackLabel(audio.Icon, audio.Label, RecordingTimeline.AudioTop(lane), RecordingTimeline.AudioHeight, mute);
        }
        if (HasCamera)
        {
            var cameraTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _cameraVisibilityButton = FlatIconButton(LucideIconKind.Eye, "Hide camera picture-in-picture", (_, _) => ToggleCameraVisibility(), 28, 15);
            _cameraVisibilityButton.Height = 22;
            _cameraVisibilityButton.Padding = new Thickness(0);
            _cameraShapeButton = FlatIconButton(LucideIconKind.Circle, "Camera shape: circle or square", (sender, _) => OpenCameraShapeMenu((FrameworkElement)sender), 28, 15);
            _cameraShapeButton.Height = 22;
            _cameraShapeButton.Padding = new Thickness(0);
            cameraTools.Children.Add(_cameraVisibilityButton);
            cameraTools.Children.Add(_cameraShapeButton);
            AddTrackLabel(LucideIconKind.Camera, "Camera", RecordingTimeline.CameraTop(_audioLanes.Count), RecordingTimeline.CameraHeight, cameraTools);
        }
        AddTrackLabel(LucideIconKind.MousePointerClick, "Events", RecordingTimeline.EventsTop(_audioLanes.Count, HasCamera), RecordingTimeline.EventsHeight, null);
        var markupTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _hideMarkupButton = FlatIconButton(LucideIconKind.Eye, "Hide markup in the player and the export (redactions stay covered)", (_, _) => ToggleHideMarkup(), 28, 15);
        _hideMarkupButton.Height = 22;
        _hideMarkupButton.Padding = new Thickness(0);
        _rowsButton = FlatIconButton(LucideIconKind.Rows3, "Show every mark on its own row", (_, _) => ToggleMarkRows(), 28, 15);
        _rowsButton.Height = 22;
        _rowsButton.Padding = new Thickness(0);
        markupTools.Children.Add(_hideMarkupButton);
        markupTools.Children.Add(_rowsButton);
        AddTrackLabel(LucideIconKind.Pencil, "Markup", RecordingTimeline.MarksTop(_audioLanes.Count, HasCamera), RecordingTimeline.RowHeight, markupTools);
        UpdateTrackLabels();
    }

    private void AddTrackLabel(LucideIconKind icon, string text, double top, double height, UIElement? trailing)
    {
        var row = new Grid { Width = LabelColumnWidth - 18, Height = Math.Max(height, 22) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new LucideIcon { Icon = icon, Width = 15, Height = 15, Foreground = Brush("#D4D6DB"), VerticalAlignment = VerticalAlignment.Center });
        TextBlock name = Label(text, 13, Brushes.White, FontWeights.Normal);
        name.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        if (trailing is not null)
        {
            if (trailing is FrameworkElement element) element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(trailing, 2);
            row.Children.Add(trailing);
        }
        Canvas.SetLeft(row, 18);
        Canvas.SetTop(row, top + (height - row.Height) / 2);
        _trackLabels.Children.Add(row);
    }

    private static string MuteTip(TimelineAudioLane lane) => lane.Muted
        ? $"Unmute {lane.Label}: hear it again and include it in exports"
        : $"Mute {lane.Label}: silence it in the player, the exports, and the marked copy";

    /// <summary>Refreshes the mute and markup toggles without rebuilding the column.</summary>
    private void UpdateTrackLabels()
    {
        foreach (Button mute in _trackLabels.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<Button>()).Where(button => button.Tag is string))
        {
            if (_audioLanes.FirstOrDefault(lane => lane.Key == (string)mute.Tag) is not TimelineAudioLane lane || mute.Content is not LucideIcon icon) continue;
            icon.Icon = lane.Muted ? LucideIconKind.VolumeX : LucideIconKind.Volume2;
            mute.Foreground = lane.Muted ? DimText : Brushes.White;
            mute.ToolTip = MuteTip(lane);
        }
        if (_rowsButton is not null)
        {
            SetLit(_rowsButton, _view.MarksOnOwnRows);
            _rowsButton.ToolTip = _view.MarksOnOwnRows ? "Fold the marks into one row" : "Show every mark on its own row";
        }
    }

    private void UpdateTimelineToolbar()
    {
        if (_selectModeButton is not null) _selectModeButton.Background = _splitMode ? Brushes.Transparent : SurfaceBrush;
        if (_splitModeButton is not null)
        {
            _splitModeButton.Background = _splitMode ? ActiveBrush : Brushes.Transparent;
            _splitModeButton.Foreground = _splitMode ? Brushes.Black : Brushes.White;
        }
        if (_undoButton is not null) _undoButton.IsEnabled = _history.CanUndo;
        if (_redoButton is not null) _redoButton.IsEnabled = _history.CanRedo;
        if (_snapButton is not null)
        {
            SetLit(_snapButton, _snapping);
            _snapButton.ToolTip = _snapping ? "Snapping is on (N)" : "Snapping is off (N)";
        }
        if (_deleteButton?.Content is LucideIcon trash)
        {
            bool cutSelected = SelectedCutIndex is not null;
            trash.Icon = cutSelected ? LucideIconKind.Undo2 : LucideIconKind.Trash2;
            _deleteButton.ToolTip = cutSelected ? "Put the selected cut back" : "Delete the selected clip or mark (Delete)";
            _deleteButton.IsEnabled = cutSelected || _selectedMark is not null || _selectedSegment is not null;
        }
        UpdateTrackLabels();
    }

    private int? SelectedCutIndex => _selectedSegment is int index && index < State.Timeline.Segments.Count && !State.Timeline.Segments[index].Kept ? index : null;

    private void DeleteOrPutBack()
    {
        if (SelectedCutIndex is not null) PutSelectedCutBack();
        else DeleteSelection();
    }

    // ---------- Splitter ----------

    private UIElement BuildTimelineGrip()
    {
        var grip = new Thumb { Cursor = Cursors.SizeNS, ToolTip = "Drag to resize the timeline. Double-click for the default height.", Height = 10 };
        var template = new ControlTemplate(typeof(Thumb));
        var area = new FrameworkElementFactory(typeof(Border));
        area.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var handle = new FrameworkElementFactory(typeof(Border));
        handle.SetValue(FrameworkElement.WidthProperty, 40.0);
        handle.SetValue(FrameworkElement.HeightProperty, 4.0);
        handle.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        handle.SetValue(Border.BackgroundProperty, Brush("#4A4C51"));
        area.AppendChild(handle);
        template.VisualTree = area;
        grip.Template = template;
        grip.DragDelta += (_, e) => _timelineRow.Height = new GridLength(Math.Clamp(_timelineRow.Height.Value - e.VerticalChange, MinimumTimelineHeight, MaximumTimelineHeight));
        grip.MouseDoubleClick += (_, _) => _timelineRow.Height = new GridLength(DefaultTimelineHeight + (HasCamera ? RecordingTimeline.CameraHeight + RecordingTimeline.LaneGap : 0));
        return grip;
    }

    // ---------- Keyboard shortcut sheet ----------

    private void ShowShortcutSheet()
    {
        var list = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        list.ColumnDefinitions.Add(new ColumnDefinition());
        IReadOnlyList<(string Keys, string Action)> rows = RecordingEditorKeys.Descriptions;
        for (int index = 0; index < rows.Count; index++)
        {
            list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new Border
            {
                Background = SurfaceBrush, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 3, 14, 3),
                HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = rows[index].Keys, FontFamily = MonoFont, FontSize = 12.5, Foreground = Brushes.White }
            };
            Grid.SetRow(keys, index);
            list.Children.Add(keys);
            TextBlock action = Label(rows[index].Action, 13.5, Brush("#D4D6DB"), FontWeights.Normal);
            Grid.SetRow(action, index);
            Grid.SetColumn(action, 1);
            list.Children.Add(action);
        }
        var content = new StackPanel();
        content.Children.Add(Label("Keyboard shortcuts", 18, Brushes.White, FontWeights.SemiBold));
        content.Children.Add(new TextBlock
        {
            Text = "These keys follow CapCut: B is split mode, I and O set the in and out points. Press ? any time for this sheet.",
            Foreground = MutedText, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 460
        });
        content.Children.Add(list);
        Button close = TextButton("Close", "Close the shortcut sheet (Esc)", (_, _) => { }, null, 34);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 14, 0, 0);
        content.Children.Add(close);
        var sheet = new Window
        {
            Owner = this, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Title = "Keyboard shortcuts",
            Content = new Border { Background = PanelBrush, BorderBrush = SurfaceBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(22, 18, 22, 18), Child = content },
            FontFamily = FontFamily
        };
        bool closing = false;
        void CloseSheet()
        {
            if (closing) return;
            closing = true;
            sheet.Close();
        }
        sheet.Closing += (_, _) => closing = true;
        close.Click += (_, _) => CloseSheet();
        sheet.PreviewKeyDown += (_, e) => { if (e.Key is Key.Escape or Key.OemQuestion) { e.Handled = true; CloseSheet(); } };
        sheet.Deactivated += (_, _) => CloseSheet();
        sheet.Show();
    }
}
