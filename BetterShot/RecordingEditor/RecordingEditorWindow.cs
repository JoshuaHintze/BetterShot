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

/// <summary>What the editor needs from the main window: current settings (and a way to save "Replace the original"), and library updates.</summary>
internal sealed record RecordingEditorContext(
    Func<CaptureSettings> Settings,
    Action<RecordingEntry> RecordingChanged,
    Action<RecordingEntry> RecordingAdded,
    string TemporaryFolder,
    Action<Func<CaptureSettings, CaptureSettings>>? UpdateSettings = null);

/// <summary>
/// Review and trim a finished recording: play it back, cut it with CapCut-style keys, draw markup (including
/// redactions that follow what they cover) on the frame at the playhead, and export a trimmed MP4, GIF, or timelapse.
/// Edits are non-destructive and saved in a hidden project sidecar beside the movie, so they reopen editable.
/// </summary>
internal sealed partial class RecordingEditorWindow : Window, IRecordingTimelineHost
{
    private const double DefaultTimelineHeight = 218, MinimumTimelineHeight = 140, MaximumTimelineHeight = 520;
    private static readonly List<RecordingEditorWindow> OpenEditors = [];

    private readonly RecordingEditorContext _context;
    private readonly RecordingTimeline _timeline;
    private readonly TextBlock _status = new() { Foreground = MutedText, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _progress = new() { Width = 120, Height = 5, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Foreground = ActiveBrush, Background = SurfaceBrush, BorderThickness = new Thickness(0) };
    private readonly Button _cancelOperation;
    private readonly RowDefinition _timelineRow = new() { Height = new GridLength(DefaultTimelineHeight) };
    private readonly Grid _contentGrid = new();
    private UIElement _inspectorPanel = new Grid();
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
    private bool _closingFlowActive;

    private RecordingEditorWindow(RecordingEntry entry, RecordingEditorContext context)
    {
        Entry = entry;
        _context = context;
        _history = new RecordingEditHistory(RecordingEditState.Create(Math.Max(0.1, entry.DurationSeconds)));
        _loaded = RecordingEditProject.Empty(Math.Max(0.1, entry.DurationSeconds));
        _view = _loaded.View;
        _replaceOriginal = context.Settings().TrimReplacesOriginal;
        _timeline = new RecordingTimeline(this);
        _cancelOperation = TextButton("Stop", "Stop the export or tracking", (_, _) => _operation?.Cancel(), null, 26);
        _cancelOperation.Margin = new Thickness(0, 0, 10, 0);
        _cancelOperation.Visibility = Visibility.Collapsed;
        Title = "BetterShot Recording Editor - " + Path.GetFileName(entry.MoviePath);
        Width = 1280; Height = 820; MinWidth = 960; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = WindowBrush; Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 14;
        UseLayoutRounding = true;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/BetterShot.ico")); }
        catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException) { } // The taskbar falls back to the app icon.
        ConfigureWindowChrome();
        Content = BuildLayout();
        _history.Changed += (_, _) => OnStateChanged();
        PreviewKeyDown += Editor_PreviewKeyDown;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Loaded += async (_, _) => await OpenRecordingAsync();
        Closing += Editor_Closing;
        Closed += (_, _) => { OpenEditors.Remove(this); ShutDownPlayer(); DisposeAudio(); _reader?.Dispose(); _reader = null; };
    }

    internal RecordingEntry Entry { get; private set; }

    /// <summary>Opens the editor for a recording, or brings its already open editor to the front.</summary>
    internal static RecordingEditorWindow Open(RecordingEntry entry, RecordingEditorContext context)
    {
        RecordingEditorWindow? existing = OpenEditors.FirstOrDefault(window => string.Equals(window.Entry.MoviePath, entry.MoviePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            BringToFront(existing);
            return existing;
        }
        var editor = new RecordingEditorWindow(entry, context);
        OpenEditors.Add(editor);
        editor.Show();
        BringToFront(editor);
        return editor;
    }

    /// <summary>
    /// Opened from a floating thumbnail (which never takes focus), Show() alone left the editor behind the app the user
    /// was in. Activate it, and if Windows still keeps another app in front, lift it above everything once.
    /// </summary>
    private static void BringToFront(Window window)
    {
        window.Activate();
        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || GetForegroundWindow() == handle) return;
        window.Topmost = true;
        window.Topmost = false;
        SetForegroundWindow(handle);
        window.Activate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

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

    /// <summary>Dragging a yellow trim handle moves the in or out point (one undo step per drag).</summary>
    public void TimelineMoveTrimHandle(bool inHandle, double time)
    {
        if (_playing) Pause();
        SetTrimPoint(inHandle, time, inHandle ? "trim-in" : "trim-out");
        Seek(inHandle ? State.Timeline.EffectiveIn : Math.Max(0, State.Timeline.EffectiveOut - FrameDuration));
    }

    // ---------- Layout ----------

    /// <summary>Header; player with the Trim/Export panel beside it (or under it when narrow); the grab handle; the timeline.</summary>
    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { MinHeight = 200 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(_timelineRow);
        root.Children.Add(BuildHeader());

        UIElement player = BuildPlayer();
        _inspectorPanel = BuildInspector();
        _contentGrid.Children.Add(player);
        _contentGrid.Children.Add(_inspectorPanel);
        _contentGrid.Margin = new Thickness(12, 0, 12, 0);
        Grid.SetRow(_contentGrid, 1);
        root.Children.Add(_contentGrid);
        UpdateResponsiveLayout();

        UIElement grip = BuildTimelineGrip();
        Grid.SetRow(grip, 2);
        root.Children.Add(grip);
        UIElement timeline = BuildTimelinePanel();
        if (timeline is FrameworkElement frame) frame.Margin = new Thickness(12, 0, 12, 12);
        Grid.SetRow(timeline, 3);
        root.Children.Add(timeline);
        return root;
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
        UpdateSubtitle();
        LoadAudioTracks();
        LoadLosslessIndex();
        await AttachFilmstripAsync();
        _timeline.Fit();
        OnStateChanged();
        if (_loaded.Warning is string warning) SetStatus(warning, true);
        else if (HasCamera) SetStatus("Use the pointer in the top toolbar to move the camera. The Camera lane controls its shape and visibility.");
        else if (_reader is not null) SetStatus("Space plays. B is split mode, I and O set in and out, Q and W delete to the playhead. Press ? for every shortcut.");
    }

    /// <summary>The filmstrip decodes through its own frame reader so thumbnails never wait behind an export.</summary>
    private async Task AttachFilmstripAsync()
    {
        if (_reader is null || _info is null) { _filmstrip.Attach(null, 0, 0); return; }
        int height = (int)Math.Round(RecordingTimeline.VideoHeight * Math.Max(1, VisualTreeHelper.GetDpi(this).DpiScaleY));
        int width = Math.Max(8, (int)Math.Round(height * VideoAspect));
        try { _filmstrip.Attach(await RecordingFrameReader.OpenAsync(Entry.MoviePath), width, height); }
        catch (Exception) { _filmstrip.Attach(null, 0, 0); } // Thumbnails are cosmetic; the clip still draws without them.
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
            _loaded = _loaded with { View = _view };
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

    private bool SaveEditableProject()
    {
        CommitTextEdit();
        if ((_history.IsDirty || _view != _loaded.View) && !SaveProject()) return false;
        SetStatus("Recording project saved.");
        return true;
    }

    private void Editor_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingConfirmed) return;
        if (_closingFlowActive) { e.Cancel = true; return; }
        _closingFlowActive = true;
        try
        {
            if (_operation is not null)
            {
                if (MessageBox.Show(this, "Stop the current export or tracking run and close the editor?", "BetterShot",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
                _operation.Cancel();
            }
            // Window.Close, Alt+F4 and Done all persist just the JSON. Only an explicit output action renders media.
            e.Cancel = !SaveEditableProject();
        }
        finally { _closingFlowActive = false; }
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- State changes ----------

    /// <summary>Applies an undoable edit; <paramref name="coalesceKey"/> merges a drag into one undo step.</summary>
    private bool Apply(Func<RecordingEditState, RecordingEditState> edit, string? coalesceKey = null) => _history.Apply(edit, coalesceKey);

    private void OnStateChanged()
    {
        if (_selectedMark is Guid id && State.Find(id) is null) _selectedMark = null;
        if (_selectedSegment is int index && index >= State.Timeline.Segments.Count) _selectedSegment = null;
        UpdateTitle();
        RefreshAudioRouting();
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
            case RecordingEditorCommand.Save: SaveEditableProject(); break;
            case RecordingEditorCommand.Escape: EscapePressed(); break;
            case RecordingEditorCommand.ShowShortcuts: ShowShortcutSheet(); break;
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

    private void SetInPoint() => SetTrimPoint(inPoint: true, _playhead);

    private void SetOutPoint() => SetTrimPoint(inPoint: false, _playhead);

    /// <summary>Moves the in or out point, keeping at least a frame between them (a drag passes a coalescing key).</summary>
    private void SetTrimPoint(bool inPoint, double time, string? coalesceKey = null)
    {
        EditTimeline timeline = State.Timeline;
        double gap = Math.Max(FrameDuration, EditTimeline.MinimumSegment);
        double clamped = inPoint ? Math.Clamp(time, 0, timeline.EffectiveOut - gap) : Math.Clamp(time, timeline.EffectiveIn + gap, timeline.Duration);
        bool changed = Apply(state => state with { Timeline = inPoint ? state.Timeline.WithInPoint(clamped) : state.Timeline.WithOutPoint(clamped) }, coalesceKey);
        if (changed && coalesceKey is null) SetStatus($"{(inPoint ? "In" : "Out")} point set at {RecordingTimeText.Tenths(clamped)}.");
    }

    /// <summary>The steppers' arrows: one frame earlier or later.</summary>
    private void StepTrimPoint(bool inPoint, int frames)
    {
        double current = inPoint ? State.Timeline.EffectiveIn : State.Timeline.EffectiveOut;
        SetTrimPoint(inPoint, current + frames * FrameDuration);
        PauseAndSeek(inPoint ? State.Timeline.EffectiveIn : Math.Max(0, State.Timeline.EffectiveOut - FrameDuration));
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
}
