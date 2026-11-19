using BetterShot.Controls;
using BetterShot.Persistence;
using BetterShot.Recording;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using static BetterShot.RecordingEditor.RecordingEditorChrome;

namespace BetterShot.RecordingEditor;

/// <summary>What the editor needs from the main window: current settings and library updates.</summary>
internal sealed record RecordingEditorContext(
    Func<CaptureSettings> Settings,
    Action<RecordingEntry> RecordingChanged,
    Action<RecordingEntry> RecordingAdded,
    string TemporaryFolder);

/// <summary>
/// Review and trim a finished recording: play it back, cut it with CapCut-style keys, draw markup (including
/// redactions that follow what they cover) on the frame at the playhead, and export a trimmed MP4, GIF, or timelapse.
/// Edits are non-destructive and saved in a hidden project sidecar beside the movie, so they reopen editable.
/// </summary>
internal sealed partial class RecordingEditorWindow : Window, IRecordingTimelineHost
{
    private const double DefaultTimelineHeight = 196, MinimumTimelineHeight = 120, MaximumTimelineHeight = 520;
    private static readonly List<RecordingEditorWindow> OpenEditors = [];

    private readonly RecordingEditorContext _context;
    private readonly RecordingTimeline _timeline;
    private readonly TextBlock _status = new() { Foreground = MutedText, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _progress = new() { Width = 160, Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button _cancelOperation;
    private readonly RowDefinition _timelineRow = new() { Height = new GridLength(DefaultTimelineHeight) };
    private RecordingEditHistory _history;
    private LoadedEditProject _loaded;
    private RecordingEditViewOptions _view;
    private string? _markedCopyFingerprint;
    private RecordingFrameReader? _reader;
    private VideoInfo? _info;
    private IReadOnlyList<RecordingEvent> _events = [];
    private CancellationTokenSource? _operation;
    private Guid? _selectedMark;
    private int? _selectedSegment;
    private bool _splitMode;
    private bool _snapping = true;
    private bool _closingConfirmed;

    private RecordingEditorWindow(RecordingEntry entry, RecordingEditorContext context)
    {
        Entry = entry;
        _context = context;
        _history = new RecordingEditHistory(RecordingEditState.Create(Math.Max(0.1, entry.DurationSeconds)));
        _loaded = RecordingEditProject.Empty(Math.Max(0.1, entry.DurationSeconds));
        _view = _loaded.View;
        _timeline = new RecordingTimeline(this);
        _cancelOperation = TextButton("Stop", "Stop the export or tracking", (_, _) => _operation?.Cancel(), null, 26);
        _cancelOperation.Visibility = Visibility.Collapsed;
        Title = "BetterShot Recording Editor - " + Path.GetFileName(entry.MoviePath);
        Width = 1240; Height = 860; MinWidth = 980; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#151617"); Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        Content = BuildLayout();
        _history.Changed += (_, _) => OnStateChanged();
        PreviewKeyDown += Editor_PreviewKeyDown;
        Loaded += async (_, _) => await OpenRecordingAsync();
        Closing += Editor_Closing;
        Closed += (_, _) => { OpenEditors.Remove(this); ShutDownPlayer(); };
    }

    internal RecordingEntry Entry { get; private set; }

    /// <summary>Opens the editor for a recording, or brings its already open editor to the front.</summary>
    internal static RecordingEditorWindow Open(RecordingEntry entry, RecordingEditorContext context)
    {
        RecordingEditorWindow? existing = OpenEditors.FirstOrDefault(window => string.Equals(window.Entry.MoviePath, entry.MoviePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var editor = new RecordingEditorWindow(entry, context);
        OpenEditors.Add(editor);
        editor.Show();
        return editor;
    }

    internal static void CloseAll()
    {
        foreach (RecordingEditorWindow editor in OpenEditors.ToArray()) editor.Close();
    }

    // ---------- IRecordingTimelineHost ----------

    public RecordingEditState State => _history.Current;
    public double Playhead => _playhead;
    public Guid? SelectedMarkId => _selectedMark;
    public int? SelectedSegmentIndex => _selectedSegment;
    public bool SplitMode => _splitMode;
    public bool Snapping => _snapping;
    public bool MarksOnOwnRows => _view.MarksOnOwnRows;
    public IReadOnlyList<RecordingEvent> Events => _events;

    public void TimelineSeek(double time) { if (_playing) Pause(); Seek(time); }

    public void TimelineSplit(double time)
    {
        if (Apply(state => state with { Timeline = state.Timeline.SplitAt(time) })) SetStatus($"Split at {FormatTime(time)}.");
    }

    public void TimelineSelectSegment(int index)
    {
        _selectedSegment = index;
        _selectedMark = null;
        RefreshSelectionUi();
    }

    public void TimelineSelectMark(Guid id)
    {
        _selectedMark = id;
        _selectedSegment = null;
        RefreshSelectionUi();
    }

    public void TimelineClearSelection()
    {
        _selectedMark = null;
        _selectedSegment = null;
        RefreshSelectionUi();
    }

    public void TimelineMoveMark(Guid id, double start, double duration)
    {
        if (State.Find(id) is not VideoMark mark) return;
        VideoMark moved = mark.WithTiming(start, duration, State.Timeline.Duration);
        double shift = moved.Start - mark.Start;
        if (mark.Tracked && Math.Abs(shift) > 1e-9 && Math.Abs(moved.Duration - mark.Duration) < 1e-9)
            moved = moved with { Keyframes = mark.Keyframes.Select(key => key with { Time = key.Time + shift }).ToArray(), LostAt = mark.LostAt + shift };
        Apply(state => state.WithMark(moved), "timeline-drag-" + id);
    }

    public void TimelineDragFinished() => _history.EndGroup();

    // ---------- Layout ----------

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(7) });
        root.RowDefinitions.Add(_timelineRow);
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });

        root.Children.Add(BuildToolbar());
        UIElement player = BuildPlayer();
        Grid.SetRow(player, 1);
        root.Children.Add(player);
        UIElement transport = BuildTransport();
        Grid.SetRow(transport, 2);
        root.Children.Add(transport);
        UIElement grip = BuildTimelineGrip();
        Grid.SetRow(grip, 3);
        root.Children.Add(grip);
        var timelineFrame = new Border { Background = Brush("#141517"), Child = _timeline };
        Grid.SetRow(timelineFrame, 4);
        root.Children.Add(timelineFrame);
        UIElement footer = BuildFooter();
        Grid.SetRow(footer, 5);
        root.Children.Add(footer);
        return root;
    }

    private UIElement BuildTimelineGrip()
    {
        var grip = new Thumb { Cursor = Cursors.SizeNS, ToolTip = "Drag to resize the timeline; double-click to reset", Background = Brush("#202122") };
        grip.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = GripVisual() };
        grip.DragDelta += (_, e) => _timelineRow.Height = new GridLength(Math.Clamp(_timelineRow.Height.Value - e.VerticalChange, MinimumTimelineHeight, MaximumTimelineHeight));
        grip.MouseDoubleClick += (_, _) => _timelineRow.Height = new GridLength(DefaultTimelineHeight);
        return grip;
    }

    private static FrameworkElementFactory GripVisual()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brush("#202122"));
        border.SetValue(Border.BorderBrushProperty, Brush("#2E2F32"));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 1, 0, 1));
        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(FrameworkElement.WidthProperty, 36.0);
        line.SetValue(FrameworkElement.HeightProperty, 3.0);
        line.SetValue(Border.CornerRadiusProperty, new CornerRadius(1.5));
        line.SetValue(Border.BackgroundProperty, Brush("#55585E"));
        border.AppendChild(line);
        return border;
    }

    private UIElement BuildFooter()
    {
        var bar = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(_status);
        var operation = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        operation.Children.Add(_progress);
        operation.Children.Add(_cancelOperation);
        Grid.SetColumn(operation, 1);
        bar.Children.Add(operation);
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        zoom.Children.Add(IconButton(LucideIconKind.Minus, "Zoom the timeline out (Ctrl+wheel)", (_, _) => _timeline.ZoomBy(0.8), 32, 28, 14));
        zoom.Children.Add(IconButton(LucideIconKind.MoveHorizontal, "Fit the whole recording", (_, _) => _timeline.Fit(), 32, 28, 14));
        zoom.Children.Add(IconButton(LucideIconKind.Plus, "Zoom the timeline in (Ctrl+wheel)", (_, _) => _timeline.ZoomBy(1.25), 32, 28, 14));
        Grid.SetColumn(zoom, 2);
        bar.Children.Add(zoom);
        return new Border { Background = Brush("#1B1C1D"), BorderBrush = Brush("#2A2B2D"), BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };
    }

    // ---------- Loading and saving ----------

    private async Task OpenRecordingAsync()
    {
        if (!File.Exists(Entry.MoviePath))
        {
            SetStatus("The recording is missing. It may have been moved or deleted.", true);
            return;
        }
        SetStatus("Opening the recording…");
        try
        {
            _reader = await RecordingFrameReader.OpenAsync(Entry.MoviePath);
            _info = _reader.Info;
        }
        catch (Exception error)
        {
            // Playback still works through the player; exports and tracking need Windows' media editing support.
            _info = new VideoInfo(Entry.DurationSeconds, Entry.Width, Entry.Height, 30, true);
            SetStatus($"Exports and tracking are unavailable: {error.Message}", true);
        }
        LoadProject(_info.Duration);
        LoadEvents();
        ConfigureSurface(_info.Width, _info.Height);
        OpenPlayer();
        _timeline.Fit();
        OnStateChanged();
        if (_loaded.Warning is string warning) SetStatus(warning, true);
        else if (_reader is not null) SetStatus("Space plays. B turns on split mode, Ctrl+B splits at the playhead, I/O set in and out, Q/W delete to the playhead.");
    }

    private void LoadProject(double duration)
    {
        _loaded = RecordingEditProject.Load(Entry.MoviePath, duration);
        _view = _loaded.View;
        _markedCopyFingerprint = _loaded.MarkedCopyFingerprint;
        _history = new RecordingEditHistory(_loaded.State);
        _history.Changed += (_, _) => OnStateChanged();
    }

    private void LoadEvents()
    {
        string path = RecordingEventLog.SidecarPath(Entry.MoviePath);
        try { _events = File.Exists(path) ? RecordingEventLog.Deserialize(File.ReadAllText(path)) : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { _events = []; } // Markers are a convenience only.
    }

    private bool SaveProject()
    {
        try
        {
            RecordingEditProject.Save(Entry.MoviePath, State, _view, _markedCopyFingerprint, _loaded.PreservedMarks, _loaded.UnknownFields);
            _history.MarkSaved();
            UpdateTitle();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save the edits: {error.Message}", true);
            return false;
        }
    }

    /// <summary>
    /// Saves the project. Leaving the editor (<paramref name="updateMarkedCopy"/>) also refreshes the marked copy that
    /// dragging shares; Ctrl+S only saves and offers "Update marked copy".
    /// </summary>
    private async Task<bool> SaveAsync(bool updateMarkedCopy)
    {
        CommitTextEdit();
        if (!SaveProject()) return false;
        if (updateMarkedCopy && NeedsMarkedCopyUpdate) return await UpdateMarkedCopyAsync();
        UpdateToolbarState();
        SetStatus(NeedsMarkedCopyUpdate
            ? "Saved. Choose Update marked copy (or Done) so dragging this recording shares the edited version."
            : "Saved. The recording stays as it was, so every cut and mark stays editable.");
        return true;
    }

    private async void Editor_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingConfirmed) return;
        CommitTextEdit();
        if (_operation is not null)
        {
            if (MessageBox.Show(this, "An export or tracking run is still going. Stop it and close the editor?", "BetterShot", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                e.Cancel = true;
                return;
            }
            _operation.Cancel();
        }
        if (!_history.IsDirty) return;
        MessageBoxResult answer = MessageBox.Show(this,
            "You have cuts or marks that are not saved.\n\nYes saves them, No leaves without saving, Cancel keeps editing.",
            "Leave without saving?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.No) return;
        e.Cancel = true;
        if (answer != MessageBoxResult.Yes) return;
        if (!await SaveAsync(updateMarkedCopy: true)) return;
        _closingConfirmed = true;
        Close();
    }

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        if (_history.IsDirty || NeedsMarkedCopyUpdate)
        {
            if (!await SaveAsync(updateMarkedCopy: true)) return;
        }
        _closingConfirmed = true;
        Close();
    }

    // ---------- State changes ----------

    /// <summary>Applies an undoable edit; <paramref name="coalesceKey"/> merges a drag into one undo step.</summary>
    private bool Apply(Func<RecordingEditState, RecordingEditState> edit, string? coalesceKey = null) => _history.Apply(edit, coalesceKey);

    private void OnStateChanged()
    {
        if (_selectedMark is Guid id && State.Find(id) is null) _selectedMark = null;
        if (_selectedSegment is int index && index >= State.Timeline.Segments.Count) _selectedSegment = null;
        UpdateTitle();
        RefreshSelectionUi();
    }

    private void RefreshSelectionUi()
    {
        UpdateToolbarState();
        RefreshOverlay();
        _timeline.InvalidateVisual();
    }

    private void UpdateTitle() =>
        Title = (_history.IsDirty ? "* " : string.Empty) + "BetterShot Recording Editor - " + Path.GetFileName(Entry.MoviePath);

    private void SetStatus(string message, bool error = false)
    {
        _status.Text = message;
        _status.ToolTip = message;
        _status.Foreground = error ? Brush("#FF8A8E") : MutedText;
    }

    // ---------- Keyboard ----------

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        RecordingEditorCommand command = RecordingEditorKeys.Map(key, Keyboard.Modifiers);
        if (command == RecordingEditorCommand.None) return;
        e.Handled = true;
        Execute(command);
    }

    private void Execute(RecordingEditorCommand command)
    {
        switch (command)
        {
            case RecordingEditorCommand.PlayPause: TogglePlay(); break;
            case RecordingEditorCommand.PreviousFrame: StepFrames(-1); break;
            case RecordingEditorCommand.NextFrame: StepFrames(1); break;
            case RecordingEditorCommand.FirstFrame: PauseAndSeek(State.Timeline.FirstPlayable); break;
            case RecordingEditorCommand.LastFrame: PauseAndSeek(Math.Max(0, State.Timeline.LastPlayable - FrameDuration)); break;
            case RecordingEditorCommand.JumpToIn: PauseAndSeek(State.Timeline.EffectiveIn); break;
            case RecordingEditorCommand.JumpToOut: PauseAndSeek(Math.Max(0, State.Timeline.EffectiveOut - FrameDuration)); break;
            case RecordingEditorCommand.ToggleSplitMode: ToggleSplitMode(); break;
            case RecordingEditorCommand.SplitAtPlayhead: TimelineSplit(_playhead); break;
            case RecordingEditorCommand.SetIn: SetInPoint(); break;
            case RecordingEditorCommand.SetOut: SetOutPoint(); break;
            case RecordingEditorCommand.DeleteToClipStart: DeleteToPlayhead(fromClipStart: true); break;
            case RecordingEditorCommand.DeleteToClipEnd: DeleteToPlayhead(fromClipStart: false); break;
            case RecordingEditorCommand.DeleteSelection: DeleteSelection(); break;
            case RecordingEditorCommand.ToggleSnapping: ToggleSnapping(); break;
            case RecordingEditorCommand.ToggleLoop: ToggleLoop(); break;
            case RecordingEditorCommand.Undo: UndoEdit(); break;
            case RecordingEditorCommand.Redo: RedoEdit(); break;
            case RecordingEditorCommand.Save: _ = SaveAsync(updateMarkedCopy: false); break;
            case RecordingEditorCommand.Escape: EscapePressed(); break;
        }
    }

    private void EscapePressed()
    {
        if (_drawStart is not null) { CancelDraw(); return; }
        if (_splitMode) { ToggleSplitMode(); return; }
        TimelineClearSelection();
    }

    private void UndoEdit()
    {
        CommitTextEdit();
        if (_history.Undo()) SetStatus("Undone.");
    }

    private void RedoEdit()
    {
        CommitTextEdit();
        if (_history.Redo()) SetStatus("Redone.");
    }

    // ---------- Cutting ----------

    private void ToggleSplitMode()
    {
        _splitMode = !_splitMode;
        SetStatus(_splitMode ? "Split mode: clicks cut. Click the clip track wherever it should be split; B or Esc turns it off." : "Split mode off.");
        UpdateToolbarState();
        _timeline.InvalidateVisual();
    }

    private void ToggleSnapping()
    {
        _snapping = !_snapping;
        SetStatus(_snapping ? "Snapping on: drags stick to the playhead, cuts, and other marks." : "Snapping off.");
        UpdateToolbarState();
    }

    private void SetInPoint()
    {
        if (Apply(state => state with { Timeline = state.Timeline.WithInPoint(_playhead) })) SetStatus($"In point set at {FormatTime(_playhead)}.");
    }

    private void SetOutPoint()
    {
        if (Apply(state => state with { Timeline = state.Timeline.WithOutPoint(_playhead) })) SetStatus($"Out point set at {FormatTime(_playhead)}.");
    }

    private void DeleteToPlayhead(bool fromClipStart)
    {
        bool changed = Apply(state => state with { Timeline = fromClipStart ? state.Timeline.DeleteFromClipStart(_playhead) : state.Timeline.DeleteToClipEnd(_playhead) });
        SetStatus(changed ? (fromClipStart ? "Deleted from the clip start to the playhead." : "Deleted from the playhead to the clip end.") : "Move the playhead inside a kept clip first.");
    }

    private void DeleteSelection()
    {
        if (_selectedMark is Guid id)
        {
            if (Apply(state => state.RemoveMark(id))) SetStatus("Mark deleted.");
            _selectedMark = null;
            RefreshSelectionUi();
            return;
        }
        if (_selectedSegment is int index && index < State.Timeline.Segments.Count && State.Timeline.Segments[index].Kept)
        {
            if (Apply(state => state with { Timeline = state.Timeline.SetKept(index, false) })) SetStatus("Clip cut. Select it and choose \"Put the selected cut back\" to restore it.");
            return;
        }
        SetStatus("Select a clip or a mark to delete.");
    }

    private void PutSelectedCutBack()
    {
        if (_selectedSegment is int index && index < State.Timeline.Segments.Count && !State.Timeline.Segments[index].Kept)
        {
            if (Apply(state => state with { Timeline = state.Timeline.SetKept(index, true) })) SetStatus("The cut is back in the recording.");
            return;
        }
        SetStatus("Select a cut on the clip track first.");
    }

    private void PutRecordingBack()
    {
        if (Apply(state => state with { Timeline = state.Timeline.Reset() }))
        {
            _selectedSegment = null;
            SetStatus("The recording is back the way it was: no cuts and no in or out points.");
        }
    }
}
