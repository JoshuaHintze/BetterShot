using BetterShot.Controls;
using BetterShot.Editing;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Documents;
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
    private readonly Viewbox _viewbox = new() { Stretch = Stretch.Uniform, Margin = new Thickness(22, 22, 22, 6) };
    private readonly Canvas _editCanvas = new();
    private readonly Run _timeNow = new("0:00.0") { Foreground = Brushes.White };
    private readonly Run _timeTotal = new(" / 0:00.0") { Foreground = MutedText };
    private readonly PillSlider _volumeSlider = new("Volume") { Width = 120, Value = 1 };
    private MarkLayer _markLayer = new();
    private RedactionPreviewLayer? _redactionPreview;
    private Button? _playButton, _speedButton, _loopButton, _volumeButton;
    private bool _playing;
    private bool _loop;
    private double _speed = 1;
    private double _volume = 1;
    private bool _playerMuted;
    private double _playhead;
    private bool _mediaReady;
    private double? _jumpTarget;
    private DateTime _jumpRequestedUtc;

    private double FrameDuration => 1 / Math.Max(1, _info?.FrameRate ?? 30);
    private double DisplayScale => _surface.ActualWidth > 0 && _viewbox.ActualWidth > 0 ? _viewbox.ActualWidth / _surface.ActualWidth : 1;

    /// <summary>The player panel: the video (with markup on top) and the transport row under it.</summary>
    private UIElement BuildPlayer()
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var area = new Grid { ClipToBounds = true };
        _redactionPreview = new RedactionPreviewLayer(_media);
        _surface.Children.Add(new Border { Background = Brushes.Black });
        _surface.Children.Add(_media);
        _surface.Children.Add(_redactionPreview);
        _surface.Children.Add(_markLayer);
        _surface.Children.Add(_editCanvas);
        _viewbox.Child = new Border
        {
            Child = _surface, Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, Opacity = .45, ShadowDepth = 3 }
        };
        area.Children.Add(_viewbox);
        panel.Children.Add(area);
        UIElement transport = BuildTransport();
        Grid.SetRow(transport, 1);
        panel.Children.Add(transport);
        _markLayer.MouseLeftButtonDown += Surface_MouseDown;
        _markLayer.MouseMove += Surface_MouseMove;
        _markLayer.MouseLeftButtonUp += Surface_MouseUp;
        _markLayer.LostMouseCapture += (_, _) => { if (_drawStart is not null && !_markLayer.IsMouseCaptured) CancelDraw(); };
        _markLayer.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) EditSelectedText(); };
        _media.MediaOpened += Media_Opened;
        _media.MediaFailed += (_, e) => SetStatus($"Windows could not play this recording: {e.ErrorException?.Message}", true);
        _media.MediaEnded += (_, _) => { if (_playing) Pause(); };
        return Panel(panel, new Thickness(0));
    }

    /// <summary>Time on the left; first/back/play/forward/last in the middle; loop, speed, and volume on the right.</summary>
    private UIElement BuildTransport()
    {
        var bar = new Grid { Margin = new Thickness(22, 6, 22, 16), Height = 56 };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        var time = new TextBlock { FontFamily = MonoFont, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        time.Inlines.Add(_timeNow);
        time.Inlines.Add(_timeTotal);
        bar.Children.Add(time);

        var center = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(FlatIconButton(LucideIconKind.SkipBack, "First frame (Home)", (_, _) => Execute(RecordingEditorCommand.FirstFrame), 40, 18));
        center.Children.Add(FlatIconButton(LucideIconKind.ChevronLeft, "Back one frame (Left or ,)", (_, _) => StepFrames(-1), 40, 20));
        _playButton = IconButton(LucideIconKind.Play, "Play or pause (Space)", (_, _) => TogglePlay(), 52, 52, 22);
        _playButton.Background = Brushes.White;
        _playButton.Foreground = Brushes.Black;
        _playButton.Margin = new Thickness(12, 0, 12, 0);
        _playButton.Template = CircleTemplate();
        center.Children.Add(_playButton);
        center.Children.Add(FlatIconButton(LucideIconKind.ChevronRight, "Forward one frame (Right or .)", (_, _) => StepFrames(1), 40, 20));
        center.Children.Add(FlatIconButton(LucideIconKind.SkipForward, "Last frame (End)", (_, _) => Execute(RecordingEditorCommand.LastFrame), 40, 18));
        Grid.SetColumn(center, 1);
        bar.Children.Add(center);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _loopButton = FlatIconButton(LucideIconKind.Repeat, "Loop the selection (L)", (_, _) => ToggleLoop(), 38, 19);
        right.Children.Add(_loopButton);
        _speedButton = TextButton("1x", "Playback speed", (sender, _) => OpenSpeedMenu((FrameworkElement)sender), LucideIconKind.ChevronDown, 34);
        _speedButton.Background = SurfaceBrush;
        _speedButton.Margin = new Thickness(8, 0, 8, 0);
        if (_speedButton.Content is StackPanel speedRow && speedRow.Children[0] is FrameworkElement chevron)
        {
            speedRow.Children.RemoveAt(0); // "1x ⌄": the chevron goes after the speed.
            chevron.Margin = new Thickness(6, 0, 0, 0);
            chevron.Width = chevron.Height = 13;
            speedRow.Children.Add(chevron);
        }
        right.Children.Add(_speedButton);
        _volumeButton = FlatIconButton(LucideIconKind.Volume2, "Mute the player", (_, _) => TogglePlayerMute(), 34, 18);
        right.Children.Add(_volumeButton);
        _volumeSlider.ValueChanged += (_, _) => SetVolume(_volumeSlider.Value);
        _volumeSlider.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(_volumeSlider);
        Grid.SetColumn(right, 2);
        bar.Children.Add(right);
        return bar;
    }

    /// <summary>The big round white play button (the icon is nudged right so the triangle looks centered).</summary>
    private static ControlTemplate CircleTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var circle = new FrameworkElementFactory(typeof(Border), "chrome");
        circle.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        circle.SetValue(Border.CornerRadiusProperty, new CornerRadius(26));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 0, 0));
        circle.AppendChild(content);
        template.VisualTree = circle;
        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(UIElement.OpacityProperty, .9));
        template.Triggers.Add(over);
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .75));
        template.Triggers.Add(pressed);
        return template;
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
        ApplyPlayerAudio();
        if (_info is { Width: <= 0 } && _media.NaturalVideoWidth > 0) ConfigureSurface(_media.NaturalVideoWidth, _media.NaturalVideoHeight);
        Seek(State.Timeline.FirstPlayable);
    }

    /// <summary>Releases the movie file (the player keeps it open) before the original is replaced or the window closes.</summary>
    private void ShutDownPlayer()
    {
        System.Windows.Media.CompositionTarget.Rendering -= Playback_Rendering;
        _playing = false;
        StopTrackPreview();
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
        ApplyPlayerAudio();
        _media.SpeedRatio = _speed;
        _media.Play();
        StartTrackPreview(start);
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
        StopTrackPreview();
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
        else
        {
            _playhead = position;
            KeepTrackPreviewInStep(position);
        }
        UpdatePlayheadUi(follow: true);
    }

    private void JumpWhilePlaying(double target)
    {
        _media.Position = TimeSpan.FromSeconds(target);
        _trackPreview?.Seek(target);
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
        _timeNow.Text = RecordingTimeText.Tenths(_playhead);
        _timeTotal.Text = " / " + RecordingTimeText.Tenths(State.Timeline.Duration);
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
        _trackPreview?.SetRate(speed);
        UpdateToolbarState();
    }

    private void SetVolume(double volume)
    {
        _volume = Math.Clamp(volume, 0, 1);
        if (_volume > 0) _playerMuted = false;
        ApplyPlayerAudio();
        UpdateToolbarState();
    }

    private void TogglePlayerMute()
    {
        _playerMuted = !_playerMuted;
        if (!_playerMuted && _volume <= 0.01) { _volume = 1; _volumeSlider.Value = 1; }
        ApplyPlayerAudio();
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
        if (_playButton?.Content is LucideIcon play)
        {
            play.Icon = _playing ? LucideIconKind.Pause : LucideIconKind.Play;
            play.Margin = new Thickness(_playing ? -3 : 0, 0, 0, 0);
        }
        if (_speedButton?.Content is StackPanel speedRow && speedRow.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock speedLabel) speedLabel.Text = SpeedLabel(_speed);
        if (_loopButton is not null)
        {
            SetLit(_loopButton, _loop);
            _loopButton.ToolTip = _loop ? "Looping the selection (L)" : "Loop the selection (L)";
        }
        if (_volumeButton?.Content is LucideIcon speaker)
        {
            bool silent = _playerMuted || _volume <= 0.01;
            speaker.Icon = silent ? LucideIconKind.VolumeX : LucideIconKind.Volume2;
            _volumeButton.ToolTip = silent ? "Unmute the player" : "Mute the player";
        }
        UpdateTimelineToolbar();
        UpdateHeaderState();
        UpdateInspector();
        UpdateMarkupToolbar();
    }
}
