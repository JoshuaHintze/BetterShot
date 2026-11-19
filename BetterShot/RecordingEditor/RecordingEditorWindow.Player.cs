using BetterShot.Controls;
using BetterShot.Editing;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using static BetterShot.RecordingEditor.RecordingEditorChrome;

namespace BetterShot.RecordingEditor;

/// <summary>
/// Playback: the source movie plays in a WPF MediaElement; the editor skips cut stretches as it plays (exports are
/// cut exactly). Marks are drawn on top in video pixels, inside a Viewbox that fits the frame to the window.
/// </summary>
internal sealed partial class RecordingEditorWindow
{
    private static readonly double[] Speeds = [0.5, 1, 1.5, 2];
    private readonly MediaElement _media = new()
    {
        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Fill
    };
    private readonly Grid _surface = new() { ClipToBounds = true };
    private readonly Viewbox _viewbox = new() { Stretch = Stretch.Uniform, Margin = new Thickness(28) };
    private readonly Canvas _editCanvas = new();
    private readonly TextBlock _timeText = new() { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
    private MarkLayer _markLayer = new();
    private RedactionPreviewLayer? _redactionPreview;
    private Button? _playButton, _speedButton, _loopButton, _splitModeButton, _snapButton, _rowsButton, _putBackButton, _followButton;
    private bool _playing;
    private bool _loop;
    private double _speed = 1;
    private double _playhead;
    private bool _mediaReady;
    private double? _jumpTarget;
    private DateTime _jumpRequestedUtc;

    private double FrameDuration => 1 / Math.Max(1, _info?.FrameRate ?? 30);
    private double DisplayScale => _surface.ActualWidth > 0 && _viewbox.ActualWidth > 0 ? _viewbox.ActualWidth / _surface.ActualWidth : 1;

    private UIElement BuildPlayer()
    {
        var area = new Grid { Background = Brush("#111213"), ClipToBounds = true };
        _redactionPreview = new RedactionPreviewLayer(_media);
        _surface.Children.Add(new Border { Background = Brushes.Black });
        _surface.Children.Add(_media);
        _surface.Children.Add(_redactionPreview);
        _surface.Children.Add(_markLayer);
        _surface.Children.Add(_editCanvas);
        _viewbox.Child = new Border
        {
            BorderBrush = Brush("#4B4D50"), BorderThickness = new Thickness(1), Child = _surface,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 28, Opacity = .55, ShadowDepth = 5 }
        };
        area.Children.Add(_viewbox);
        _markLayer.MouseLeftButtonDown += Surface_MouseDown;
        _markLayer.MouseMove += Surface_MouseMove;
        _markLayer.MouseLeftButtonUp += Surface_MouseUp;
        _markLayer.LostMouseCapture += (_, _) => { if (_drawStart is not null && !_markLayer.IsMouseCaptured) CancelDraw(); };
        _markLayer.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) EditSelectedText(); };
        _media.MediaOpened += Media_Opened;
        _media.MediaFailed += (_, e) => SetStatus($"Windows could not play this recording: {e.ErrorException?.Message}", true);
        _media.MediaEnded += (_, _) => { if (_playing) Pause(); };
        return area;
    }

    private UIElement BuildTransport()
    {
        var bar = new Grid { Margin = new Thickness(12, 7, 12, 7) };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(IconButton(LucideIconKind.SkipBack, "First frame (Home)", (_, _) => Execute(RecordingEditorCommand.FirstFrame), 34, 34, 16));
        left.Children.Add(IconButton(LucideIconKind.ChevronLeft, "Back one frame (Left or ,)", (_, _) => StepFrames(-1), 34, 34, 16));
        _playButton = IconButton(LucideIconKind.Play, "Play or pause (Space)", (_, _) => TogglePlay(), 42, 34, 18);
        left.Children.Add(_playButton);
        left.Children.Add(IconButton(LucideIconKind.ChevronRight, "Forward one frame (Right or .)", (_, _) => StepFrames(1), 34, 34, 16));
        left.Children.Add(IconButton(LucideIconKind.SkipForward, "Last frame (End)", (_, _) => Execute(RecordingEditorCommand.LastFrame), 34, 34, 16));
        left.Children.Add(Separator());
        left.Children.Add(IconButton(LucideIconKind.ArrowLeftToLine, "Jump to the in point (Shift+I)", (_, _) => Execute(RecordingEditorCommand.JumpToIn), 34, 34, 16));
        left.Children.Add(TextButton("I", "Set the in point at the playhead (I)", (_, _) => SetInPoint(), null, 34));
        left.Children.Add(TextButton("O", "Set the out point at the playhead (O)", (_, _) => SetOutPoint(), null, 34));
        left.Children.Add(IconButton(LucideIconKind.ArrowRightToLine, "Jump to the out point (Shift+O)", (_, _) => Execute(RecordingEditorCommand.JumpToOut), 34, 34, 16));
        left.Children.Add(_timeText);
        bar.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _speedButton = TextButton("1x", "Playback speed", (sender, _) => OpenSpeedMenu((FrameworkElement)sender), null, 34);
        right.Children.Add(_speedButton);
        _loopButton = IconButton(LucideIconKind.Repeat, "Loop the selection (L)", (_, _) => ToggleLoop(), 34, 34, 16);
        right.Children.Add(_loopButton);
        right.Children.Add(Separator());
        _splitModeButton = IconButton(LucideIconKind.Scissors, "Split mode: clicks cut (B)", (_, _) => ToggleSplitMode(), 34, 34, 16);
        right.Children.Add(_splitModeButton);
        right.Children.Add(IconButton(LucideIconKind.SquareSplitHorizontal, "Split at the playhead (Ctrl+B)", (_, _) => TimelineSplit(_playhead), 34, 34, 16));
        right.Children.Add(IconButton(LucideIconKind.Trash2, "Delete the selected clip or mark (Delete). Q deletes from the clip start to the playhead, W from the playhead to the clip end.", (_, _) => DeleteSelection(), 34, 34, 16));
        _putBackButton = IconButton(LucideIconKind.Undo2, "Put the selected cut back", (_, _) => PutSelectedCutBack(), 34, 34, 16);
        right.Children.Add(_putBackButton);
        right.Children.Add(IconButton(LucideIconKind.RotateCcw, "Put the recording back the way it was", (_, _) => PutRecordingBack(), 34, 34, 16));
        _snapButton = IconButton(LucideIconKind.Magnet, "Snapping (N)", (_, _) => ToggleSnapping(), 34, 34, 16);
        right.Children.Add(_snapButton);
        right.Children.Add(Separator());
        _followButton = IconButton(LucideIconKind.Crosshair, "Follow: moves the selected redaction with what it covers as the recording plays", (sender, _) => OpenFollowMenu((FrameworkElement)sender), 34, 34, 16);
        right.Children.Add(_followButton);
        _rowsButton = IconButton(LucideIconKind.Rows3, "Show every mark on its own row", (_, _) => ToggleMarkRows(), 34, 34, 16);
        right.Children.Add(_rowsButton);
        Grid.SetColumn(right, 1);
        bar.Children.Add(right);
        return new Border { Background = Brush("#1B1C1D"), BorderBrush = Brush("#2A2B2D"), BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };
    }

    private void ConfigureSurface(int width, int height)
    {
        int safeWidth = Math.Max(2, width), safeHeight = Math.Max(2, height);
        _surface.Width = safeWidth;
        _surface.Height = safeHeight;
        _media.Width = safeWidth;
        _media.Height = safeHeight;
        _editCanvas.Width = safeWidth;
        _editCanvas.Height = safeHeight;
        if (_redactionPreview is not null) { _redactionPreview.Width = safeWidth; _redactionPreview.Height = safeHeight; }
        _markLayer.Configure(safeWidth, safeHeight);
    }

    private void OpenPlayer()
    {
        try
        {
            _media.Source = new Uri(Entry.MoviePath, UriKind.Absolute);
            _media.Pause(); // Opens the movie and shows the first frame without playing.
        }
        catch (Exception error) { SetStatus($"Windows could not open this recording for playback: {error.Message}", true); }
    }

    private void Media_Opened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;
        if (_info is { Width: <= 0 } && _media.NaturalVideoWidth > 0) ConfigureSurface(_media.NaturalVideoWidth, _media.NaturalVideoHeight);
        Seek(State.Timeline.FirstPlayable);
    }

    /// <summary>Releases the movie file (the player keeps it open) before the original is replaced or the window closes.</summary>
    private void ShutDownPlayer()
    {
        System.Windows.Media.CompositionTarget.Rendering -= Playback_Rendering;
        _playing = false;
        try
        {
            _media.Stop();
            _media.Close();
            _media.Source = null;
        }
        catch (InvalidOperationException) { } // Already closed.
        _mediaReady = false;
    }

    private void TogglePlay()
    {
        if (_playing) Pause();
        else Play();
    }

    private void Play()
    {
        if (!_mediaReady) return;
        CommitTextEdit();
        EditTimeline timeline = State.Timeline;
        if (timeline.KeptRanges.Count == 0) { SetStatus("Everything is cut, so there is nothing to play. Put a cut back first.", true); return; }
        double start = timeline.NextPlayable(_playhead) is double next && _playhead < timeline.LastPlayable - FrameDuration ? next : timeline.FirstPlayable;
        Seek(start);
        _media.SpeedRatio = _speed;
        _media.Play();
        _playing = true;
        System.Windows.Media.CompositionTarget.Rendering += Playback_Rendering;
        UpdateToolbarState();
    }

    private void Pause()
    {
        if (!_playing) return;
        System.Windows.Media.CompositionTarget.Rendering -= Playback_Rendering;
        _playing = false;
        _media.Pause();
        Seek(_media.Position.TotalSeconds);
        UpdateToolbarState();
    }

    private void PauseAndSeek(double time)
    {
        Pause();
        Seek(time);
    }

    /// <summary>While playing: skips cut stretches, stops (or loops) at the end of the selection, and moves the playhead.</summary>
    private void Playback_Rendering(object? sender, EventArgs e)
    {
        if (!_playing) return;
        double position = _media.Position.TotalSeconds;
        if (_jumpTarget is double target)
        {
            // The player reports the old position for a moment after a jump; do not jump again meanwhile.
            if (position < target - 0.05 && DateTime.UtcNow - _jumpRequestedUtc < TimeSpan.FromMilliseconds(400)) return;
            _jumpTarget = null;
        }
        EditTimeline timeline = State.Timeline;
        if (timeline.NextPlayable(position) is not double next || position >= timeline.LastPlayable - FrameDuration / 2)
        {
            if (_loop) JumpWhilePlaying(timeline.FirstPlayable);
            else
            {
                Pause();
                Seek(Math.Max(0, timeline.LastPlayable - FrameDuration));
                return;
            }
        }
        else if (next > position + 0.02) JumpWhilePlaying(next);
        else _playhead = position;
        UpdatePlayheadUi(follow: true);
    }

    private void JumpWhilePlaying(double target)
    {
        _media.Position = TimeSpan.FromSeconds(target);
        _playhead = target;
        _jumpTarget = target;
        _jumpRequestedUtc = DateTime.UtcNow;
    }

    private void Seek(double time)
    {
        double duration = State.Timeline.Duration;
        _playhead = Math.Clamp(double.IsFinite(time) ? time : 0, 0, Math.Max(0, duration - FrameDuration / 2));
        if (_mediaReady) _media.Position = TimeSpan.FromSeconds(_playhead);
        UpdatePlayheadUi(follow: false);
    }

    private void StepFrames(int frames)
    {
        Pause();
        Seek(_playhead + frames * FrameDuration);
    }

    private void UpdatePlayheadUi(bool follow)
    {
        EditTimeline timeline = State.Timeline;
        string output = timeline.SourceToOutput(_playhead) is double kept ? FormatTime(kept) : "cut";
        _timeText.Text = $"{FormatTime(_playhead)} / {FormatTime(timeline.Duration)}   ·   edited {output} of {FormatTime(timeline.OutputDuration)}";
        if (follow) _timeline.FollowPlayhead();
        else _timeline.InvalidateVisual();
        RefreshOverlay();
    }

    private void ToggleLoop()
    {
        _loop = !_loop;
        SetStatus(_loop ? "Looping the selection." : "Looping off.");
        UpdateToolbarState();
    }

    private void OpenSpeedMenu(FrameworkElement target)
    {
        ContextMenu menu = Menu();
        foreach (double speed in Speeds)
        {
            double chosen = speed;
            menu.Items.Add(MenuItem(SpeedLabel(speed), (_, _) => SetSpeed(chosen), isChecked: Math.Abs(_speed - speed) < 1e-9));
        }
        OpenBelow(menu, target);
    }

    private void SetSpeed(double speed)
    {
        _speed = speed;
        if (_playing) _media.SpeedRatio = speed;
        UpdateToolbarState();
    }

    private static string SpeedLabel(double speed) => speed.ToString("0.##", CultureInfo.InvariantCulture) + "x";

    private void ToggleMarkRows()
    {
        _view = _view with { MarksOnOwnRows = !_view.MarksOnOwnRows };
        SetStatus(_view.MarksOnOwnRows ? "Every mark is on its own row." : "The marks are folded into one row.");
        UpdateToolbarState();
        _timeline.InvalidateVisual();
    }

    /// <summary>Redraws marks at the playhead, live redaction previews, and the lost-field blur.</summary>
    private void RefreshOverlay()
    {
        if (_info is null) return;
        double time = _playhead;
        List<VideoMark> visible = State.VisibleMarksAt(time).ToList();
        var shapes = new List<Annotation>();
        var pixelRedactions = new List<RectangleAnnotation>();
        foreach (VideoMark mark in visible)
        {
            if (_editingText is not null && mark.Id == _editingMarkId) continue;
            Annotation shape = _dragPreview is not null && mark.Id == _selectedMark ? _dragPreview : mark.ShapeAt(time);
            if (mark.NeedsSourcePixels && shape is RectangleAnnotation redaction) pixelRedactions.Add(redaction);
            else shapes.Add(shape);
        }
        _redactionPreview?.Show(pixelRedactions);
        bool blurWhole = State.BlursWholePictureAt(time);
        if (blurWhole && _media.Effect is null) _media.Effect = new BlurEffect { Radius = 40, KernelType = KernelType.Gaussian };
        else if (!blurWhole && _media.Effect is not null) _media.Effect = null;
        (Rect? selection, IReadOnlyList<Point> handles) = SelectionGeometry(visible, time);
        _markLayer.Show(shapes, selection, handles, _drawPreview, DisplayScale);
    }

    private void UpdateToolbarState()
    {
        if (_playButton?.Content is LucideIcon play) play.Icon = _playing ? LucideIconKind.Pause : LucideIconKind.Play;
        if (_speedButton?.Content is StackPanel speedRow && speedRow.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock speedLabel) speedLabel.Text = SpeedLabel(_speed);
        if (_loopButton is not null) SetActive(_loopButton, _loop);
        if (_splitModeButton is not null) SetActive(_splitModeButton, _splitMode);
        if (_snapButton is not null) SetActive(_snapButton, _snapping);
        if (_rowsButton is not null)
        {
            SetActive(_rowsButton, _view.MarksOnOwnRows);
            _rowsButton.ToolTip = _view.MarksOnOwnRows ? "Fold the marks into one row" : "Show every mark on its own row";
        }
        if (_putBackButton is not null)
            _putBackButton.IsEnabled = _selectedSegment is int index && index < State.Timeline.Segments.Count && !State.Timeline.Segments[index].Kept;
        if (_followButton is not null) _followButton.IsEnabled = SelectedMark is { IsRedaction: true } && _reader is not null;
        UpdateMarkupToolbar();
    }
}
