using BetterShot.Controls;
using BetterShot.Persistence;
using BetterShot.Recording;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterShot.Presentation;

internal enum RecordingSetupChoice { Start, Cancel, Reselect }

/// <summary>What the Area row says, which devices the pickers offer, and where recordings are saved.</summary>
internal sealed record RecordingSetupInfo(
    string AreaTitle,
    string AreaDetail,
    LucideIconKind AreaIcon,
    IReadOnlyList<RecordingDevice> Microphones,
    string DefaultMicrophoneName,
    IReadOnlyList<RecordingDevice> Cameras,
    string SaveFolder);

/// <summary>
/// The "New recording" card (KhingShot parity): area, microphone, camera, system audio, clicks, keystrokes, countdown,
/// save folder, and Start recording. Every change is saved straight to the recording settings, so Settings > Recording
/// stays in sync. <c>save</c> applies a change and returns the settings actually saved (unchanged when saving failed).
/// </summary>
internal partial class RecordingSetupCard : UserControl
{
    private const string NotConnectedLabel = "Saved device (not connected)";
    private readonly RecordingSetupInfo _info;
    private readonly Func<Func<CaptureSettings, CaptureSettings>, CaptureSettings> _save;
    private CaptureSettings _settings;
    private bool _loading;

    internal RecordingSetupCard(RecordingSetupInfo info, CaptureSettings settings, Func<Func<CaptureSettings, CaptureSettings>, CaptureSettings> save)
    {
        InitializeComponent();
        _info = info;
        _save = save;
        _settings = settings;
        AreaTitleText.Text = info.AreaTitle;
        AreaDetailText.Text = info.AreaDetail;
        AreaIcon.Icon = info.AreaIcon;
        AutomationPropertiesSetName(AreaButton, $"{info.AreaTitle}, {info.AreaDetail}. Pick a different area");
        SaveFolderText.Text = "Saves to " + info.SaveFolder;
        SaveFolderText.ToolTip = info.SaveFolder;
        WireTile(MicrophoneTile, MicrophoneToggle);
        WireTile(CameraTile, CameraToggle);
        WireTile(SystemAudioTile, SystemAudioToggle);
        WireTile(ClicksTile, ClicksToggle);
        WireTile(KeystrokesTile, KeystrokesToggle);
        ShowSettings(settings);
    }

    /// <summary>Raised once the user starts, cancels, or asks to pick the area again.</summary>
    internal event EventHandler<RecordingSetupChoice>? Chosen;

    /// <summary>True while a device menu is open, so Enter/Esc go to the menu rather than the card.</summary>
    internal bool IsMenuOpen { get; private set; }

    internal CaptureSettings Settings => _settings;

    internal void FocusStart() => StartButton.Focus();

    /// <summary>Enter starts (unless another button has focus, which is then clicked) and Esc cancels.</summary>
    internal bool HandleKey(Key key)
    {
        if (IsMenuOpen) return false;
        if (key == Key.Escape) { Choose(RecordingSetupChoice.Cancel); return true; }
        if (key != Key.Enter) return false;
        if (Keyboard.FocusedElement is Button focused && !ReferenceEquals(focused, StartButton) && IsAncestorOf(focused))
            focused.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, focused));
        else
            Choose(RecordingSetupChoice.Start);
        return true;
    }

    /// <summary>Flips a switch exactly as a click would (used by the row tiles and diagnostics).</summary>
    internal void Flip(ToggleButton toggle)
    {
        if (!toggle.IsEnabled) return;
        toggle.IsChecked = toggle.IsChecked != true;
        SaveToggles();
    }

    /// <summary>Selects a device as the device menu would (empty id = Windows default / first camera).</summary>
    internal void SelectMicrophone(string deviceId) => Apply(settings => settings with { MicrophoneDeviceId = deviceId });

    internal void SelectCamera(string deviceId) => Apply(settings => settings with { CameraDeviceId = deviceId });

    internal void SelectCountdown(int seconds)
    {
        RadioButton? target = seconds switch { 0 => CountdownOff, 3 => CountdownThree, 5 => CountdownFive, _ => null };
        if (target is not null) target.IsChecked = true;
    }

    private void ShowSettings(CaptureSettings settings)
    {
        _settings = settings;
        _loading = true;
        try
        {
            MicrophoneToggle.IsChecked = settings.RecordMicrophone;
            CameraToggle.IsChecked = settings.RecordCamera;
            SystemAudioToggle.IsChecked = settings.RecordSystemAudio;
            ClicksToggle.IsChecked = settings.HighlightClicks;
            KeystrokesToggle.IsChecked = settings.ShowKeystrokes;
            CountdownOff.IsChecked = settings.RecordingCountdownSeconds == 0;
            CountdownThree.IsChecked = settings.RecordingCountdownSeconds == 3;
            CountdownFive.IsChecked = settings.RecordingCountdownSeconds == 5;
        }
        finally { _loading = false; }
        CameraIcon.Icon = settings.RecordCamera ? LucideIconKind.Camera : LucideIconKind.CameraOff;
        MicrophoneDeviceButton.Content = MicrophoneLabel(settings.MicrophoneDeviceId);
        CameraDeviceButton.Content = CameraLabel(settings.CameraDeviceId);
        CameraDeviceButton.IsEnabled = _info.Cameras.Count > 0 || settings.CameraDeviceId.Length > 0;
    }

    internal string MicrophoneLabel(string deviceId)
    {
        if (deviceId.Length == 0) return _info.DefaultMicrophoneName.Length > 0 ? _info.DefaultMicrophoneName : "Default microphone";
        return _info.Microphones.FirstOrDefault(device => device.Id == deviceId)?.Name ?? NotConnectedLabel;
    }

    internal string CameraLabel(string deviceId)
    {
        if (deviceId.Length == 0) return _info.Cameras.Count > 0 ? _info.Cameras[0].Name : "No camera found";
        return _info.Cameras.FirstOrDefault(device => device.Id == deviceId)?.Name ?? NotConnectedLabel;
    }

    private void Apply(Func<CaptureSettings, CaptureSettings> change) => ShowSettings(_save(change));

    private void SaveToggles()
    {
        if (_loading) return;
        bool microphone = MicrophoneToggle.IsChecked == true, camera = CameraToggle.IsChecked == true, systemAudio = SystemAudioToggle.IsChecked == true;
        bool clicks = ClicksToggle.IsChecked == true, keys = KeystrokesToggle.IsChecked == true;
        Apply(settings => settings with
        {
            RecordMicrophone = microphone,
            RecordCamera = camera,
            RecordSystemAudio = systemAudio,
            HighlightClicks = clicks,
            ShowKeystrokes = keys
        });
    }

    private void Toggle_Click(object sender, RoutedEventArgs e) => SaveToggles();

    private void Countdown_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)) return;
        Apply(settings => settings with { RecordingCountdownSeconds = seconds });
    }

    /// <summary>Clicking anywhere on a row (other than its buttons) flips its switch, like the reference app.</summary>
    private void WireTile(Border tile, ToggleButton toggle)
    {
        tile.Cursor = Cursors.Hand;
        tile.Background = (Brush)FindResource("TileBackground");
        tile.MouseEnter += (_, _) => tile.Background = (Brush)FindResource("TileHover");
        tile.MouseLeave += (_, _) => tile.Background = (Brush)FindResource("TileBackground");
        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindAncestor<ButtonBase>(source, tile) is not null) return;
            e.Handled = true;
            Flip(toggle);
        };
    }

    private void MicrophoneDevice_Click(object sender, RoutedEventArgs e)
    {
        var choices = new List<RecordingDevice> { new(string.Empty, _info.DefaultMicrophoneName.Length > 0 ? $"Default ({_info.DefaultMicrophoneName})" : "Default microphone") };
        choices.AddRange(_info.Microphones);
        ShowDeviceMenu(MicrophoneDeviceButton, choices, _settings.MicrophoneDeviceId, SelectMicrophone);
    }

    private void CameraDevice_Click(object sender, RoutedEventArgs e)
    {
        var choices = new List<RecordingDevice> { new(string.Empty, _info.Cameras.Count > 0 ? $"Default ({_info.Cameras[0].Name})" : "Default camera") };
        choices.AddRange(_info.Cameras);
        ShowDeviceMenu(CameraDeviceButton, choices, _settings.CameraDeviceId, SelectCamera);
    }

    private void ShowDeviceMenu(Button anchor, List<RecordingDevice> choices, string selectedId, Action<string> select)
    {
        if (selectedId.Length > 0 && choices.All(device => device.Id != selectedId)) choices.Add(new RecordingDevice(selectedId, NotConnectedLabel));
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            Background = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x27)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF5)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40)),
            FontFamily = FontFamily,
            FontSize = 13
        };
        foreach (RecordingDevice device in choices)
        {
            string id = device.Id;
            var item = new MenuItem { Header = device.Name, IsCheckable = true, IsChecked = id == selectedId, Foreground = menu.Foreground };
            item.Click += (_, _) => select(id);
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => IsMenuOpen = true;
        menu.Closed += (_, _) => { IsMenuOpen = false; anchor.Focus(); };
        menu.IsOpen = true;
    }

    private void AreaButton_Click(object sender, RoutedEventArgs e) => Choose(RecordingSetupChoice.Reselect);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Choose(RecordingSetupChoice.Cancel);
    private void StartButton_Click(object sender, RoutedEventArgs e) => Choose(RecordingSetupChoice.Start);

    private void Choose(RecordingSetupChoice choice) => Chosen?.Invoke(this, choice);

    private static void AutomationPropertiesSetName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    private static T? FindAncestor<T>(DependencyObject? element, DependencyObject stopAt) where T : DependencyObject
    {
        while (element is not null && !ReferenceEquals(element, stopAt))
        {
            if (element is T match) return match;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
}
