using BetterShot.Controls;
using BetterShot.Platform;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Onboarding;

/// <summary>
/// "Welcome to BetterShot" first-run setup: welcome, shortcuts (Win+Shift+S toggle and capture hotkeys), start with
/// Windows, and "Take your first screenshot". Every step can be skipped; closing the window counts as skipping.
/// </summary>
internal sealed class OnboardingWindow : Window
{
    internal const int StepCount = 4;
    private static readonly HotkeyAction[] SetupActions = [HotkeyAction.Area, HotkeyAction.Window, HotkeyAction.Fullscreen];
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x4A));
    private static readonly Brush AccentText = new SolidColorBrush(Color.FromRgb(0x17, 0x15, 0x0B));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9B, 0x9E, 0xA5));
    private static readonly Brush Problem = new SolidColorBrush(Color.FromRgb(0xFF, 0x87, 0x87));
    private static readonly Brush Card = new SolidColorBrush(Color.FromRgb(0x1F, 0x20, 0x24));
    private static readonly Brush CardBorder = new SolidColorBrush(Color.FromRgb(0x34, 0x36, 0x3B));
    private readonly IOnboardingHost _host;
    private readonly ContentControl _page = new() { Focusable = false };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _navigation = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Dictionary<HotkeyAction, HotkeyRecorderButton> _recorders = new();
    private readonly TextBlock _hotkeyProblem = new() { Foreground = Problem, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private bool _completed;
    private bool _updatingToggles;

    internal OnboardingWindow(IOnboardingHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Title = "Welcome to BetterShot";
        Width = 660; Height = 580; MinWidth = 560; MinHeight = 520;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x15, 0x16, 0x19));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF4));
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/BetterShot.ico")); }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UriFormatException) { }
        Content = BuildLayout();
        ShowStep(0);
        Closed += (_, _) => Complete();
    }

    internal int CurrentStep { get; private set; }

    internal void ShowStep(int step)
    {
        CurrentStep = Math.Clamp(step, 0, StepCount - 1);
        _page.Content = CurrentStep switch
        {
            0 => BuildWelcome(),
            1 => BuildShortcuts(),
            2 => BuildStartup(),
            _ => BuildFirstScreenshot()
        };
        UpdateDots();
        UpdateNavigation();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(36, 30, 36, 26) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_page);
        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.Children.Add(_dots);
        Grid.SetColumn(_navigation, 1);
        footer.Children.Add(_navigation);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        return root;
    }

    private UIElement BuildWelcome()
    {
        var page = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var logo = new Image { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 22) };
        try { logo.Source = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/BetterShot.ico")); }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UriFormatException) { }
        page.Children.Add(logo);
        page.Children.Add(Heading("Welcome to BetterShot"));
        page.Children.Add(Paragraph("Capture an area, a window, or a whole display. Every capture waits as a thumbnail in the corner of your screen, ready to edit, copy, or drag into any app."));
        page.Children.Add(Feature(LucideIconKind.Maximize, "Area, window, display, and scrolling captures"));
        page.Children.Add(Feature(LucideIconKind.Pencil, "Annotate with arrows, text, steps, redaction, and backgrounds"));
        page.Children.Add(Feature(LucideIconKind.PanelBottom, "Lives quietly in your notification area"));
        page.Children.Add(Paragraph("This takes about a minute. You can change everything later in Settings.", top: 22));
        return page;
    }

    private UIElement BuildShortcuts()
    {
        var page = new StackPanel();
        page.Children.Add(StepIcon(LucideIconKind.Keyboard));
        page.Children.Add(Heading("Set up your shortcuts"));
        page.Children.Add(Paragraph("Use the shortcut you already know, or pick your own. Ctrl + Shift + F9 always captures an area."));
        CheckBox snip = Toggle("Use Win + Shift + S for area capture", _host.ReplaceScreenshotShortcut);
        snip.Checked += (_, _) => SetSnipShortcut(snip, true);
        snip.Unchecked += (_, _) => SetSnipShortcut(snip, false);
        page.Children.Add(snip);
        page.Children.Add(Hint("Replaces the Windows Snipping Tool shortcut while BetterShot is running."));
        var grid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _recorders.Clear();
        for (int row = 0; row < SetupActions.Length; row++)
        {
            HotkeyAction action = SetupActions[row];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = ActionLabel(action), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            Grid.SetRow(label, row);
            grid.Children.Add(label);
            HotkeyRecorderButton recorder = Recorder(action);
            Grid.SetRow(recorder, row);
            Grid.SetColumn(recorder, 1);
            grid.Children.Add(recorder);
        }
        page.Children.Add(grid);
        page.Children.Add(Hint("Click a shortcut, then press the keys. Esc cancels; Backspace clears.", indent: false));
        page.Children.Add(_hotkeyProblem);
        return page;
    }

    private UIElement BuildStartup()
    {
        var page = new StackPanel();
        page.Children.Add(StepIcon(LucideIconKind.Power));
        page.Children.Add(Heading("Start with Windows"));
        page.Children.Add(Paragraph("Keep BetterShot ready in the notification area after you sign in, so your shortcuts always work."));
        CheckBox startup = Toggle("Start BetterShot when I sign in", _host.StartWithWindows);
        startup.IsEnabled = _host.CanConfigureStartup;
        startup.Checked += (_, _) => SetStartup(startup, true);
        startup.Unchecked += (_, _) => SetStartup(startup, false);
        page.Children.Add(startup);
        page.Children.Add(Hint(_host.CanConfigureStartup ? "BetterShot opens quietly in the tray; it never shows a window at sign-in." : "Startup can't be changed in this session."));
        return page;
    }

    private UIElement BuildFirstScreenshot()
    {
        var page = new StackPanel();
        page.Children.Add(StepIcon(LucideIconKind.Camera));
        page.Children.Add(Heading("Take your first screenshot"));
        string shortcut = _host.ReplaceScreenshotShortcut ? "Win + Shift + S" : HotkeyLabel(HotkeyAction.Area);
        page.Children.Add(Paragraph($"Press {shortcut} anytime, or try it now. Drag to select an area, or click a window. Your capture appears as a thumbnail in the bottom-left corner."));
        Button capture = MakeButton("Capture an area now", LucideIconKind.Maximize, primary: true, () =>
        {
            Complete();
            Close();
            _host.TakeFirstScreenshot();
        });
        capture.HorizontalAlignment = HorizontalAlignment.Left;
        capture.Margin = new Thickness(0, 20, 0, 0);
        page.Children.Add(capture);
        page.Children.Add(Paragraph("Tip: hover a thumbnail for Edit, Copy, and Save, or drag it straight into a chat or document.", top: 22));
        return page;
    }

    private void UpdateNavigation()
    {
        _navigation.Children.Clear();
        if (CurrentStep < StepCount - 1)
            _navigation.Children.Add(MakeButton("Skip setup", null, primary: false, Close));
        if (CurrentStep > 0)
            _navigation.Children.Add(MakeButton("Back", LucideIconKind.ChevronLeft, primary: false, () => ShowStep(CurrentStep - 1)));
        _navigation.Children.Add(CurrentStep switch
        {
            0 => MakeButton("Get started", LucideIconKind.ChevronRight, primary: true, () => ShowStep(1)),
            StepCount - 1 => MakeButton("Finish", LucideIconKind.Check, primary: false, Close),
            _ => MakeButton("Next", LucideIconKind.ChevronRight, primary: true, () => ShowStep(CurrentStep + 1))
        });
    }

    private void UpdateDots()
    {
        _dots.Children.Clear();
        for (int step = 0; step < StepCount; step++)
            _dots.Children.Add(new Border
            {
                Width = step == CurrentStep ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0),
                Background = step == CurrentStep ? Accent : CardBorder
            });
    }

    private void SetSnipShortcut(CheckBox toggle, bool enabled)
    {
        if (_updatingToggles) return;
        bool active = _host.SetReplaceScreenshotShortcut(enabled);
        SetToggleQuietly(toggle, active);
    }

    private void SetStartup(CheckBox toggle, bool enabled)
    {
        if (_updatingToggles) return;
        SetToggleQuietly(toggle, _host.SetStartWithWindows(enabled));
    }

    private void SetToggleQuietly(CheckBox toggle, bool value)
    {
        _updatingToggles = true;
        try { toggle.IsChecked = value; }
        finally { _updatingToggles = false; }
    }

    private HotkeyRecorderButton Recorder(HotkeyAction action)
    {
        var recorder = new HotkeyRecorderButton
        {
            Width = 210, Height = 34, Margin = new Thickness(0, 5, 0, 5), HorizontalAlignment = HorizontalAlignment.Right,
            Tag = action, Binding = _host.HotkeyFor(action), IsUnavailable = _host.IsHotkeyUnavailable(action)
        };
        if (_host.FindStyle("QuietButton") is Style quiet && quiet.TargetType.IsAssignableFrom(typeof(HotkeyRecorderButton))) recorder.Style = quiet;
        recorder.Width = 210;
        System.Windows.Automation.AutomationProperties.SetName(recorder, ActionLabel(action) + " shortcut");
        recorder.RecordingStarted += (_, _) => _host.BeginHotkeyRecording();
        recorder.RecordingEnded += (_, _) => { _host.EndHotkeyRecording(); RefreshRecorders(); };
        recorder.Recorded += (_, e) =>
        {
            string? problem = _host.ApplyHotkey(action, e.Binding);
            _hotkeyProblem.Text = problem ?? string.Empty;
            _hotkeyProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
            RefreshRecorders();
        };
        _recorders[action] = recorder;
        return recorder;
    }

    private void RefreshRecorders()
    {
        foreach ((HotkeyAction action, HotkeyRecorderButton recorder) in _recorders)
        {
            if (recorder.IsRecording) continue;
            recorder.Binding = _host.HotkeyFor(action);
            recorder.IsUnavailable = _host.IsHotkeyUnavailable(action);
        }
    }

    private string HotkeyLabel(HotkeyAction action)
    {
        HotkeyBinding binding = _host.HotkeyFor(action);
        return binding.IsEmpty ? "Ctrl + Shift + F9" : binding.ToString();
    }

    private static string ActionLabel(HotkeyAction action) => action switch
    {
        HotkeyAction.Area => "Capture area",
        HotkeyAction.Window => "Capture window",
        HotkeyAction.Fullscreen => "Capture fullscreen",
        _ => action.ToString()
    };

    private void Complete()
    {
        if (_completed) return;
        _completed = true;
        _host.CompleteOnboarding();
    }

    private CheckBox Toggle(string text, bool isChecked)
    {
        var toggle = new CheckBox { Content = text, IsChecked = isChecked, Foreground = Foreground, Margin = new Thickness(0, 18, 0, 0), Cursor = Cursors.Hand, FontSize = 14 };
        if (_host.FindStyle("ShortcutToggle") is Style style) toggle.Style = style;
        return toggle;
    }

    private static UIElement StepIcon(LucideIconKind icon) => new Border
    {
        Width = 54, Height = 54, CornerRadius = new CornerRadius(27), Background = new SolidColorBrush(Color.FromRgb(0x24, 0x23, 0x18)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x46, 0x40, 0x21)), BorderThickness = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 18),
        Child = new LucideIcon { Icon = icon, Width = 24, Height = 24, Foreground = Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
    };

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 26, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Paragraph(string text, double top = 10) =>
        new() { Text = text, Foreground = Muted, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, top, 0, 0) };

    private static TextBlock Hint(string text, bool indent = true) =>
        new() { Text = text, Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(indent ? 42 : 0, 8, 0, 0) };

    private static UIElement Feature(LucideIconKind icon, string text)
    {
        var row = new Border { Background = Card, BorderBrush = CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 10, 0, 0) };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new LucideIcon { Icon = icon, Width = 17, Height = 17, Foreground = Accent, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        row.Child = content;
        return row;
    }

    private static Button MakeButton(string text, LucideIconKind? icon, bool primary, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new TextBlock { Text = text, Foreground = primary ? AccentText : Brushes.White, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        bool trailingIcon = icon is LucideIconKind.ChevronRight;
        if (!trailingIcon) AddIcon(content, icon, primary, new Thickness(0, 0, 7, 0));
        content.Children.Add(label);
        if (trailingIcon) AddIcon(content, icon, primary, new Thickness(7, 0, 0, 0));
        var button = new Button { Content = content, Height = 38, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(16, 4, 16, 4) };
        if (primary) { button.Background = Accent; button.BorderBrush = Accent; }
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => click();
        return button;
    }

    private static void AddIcon(Panel content, LucideIconKind? icon, bool primary, Thickness margin)
    {
        if (icon is LucideIconKind kind)
            content.Children.Add(new LucideIcon { Icon = kind, Width = 15, Height = 15, Foreground = primary ? AccentText : Brushes.White, Margin = margin });
    }
}
