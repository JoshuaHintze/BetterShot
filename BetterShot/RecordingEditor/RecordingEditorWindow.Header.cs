using BetterShot.Controls;
using BetterShot.Presentation;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Polygon = System.Windows.Shapes.Polygon;
using System.Windows.Shell;
using static BetterShot.RecordingEditor.RecordingEditorChrome;

namespace BetterShot.RecordingEditor;

/// <summary>
/// The header (KhingShot layout): title and facts on the left, the floating markup tool pill in the middle (grouped
/// tools carry a corner triangle and remember the last member used), and show-in-Explorer, share, Revert, and Done on
/// the right, followed by the Windows caption buttons. The header doubles as the window's title bar.
/// </summary>
internal sealed partial class RecordingEditorWindow
{
    private const double HeaderHeight = 64;

    /// <summary>A pill button and the tools it switches between (the first is the default).</summary>
    private sealed record ToolGroup(string Name, (VideoMarkTool Tool, LucideIconKind Icon, string Label)[] Members)
    {
        internal int Current { get; set; }
        internal Button? Button { get; set; }
    }

    private readonly List<ToolGroup> _toolGroups =
    [
        new("Select", [(VideoMarkTool.Select, LucideIconKind.MousePointer2, "Select, move, and resize marks")]),
        new("Shapes", [(VideoMarkTool.Rectangle, LucideIconKind.Square, "Rectangle"), (VideoMarkTool.Ellipse, LucideIconKind.Circle, "Ellipse")]),
        new("Lines", [(VideoMarkTool.Arrow, LucideIconKind.ArrowUpRight, "Arrow"), (VideoMarkTool.Line, LucideIconKind.Slash, "Line")]),
        new("Draw", [(VideoMarkTool.Pen, LucideIconKind.Signature, "Pen"), (VideoMarkTool.Highlighter, LucideIconKind.Highlighter, "Highlighter")]),
        new("Text", [(VideoMarkTool.Callout, LucideIconKind.MessageSquare, "Callout: drag from what it points at to where the bubble goes"), (VideoMarkTool.Text, LucideIconKind.Type, "Text")]),
        new("Step", [(VideoMarkTool.Step, LucideIconKind.ListOrdered, "Step number")]),
        new("Effects", [(VideoMarkTool.Redact, LucideIconKind.Grid3x3, "Redact (solid, pixelate, or blur)"), (VideoMarkTool.Spotlight, LucideIconKind.Flashlight, "Spotlight")])
    ];
    private readonly TextBlock _subtitle = Label(string.Empty, 13.5, MutedText, FontWeights.Normal);
    private Button? _revertButton, _widthButton, _followButton, _maximizeButton;
    private Border? _widthSwatch;
    private TextBlock? _widthLabel;

    private static string ToolName(VideoMarkTool tool) => tool switch
    {
        VideoMarkTool.Redact => "Redaction",
        VideoMarkTool.Step => "Step",
        _ => tool.ToString()
    };

    private void ConfigureWindowChrome()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = HeaderHeight - 8, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
        });
        StateChanged += (_, _) => OnWindowStateChanged();
    }

    /// <summary>A maximized chrome-less window hangs over the screen edge by its resize frame; pad it back in.</summary>
    private void OnWindowStateChanged()
    {
        if (Content is FrameworkElement root)
        {
            Thickness frame = SystemParameters.WindowResizeBorderThickness;
            root.Margin = WindowState == WindowState.Maximized ? new Thickness(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4) : new Thickness(0);
        }
        if (_maximizeButton?.Content is TextBlock glyph) glyph.Text = WindowState == WindowState.Maximized ? "" : "";
        if (_maximizeButton is not null) _maximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    private UIElement BuildHeader()
    {
        var bar = new Grid { Height = HeaderHeight };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 12 });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 12, 0) };
        TextBlock heading = Label("Screen recording", 19, Brushes.White, FontWeights.SemiBold);
        heading.ToolTip = Path.GetFileName(Entry.MoviePath);
        title.Children.Add(heading);
        _subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _subtitle.Margin = new Thickness(0, 2, 0, 0);
        title.Children.Add(_subtitle);
        bar.Children.Add(title);

        UIElement pill = BuildToolPill();
        Grid.SetColumn(pill, 1);
        bar.Children.Add(pill);

        UIElement actions = BuildHeaderActions();
        Grid.SetColumn(actions, 3);
        bar.Children.Add(actions);

        UIElement caption = BuildCaptionButtons();
        Grid.SetColumn(caption, 4);
        bar.Children.Add(caption);
        return bar;
    }

    private UIElement BuildToolPill()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (ToolGroup group in _toolGroups)
        {
            ToolGroup current = group;
            Button button = FlatIconButton(group.Members[0].Icon, group.Members[0].Label, (sender, _) => ToolGroupClicked(current, (FrameworkElement)sender), 40, 19);
            button.Height = 36;
            if (group.Members.Length > 1)
            {
                button.Content = WithCornerTriangle((UIElement)button.Content);
                button.MouseRightButtonUp += (sender, e) => { e.Handled = true; OpenToolGroupMenu(current, (FrameworkElement)sender); };
            }
            group.Button = button;
            row.Children.Add(button);
        }
        _followButton = FlatIconButton(LucideIconKind.Crosshair, "Follow: moves the selected redaction with what it covers as the recording plays", (sender, _) => OpenFollowMenu((FrameworkElement)sender), 40, 18);
        _followButton.Height = 36;
        _followButton.Visibility = Visibility.Collapsed;
        row.Children.Add(_followButton);
        row.Children.Add(VerticalRule(22));
        row.Children.Add(BuildWidthButton());
        var pill = new Border
        {
            Background = SurfaceBrush, BorderBrush = SurfaceBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(5, 4, 5, 4), Child = row, VerticalAlignment = VerticalAlignment.Center
        };
        WindowChrome.SetIsHitTestVisibleInChrome(pill, true);
        return pill;
    }

    /// <summary>The icon plus a small triangle in the bottom-right corner that marks a group with more tools.</summary>
    private static UIElement WithCornerTriangle(UIElement icon)
    {
        var grid = new Grid { Width = 26, Height = 26 };
        if (icon is FrameworkElement element) { element.HorizontalAlignment = HorizontalAlignment.Center; element.VerticalAlignment = VerticalAlignment.Center; }
        grid.Children.Add(icon);
        var triangle = new Polygon
        {
            Points = [new Point(5, 0), new Point(5, 5), new Point(0, 5)], Fill = MutedText, Width = 5, Height = 5,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -3, -2)
        };
        grid.Children.Add(triangle);
        return grid;
    }

    private Button BuildWidthButton()
    {
        _widthSwatch = new Border { Width = 18, Height = 3, CornerRadius = new CornerRadius(1.5), Background = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        _widthLabel = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var chevron = new LucideIcon { Icon = LucideIconKind.ChevronDown, Width = 14, Height = 14, Foreground = MutedText };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_widthSwatch);
        row.Children.Add(_widthLabel);
        row.Children.Add(chevron);
        _widthButton = new Button
        {
            Content = row, ToolTip = "Line width, color, text size, and redaction style for new marks and the selected mark", Height = 36,
            Padding = new Thickness(10, 0, 8, 0), Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        _widthButton.Template = RoundedTemplate();
        System.Windows.Automation.AutomationProperties.SetName(_widthButton, "Style");
        _widthButton.Click += (sender, _) => OpenStyleMenu((FrameworkElement)sender);
        return _widthButton;
    }

    private void ToolGroupClicked(ToolGroup group, FrameworkElement target)
    {
        VideoMarkTool tool = group.Members[group.Current].Tool;
        if (_tool == tool && group.Members.Length > 1) { OpenToolGroupMenu(group, target); return; }
        SelectTool(tool);
    }

    private void OpenToolGroupMenu(ToolGroup group, FrameworkElement target)
    {
        ContextMenu menu = Menu();
        for (int index = 0; index < group.Members.Length; index++)
        {
            int chosen = index;
            (VideoMarkTool tool, LucideIconKind icon, string label) = group.Members[index];
            menu.Items.Add(MenuItem(label.Split(':')[0], (_, _) => { group.Current = chosen; SelectTool(tool); }, icon, isChecked: _tool == tool));
        }
        OpenBelow(menu, target);
    }

    /// <summary>Lights the active tool's group (yellow tile, black icon) and shows the group's current member.</summary>
    private void UpdateToolPill()
    {
        foreach (ToolGroup group in _toolGroups)
        {
            int member = Array.FindIndex(group.Members, item => item.Tool == _tool);
            if (member >= 0) group.Current = member;
            if (group.Button is not Button button) continue;
            (_, LucideIconKind icon, string label) = group.Members[group.Current];
            LucideIcon? glyph = button.Content as LucideIcon ?? (button.Content as Grid)?.Children.OfType<LucideIcon>().FirstOrDefault();
            if (glyph is not null) glyph.Icon = icon;
            button.ToolTip = group.Members.Length > 1 ? label + " (right-click or click again for more)" : label;
            SetActive(button, member >= 0);
            if (member < 0) button.Background = Brushes.Transparent;
        }
        if (_widthLabel is not null) _widthLabel.Text = $"{_strokeWidth:0} px";
        if (_widthSwatch is not null)
        {
            _widthSwatch.Background = new SolidColorBrush(_color);
            _widthSwatch.Height = Math.Clamp(_strokeWidth / 2, 2, 6);
        }
        if (_followButton is not null) _followButton.Visibility = SelectedMark is { IsRedaction: true } && _reader is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement BuildHeaderActions()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Button folder = IconButton(LucideIconKind.Folder, "Show in File Explorer", (_, _) => ShowInExplorer(), 42, 40, 19);
        folder.Background = SurfaceBrush;
        row.Children.Add(folder);
        Button share = IconButton(LucideIconKind.Share, "Share the marked copy: copy the file, or share it with Windows (drag this button into an app to drop the file)", (sender, _) => OpenShareMenu((FrameworkElement)sender), 42, 40, 19);
        share.Background = SurfaceBrush;
        share.Margin = new Thickness(8, 0, 2, 0);
        AttachShareDrag(share);
        row.Children.Add(share);
        row.Children.Add(VerticalRule(26));
        _revertButton = TextButton("Revert", "Put the recording back the way it was", (_, _) => RevertRecording(), null, 40);
        _revertButton.Background = Brushes.Transparent;
        _revertButton.FontSize = 16;
        row.Children.Add(_revertButton);
        Button done = TextButton("Done", "Save and close (marked copy updated for sharing)", Done_Click, LucideIconKind.Check, 42);
        done.Background = ActiveBrush;
        done.Foreground = Brushes.Black;
        done.FontSize = 16;
        done.Padding = new Thickness(16, 4, 18, 4);
        done.Margin = new Thickness(8, 0, 4, 0);
        row.Children.Add(done);
        WindowChrome.SetIsHitTestVisibleInChrome(row, true);
        return row;
    }

    private UIElement BuildCaptionButtons()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        row.Children.Add(CaptionButton("", "Minimize", (_, _) => WindowState = WindowState.Minimized));
        _maximizeButton = CaptionButton("", "Maximize", (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        row.Children.Add(_maximizeButton);
        row.Children.Add(CaptionButton("", "Close", (_, _) => Close(), close: true));
        WindowChrome.SetIsHitTestVisibleInChrome(row, true);
        return row;
    }

    /// <summary>"Today at 1:20 PM · 0:13 · 2278 × 1008 · 29 fps · 5.6 MB".</summary>
    private void UpdateSubtitle()
    {
        long? bytes = null;
        try { bytes = new FileInfo(Entry.MoviePath).Length; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // The size is informational.
        _subtitle.Text = RecordingEditorText.Subtitle(Entry.CreatedUtc.ToLocalTime(), DateTime.Now, _info?.Duration ?? Entry.DurationSeconds,
            _info?.Width ?? Entry.Width, _info?.Height ?? Entry.Height, _info?.FrameRate ?? 0, bytes, CultureInfo.CurrentCulture);
        _subtitle.ToolTip = Entry.MoviePath;
    }

    private void UpdateHeaderState()
    {
        if (_revertButton is not null)
        {
            bool changed = State.Timeline.HasCuts || State.MutedTracks != MutedAudioTracks.None;
            _revertButton.IsEnabled = changed;
            _revertButton.Foreground = changed ? Brushes.White : DimText;
        }
    }

    private void ShowInExplorer()
    {
        try { RecordingFiles.ShowInFolder(Entry.MoviePath); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { SetStatus($"Could not open File Explorer: {error.Message}", true); }
    }

    /// <summary>Revert: no cuts, no in or out points, and every track audible again (marks stay; delete them on the timeline).</summary>
    private void RevertRecording()
    {
        if (Apply(state => state with { Timeline = state.Timeline.Reset(), MutedTracks = MutedAudioTracks.None }))
        {
            _selectedSegment = null;
            SetStatus("The recording is back the way it was: no cuts, no in or out points, and every track audible.");
        }
    }
}
