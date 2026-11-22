using BetterShot.Controls;
using BetterShot.Persistence;
using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using static BetterShot.RecordingEditor.RecordingEditorChrome;

namespace BetterShot.RecordingEditor;

/// <summary>
/// The Trim and Export panel (KhingShot layout). Trim: a Lossless/Re-encodes badge, In and Out steppers (one frame per
/// arrow; click the time to type one), how much is kept, "Replace the original", and Trim. Export: MP4, GIF, and
/// Timelapse cards, a line saying what the chosen one makes, a size estimate, and the export button.
/// On narrow windows the panel moves under the player with Trim and Export side by side.
/// </summary>
internal sealed partial class RecordingEditorWindow
{
    private const double InspectorWidth = 328, NarrowWindowWidth = 940;
    private static readonly Brush LosslessGreen = Brush("#4ADE80");
    private readonly Grid _inspectorGrid = new();
    private readonly StackPanel _trimSection = new();
    private readonly Grid _exportSection = new();
    private readonly Border _badge = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly System.Windows.Documents.Run _keepingValue = new() { FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
    private readonly System.Windows.Documents.Run _keepingTotal = new() { Foreground = MutedText };
    private readonly Border _keepingBar = new() { Height = 4, CornerRadius = new CornerRadius(2), Background = ActiveBrush, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _snapNote = new() { FontSize = 12, Foreground = MutedText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ToggleSwitch _replaceSwitch = new("Replace the original");
    private readonly TextBlock _exportDescription = new() { FontSize = 14, Foreground = Brush("#C9CCD2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _estimate = new() { FontSize = 14, Foreground = MutedText, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _mp4Options = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _mp4Dimensions = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, Margin = new Thickness(8, 8, 0, 0), Foreground = MutedText };
    private readonly ComboBox _mp4Preset = new() { Height = 28, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Dictionary<RecordingExportKind, Button> _exportCards = [];
    private TimeStepper? _inStepper, _outStepper;
    private Button? _trimButton, _exportButton;
    private RecordingExportKind _exportKind = RecordingExportKind.Mp4;
    private int _timelapseSpeed = 4;
    private bool _narrowLayout;
    private Mp4MovieIndex? _losslessIndex;
    private string? _losslessUnavailable;

    private UIElement BuildInspector()
    {
        BuildTrimSection();
        BuildExportSection();
        _inspectorGrid.Children.Add(_trimSection);
        _inspectorGrid.Children.Add(_exportSection);
        ArrangeInspector(narrow: false);
        // Scrolls only when the window is too short; otherwise it fills the panel so the export button sits at the bottom.
        var scroller = new ScrollViewer { Content = _inspectorGrid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        scroller.SizeChanged += (_, _) => _inspectorGrid.MinHeight = _narrowLayout ? 0 : scroller.ActualHeight;
        return Panel(scroller, new Thickness(14, 11, 14, 11));
    }

    /// <summary>Wide: Trim above Export. Narrow: side by side under the player.</summary>
    private void ArrangeInspector(bool narrow)
    {
        _inspectorGrid.RowDefinitions.Clear();
        _inspectorGrid.ColumnDefinitions.Clear();
        if (narrow)
        {
            _inspectorGrid.ColumnDefinitions.Add(new ColumnDefinition());
            _inspectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            _inspectorGrid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetRow(_trimSection, 0);
            Grid.SetRow(_exportSection, 0);
            Grid.SetColumn(_exportSection, 2);
            _trimSection.Margin = new Thickness(0);
            _inspectorGrid.MinHeight = 0;
        }
        else
        {
            _inspectorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _inspectorGrid.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(_exportSection, 1);
            Grid.SetColumn(_exportSection, 0);
            _trimSection.Margin = new Thickness(0, 0, 0, 10);
        }
    }

    private void BuildTrimSection()
    {
        var heading = new Grid();
        heading.Children.Add(Label("Trim", 16, Brushes.White, FontWeights.SemiBold));
        heading.Children.Add(_badge);
        _trimSection.Children.Add(heading);

        var steppers = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        steppers.ColumnDefinitions.Add(new ColumnDefinition());
        steppers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        steppers.ColumnDefinitions.Add(new ColumnDefinition());
        _inStepper = new TimeStepper("In", this, inPoint: true);
        _outStepper = new TimeStepper("Out", this, inPoint: false);
        steppers.Children.Add(_inStepper.Root);
        Grid.SetColumn(_outStepper.Root, 2);
        steppers.Children.Add(_outStepper.Root);
        _trimSection.Children.Add(steppers);

        var keeping = new Grid { Margin = new Thickness(0, 8, 0, 5) };
        keeping.Children.Add(Label("Keeping", 15, Brush("#C9CCD2"), FontWeights.Normal));
        var amount = new TextBlock { FontSize = 15, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        amount.Inlines.Add(_keepingValue);
        amount.Inlines.Add(_keepingTotal);
        keeping.Children.Add(amount);
        _trimSection.Children.Add(keeping);
        var track = new Grid { Height = 4 };
        track.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = SurfaceBorderBrush });
        track.Children.Add(_keepingBar);
        track.SizeChanged += (_, _) => UpdateInspector();
        _trimSection.Children.Add(track);
        _trimSection.Children.Add(_snapNote);

        var replace = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        replace.ColumnDefinitions.Add(new ColumnDefinition());
        replace.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        replace.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock replaceLabel = Label("Replace the original", 15, Brushes.White, FontWeights.Normal);
        replaceLabel.ToolTip = "Off keeps the original and saves the trimmed copy beside it";
        replace.Children.Add(replaceLabel);
        _replaceSwitch.ToolTip = "Off keeps the original and saves the trimmed copy beside it";
        _replaceSwitch.VerticalAlignment = VerticalAlignment.Center;
        _replaceSwitch.Toggled += (_, _) => SetReplaceOriginal(_replaceSwitch.IsOn);
        Grid.SetColumn(_replaceSwitch, 1);
        replace.Children.Add(_replaceSwitch);
        _trimButton = TextButton("Trim", "Keep only what is highlighted", async (_, _) => await ApplyTrimAsync(), LucideIconKind.Scissors, 32);
        _trimButton.Background = SurfaceBrush;
        _trimButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_trimButton, 2);
        replace.Children.Add(_trimButton);
        _trimSection.Children.Add(replace);
    }

    private void BuildExportSection()
    {
        _exportSection.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _exportSection.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _exportSection.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _exportSection.RowDefinitions.Add(new RowDefinition());
        _exportSection.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var rule = new Border { Height = 1, Background = PanelBorderBrush, Margin = new Thickness(0, 0, 0, 8) };
        _exportSection.Children.Add(rule);
        TextBlock heading = Label("Export", 16, Brushes.White, FontWeights.SemiBold);
        Grid.SetRow(heading, 1);
        _exportSection.Children.Add(heading);
        var cards = new UniformGrid { Columns = 3, Margin = new Thickness(-3, 7, -3, 0) };
        cards.Children.Add(ExportCard(RecordingExportKind.Mp4, LucideIconKind.Film, "MP4"));
        cards.Children.Add(ExportCard(RecordingExportKind.Gif, LucideIconKind.Images, "GIF"));
        cards.Children.Add(ExportCard(RecordingExportKind.Timelapse, LucideIconKind.Clock, "Timelapse"));
        var details = new StackPanel();
        details.Children.Add(cards);
        _mp4Preset.SetResourceReference(StyleProperty, "DarkComboBox");
        foreach ((Mp4ExportPreset preset, string label) in new[]
        {
            (Mp4ExportPreset.Original, "Original resolution"), (Mp4ExportPreset.P1080, "Full HD · 1080p"),
            (Mp4ExportPreset.P720, "HD · 720p"), (Mp4ExportPreset.P480, "Small · 480p")
        })
            _mp4Preset.Items.Add(new ComboBoxItem { Content = label, Tag = preset });
        Mp4ExportPreset savedPreset = _context.Settings().Mp4ExportPreset;
        _mp4Preset.SelectedItem = _mp4Preset.Items.Cast<ComboBoxItem>().First(item => Equals(item.Tag, savedPreset));
        _mp4Preset.SelectionChanged += (_, _) => SetMp4Preset(SelectedMp4Preset);
        System.Windows.Automation.AutomationProperties.SetName(_mp4Preset, "MP4 export quality");
        _mp4Options.Children.Add(_mp4Preset);
        _mp4Options.Children.Add(_mp4Dimensions);
        details.Children.Add(_mp4Options);
        details.Children.Add(_exportDescription);
        Grid.SetRow(details, 2);
        _exportSection.Children.Add(details);

        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_estimate);
        _exportButton = TextButton("Export MP4", "Export the kept stretch", async (_, _) => await ExportSelectedAsync(), LucideIconKind.Share, 34);
        _exportButton.Background = Brushes.Transparent;
        _exportButton.BorderBrush = SurfaceBorderBrush;
        _exportButton.FontSize = 13;
        _exportButton.Padding = new Thickness(10, 3, 11, 3);
        _exportButton.Template = OutlinedTemplate();
        _exportButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(_exportButton, 1);
        footer.Children.Add(_exportButton);
        var footerFrame = new Border { BorderBrush = PanelBorderBrush, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0), Child = footer };
        Grid.SetRow(footerFrame, 4);
        _exportSection.Children.Add(footerFrame);
    }

    private Mp4ExportPreset SelectedMp4Preset =>
        (_mp4Preset.SelectedItem as ComboBoxItem)?.Tag is Mp4ExportPreset preset ? preset : Mp4ExportPreset.Original;

    private void SetMp4Preset(Mp4ExportPreset preset)
    {
        if (_context.UpdateSettings is { } update)
        {
            try { update(settings => settings with { Mp4ExportPreset = preset }); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SetStatus($"The export choice could not be saved: {error.Message}", true); }
        }
        UpdateExportCards();
    }

    private Button ExportCard(RecordingExportKind kind, LucideIconKind icon, string title)
    {
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(new LucideIcon { Icon = icon, Width = 13, Height = 13, Foreground = Brushes.White, Margin = new Thickness(0, 0, 3, 0) });
        heading.Children.Add(Label(title, 12, Brushes.White, FontWeights.SemiBold));
        var detail = new TextBlock { FontSize = 11.5, Foreground = MutedText, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var content = new StackPanel { Margin = new Thickness(5, 7, 4, 7), HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(heading);
        content.Children.Add(detail);
        var card = new Button
        {
            Content = content, Margin = new Thickness(4, 0, 4, 0), Background = SurfaceBrush, BorderBrush = Brushes.Transparent, Foreground = Brushes.White,
            HorizontalContentAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, Template = OutlinedTemplate(), Tag = detail, Padding = new Thickness(0)
        };
        System.Windows.Automation.AutomationProperties.SetName(card, title);
        card.Click += (sender, _) =>
        {
            if (_exportKind == kind && kind == RecordingExportKind.Timelapse) OpenTimelapseMenu((FrameworkElement)sender);
            _exportKind = kind;
            UpdateInspector();
        };
        if (kind == RecordingExportKind.Timelapse) card.MouseRightButtonUp += (sender, e) => { e.Handled = true; OpenTimelapseMenu((FrameworkElement)sender); };
        _exportCards[kind] = card;
        return card;
    }

    private void OpenTimelapseMenu(FrameworkElement target)
    {
        ContextMenu menu = Menu();
        menu.Items.Add(MenuHeader("Timelapse speed"));
        foreach (int speed in ExportPlanning.TimelapseSpeeds)
        {
            int chosen = speed;
            menu.Items.Add(MenuItem($"{speed}×", (_, _) => { _timelapseSpeed = chosen; UpdateInspector(); }, isChecked: _timelapseSpeed == speed));
        }
        OpenBelow(menu, target);
    }

    /// <summary>A rounded button with a 1.5 px border (the export cards and the export button).</summary>
    private static ControlTemplate OutlinedTemplate()
    {
        ControlTemplate template = RoundedTemplate();
        if (template.VisualTree is FrameworkElementFactory chrome)
        {
            chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            if (chrome.FirstChild?.FirstChild?.NextSibling is FrameworkElementFactory presenter)
                presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        }
        return template;
    }

    private void SetReplaceOriginal(bool replace)
    {
        if (_context.UpdateSettings is { } update)
        {
            try { update(settings => settings with { TrimReplacesOriginal = replace }); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SetStatus($"The choice could not be saved: {error.Message}", true); }
        }
        _replaceOriginal = replace;
        SetStatus(replace ? "Trim replaces the original recording." : "Trim keeps the original and saves the trimmed copy beside it.");
    }

    private bool _replaceOriginal;

    // ---------- State ----------

    /// <summary>Reads the movie's keyframes so the badge can say whether Trim copies the video without re-encoding.</summary>
    private void LoadLosslessIndex()
    {
        _losslessIndex = null;
        _losslessUnavailable = null;
        string path = Entry.MoviePath;
        _ = Task.Run(() => Mp4MovieIndex.Read(path)).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) _losslessIndex = task.Result;
            else _losslessUnavailable = task.Exception?.GetBaseException() is NotSupportedException unsupported ? unsupported.Message : "The movie's layout could not be read for a lossless trim.";
            UpdateInspector();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The lossless plan for the current cuts, or null with the reason Trim would re-encode.</summary>
    private LosslessTrimPlan? CurrentLosslessPlan(out string reason)
    {
        if (HasCamera)
        {
            reason = "Trimming renders the selected camera appearance into the saved video.";
            return null;
        }
        if (_losslessIndex is null) { reason = _losslessUnavailable ?? "Checking whether the movie can be trimmed without re-encoding…"; return null; }
        LosslessTrimPlan? plan = LosslessTrimPlanner.Plan(_losslessIndex.KeyframeTimes, State.Timeline.KeptRanges, FrameDuration, out string? why);
        reason = why ?? string.Empty;
        return plan;
    }

    private void UpdateInspector()
    {
        if (_inStepper is null || _outStepper is null) return;
        EditTimeline timeline = State.Timeline;
        _inStepper.Show(timeline.EffectiveIn);
        _outStepper.Show(timeline.EffectiveOut);
        _keepingValue.Text = $"{timeline.OutputDuration:0.0} s";
        _keepingTotal.Text = $" of {timeline.Duration:0.0} s";
        if (_keepingBar.Parent is FrameworkElement track)
            _keepingBar.Width = Math.Max(0, track.ActualWidth * Math.Clamp(timeline.OutputDuration / Math.Max(0.001, timeline.Duration), 0, 1));
        UpdateBadge();
        if (_trimButton is not null)
        {
            _trimButton.IsEnabled = timeline.HasCuts && _operation is null;
            _trimButton.ToolTip = timeline.HasCuts ? "Keep only what is highlighted" : "Drag the yellow handles or cut a stretch out first";
        }
        _replaceSwitch.IsOn = _replaceOriginal;
        UpdateExportCards();
    }

    private void UpdateBadge()
    {
        LosslessTrimPlan? plan = CurrentLosslessPlan(out string reason);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (plan is not null)
        {
            row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = LosslessGreen, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Label("Lossless", 13.5, LosslessGreen, FontWeights.SemiBold));
            _badge.Background = Brush("#1C3326");
            _badge.ToolTip = "Trimming copies the video as it is, without re-encoding.";
        }
        else
        {
            row.Children.Add(Label("Re-encodes", 13.5, MutedText, FontWeights.SemiBold));
            _badge.Background = SurfaceBrush;
            _badge.ToolTip = string.IsNullOrEmpty(reason) ? "Trimming re-encodes the kept stretch to cut it exactly." : reason;
        }
        _badge.Child = row;
        bool snaps = plan is not null && plan.InSnapSeconds > FrameDuration / 2;
        _snapNote.Visibility = snaps ? Visibility.Visible : Visibility.Collapsed;
        if (snaps) _snapNote.Text = $"Video can only start on a keyframe, so the trimmed movie starts at {RecordingTimeText.Tenths(plan!.Ranges[0].Start)}, {plan.InSnapSeconds:0.0} s before In.";
    }

    private void UpdateExportCards()
    {
        CaptureSettings settings = _context.Settings();
        double kept = State.Timeline.OutputDuration;
        int gifFps = ExportPlanning.NormalizeGifFrameRate(settings.GifFps), gifSize = ExportPlanning.NormalizeGifMaxDimension(settings.GifMaxDimension);
        int width = _info?.Width ?? Entry.Width, height = _info?.Height ?? Entry.Height;
        (int gifWidth, int gifHeight) = ExportPlanning.FitWithin(width, height, gifSize);
        Mp4ExportPlan mp4Plan = ExportPlanning.PlanMp4(SelectedMp4Preset, width, height, _info?.FrameRate ?? 30);
        _mp4Dimensions.Text = $"{mp4Plan.Width} × {mp4Plan.Height}";
        _mp4Options.Visibility = _exportKind == RecordingExportKind.Mp4 ? Visibility.Visible : Visibility.Collapsed;
        foreach ((RecordingExportKind kind, Button card) in _exportCards)
        {
            bool selected = kind == _exportKind;
            card.BorderBrush = selected ? Brush("#C9CCD2") : Brushes.Transparent;
            card.Background = selected ? Brush("#303135") : SurfaceBrush;
            if (card.Tag is TextBlock detail)
            {
                detail.Text = kind switch
                {
                    RecordingExportKind.Gif => $"{(gifSize == 0 ? "Full size" : Math.Max(gifWidth, gifHeight) + " px")} · {gifFps} fps",
                    RecordingExportKind.Timelapse => $"{_timelapseSpeed}× · {RecordingTimeText.Whole(kept)} → {RecordingTimeText.Whole(kept / _timelapseSpeed)}",
                    _ => "Video"
                };
            }
        }
        AudioOutputPlan audio = CurrentAudioPlan;
        string markup = State.HasVisibleMarks ? " Markup is drawn in; the recording stays as it was." : string.Empty;
        _exportDescription.Text = _exportKind switch
        {
            RecordingExportKind.Gif => "An endlessly looping GIF of the kept stretch, without audio. Size and frame rate are in Settings > Recording." + markup,
            RecordingExportKind.Timelapse => $"The kept stretch {_timelapseSpeed}× faster as an MP4. Audio is left out, so nothing plays back sped up." + markup,
            _ => audio.Mode switch
            {
                AudioOutputMode.Original => "The kept stretch, audio included, as an MP4.",
                AudioOutputMode.Remix => "The kept stretch as an MP4, without the muted audio.",
                _ => "The kept stretch as an MP4, without audio."
            } + markup
        };
        _estimate.Text = _exportKind switch
        {
            RecordingExportKind.Gif => ExportEstimate.About(ExportEstimate.GifBytes(kept, gifFps, gifWidth, gifHeight)),
            RecordingExportKind.Timelapse => ExportEstimate.About(ExportEstimate.TimelapseBytes(kept, _timelapseSpeed, width, height)),
            _ => ExportEstimate.Mp4Range(kept, mp4Plan, audio.Mode != AudioOutputMode.Silent, _info, _sourceFileBytes)
        };
        _estimate.ToolTip = _exportKind == RecordingExportKind.Mp4
            ? "Estimated from the source recording and the selected encoder bitrate. Motion and overlays affect the finished size."
            : null;
        if (_exportButton?.Content is StackPanel buttonRow && buttonRow.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock label)
            label.Text = _exportKind switch { RecordingExportKind.Gif => "Export GIF", RecordingExportKind.Timelapse => "Export timelapse", _ => "Export MP4" };
        if (_exportButton is not null) _exportButton.IsEnabled = _operation is null;
    }

    /// <summary>Moves the panel under the player when the window is too narrow for three columns of content.</summary>
    private void UpdateResponsiveLayout()
    {
        bool narrow = ActualWidth > 0 && ActualWidth < NarrowWindowWidth;
        if (narrow == _narrowLayout && _contentGrid.ColumnDefinitions.Count > 0) return;
        _narrowLayout = narrow;
        _contentGrid.ColumnDefinitions.Clear();
        _contentGrid.RowDefinitions.Clear();
        if (narrow)
        {
            _contentGrid.RowDefinitions.Add(new RowDefinition { MinHeight = 160 });
            _contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            _contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _contentGrid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(_inspectorPanel, 0);
            Grid.SetRow(_inspectorPanel, 2);
        }
        else
        {
            _contentGrid.ColumnDefinitions.Add(new ColumnDefinition());
            _contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            _contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InspectorWidth) });
            Grid.SetColumn(_inspectorPanel, 2);
            Grid.SetRow(_inspectorPanel, 0);
        }
        ArrangeInspector(narrow);
    }

    /// <summary>"‹ 0:00.0 ›": steps the in or out point one frame, or takes a typed time.</summary>
    private sealed class TimeStepper
    {
        private readonly RecordingEditorWindow _owner;
        private readonly bool _inPoint;
        private readonly Button _value;
        private readonly TextBox _editor = new()
        {
            FontFamily = MonoFont, FontSize = 16, Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            CaretBrush = Brushes.White, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed
        };

        internal TimeStepper(string name, RecordingEditorWindow owner, bool inPoint)
        {
            _owner = owner;
            _inPoint = inPoint;
            Root = new StackPanel();
            Root.Children.Add(Label(name, 14, MutedText, FontWeights.Normal));
            var box = new Grid { Height = 38, Margin = new Thickness(0, 5, 0, 0) };
            box.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            box.ColumnDefinitions.Add(new ColumnDefinition());
            box.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            box.Children.Add(FlatIconButton(LucideIconKind.ChevronLeft, $"{name} point back one frame", (_, _) => _owner.StepTrimPoint(_inPoint, -1), 32, 16));
            _value = new Button
            {
                Content = new TextBlock { FontFamily = MonoFont, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
                Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.IBeam, Padding = new Thickness(0),
                ToolTip = $"Click to type the {name.ToLowerInvariant()} point (for example 0:04.5)", Template = RoundedTemplate()
            };
            System.Windows.Automation.AutomationProperties.SetName(_value, name + " point");
            _value.Click += (_, _) => BeginTyping();
            Grid.SetColumn(_value, 1);
            box.Children.Add(_value);
            Grid.SetColumn(_editor, 1);
            box.Children.Add(_editor);
            Button next = FlatIconButton(LucideIconKind.ChevronRight, $"{name} point forward one frame", (_, _) => _owner.StepTrimPoint(_inPoint, 1), 32, 16);
            Grid.SetColumn(next, 2);
            box.Children.Add(next);
            _editor.PreviewKeyDown += Editor_PreviewKeyDown;
            _editor.LostKeyboardFocus += (_, _) => EndTyping(commit: true);
            Root.Children.Add(new Border { Background = SurfaceBrush, CornerRadius = new CornerRadius(10), Child = box });
        }

        internal StackPanel Root { get; }

        internal void Show(double seconds)
        {
            if (_value.Content is TextBlock text) text.Text = RecordingTimeText.Tenths(seconds);
        }

        private void BeginTyping()
        {
            _editor.Text = (_value.Content as TextBlock)?.Text ?? string.Empty;
            _value.Visibility = Visibility.Collapsed;
            _editor.Visibility = Visibility.Visible;
            _editor.Focus();
            _editor.SelectAll();
        }

        private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; EndTyping(commit: true); }
            else if (e.Key == Key.Escape) { e.Handled = true; EndTyping(commit: false); }
        }

        private void EndTyping(bool commit)
        {
            if (_editor.Visibility != Visibility.Visible) return;
            _editor.Visibility = Visibility.Collapsed;
            _value.Visibility = Visibility.Visible;
            if (!commit) return;
            if (RecordingTimeText.TryParse(_editor.Text, out double seconds)) _owner.SetTrimPoint(_inPoint, seconds);
            else _owner.SetStatus("Type a time like 0:04.5 or 4.5.", true);
        }
    }
}
