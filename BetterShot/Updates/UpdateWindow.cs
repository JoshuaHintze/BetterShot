using BetterShot.Controls;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;

namespace BetterShot.Updates;

/// <summary>
/// Offers a newer release: shows its notes, then Download / Skip this version / Later. A download is verified against
/// its published SHA-256 before "Install now" appears, and the installer only runs when the user clicks it.
/// </summary>
internal sealed class UpdateWindow : Window
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x4A));
    private static readonly Brush AccentText = new SolidColorBrush(Color.FromRgb(0x17, 0x15, 0x0B));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9B, 0x9E, 0xA5));
    private readonly ReleaseInfo _release;
    private readonly Func<IProgress<double>, CancellationToken, Task<string>> _download;
    private readonly TextBlock _status = new() { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
    private readonly CancellationTokenSource _cancellation = new();
    private string? _verifiedInstaller;

    internal UpdateWindow(ReleaseInfo release, SemanticVersion current, Func<IProgress<double>, CancellationToken, Task<string>> download)
    {
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _download = download ?? throw new ArgumentNullException(nameof(download));
        Title = "BetterShot update";
        Width = 560; Height = 520; MinWidth = 440; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1C, 0x20));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF4));
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        ResizeMode = ResizeMode.CanResize;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/BetterShot.ico")); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException) { }
        Content = BuildLayout(current);
        ShowOfferButtons();
        Closed += (_, _) => { _cancellation.Cancel(); _cancellation.Dispose(); };
    }

    internal event EventHandler? SkipRequested;
    internal event EventHandler<string>? InstallRequested;

    private UIElement BuildLayout(SemanticVersion current)
    {
        var root = new Grid { Margin = new Thickness(26, 24, 26, 22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new LucideIcon { Icon = LucideIconKind.Download, Width = 22, Height = 22, Foreground = Accent, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        titleRow.Children.Add(new TextBlock { Text = $"BetterShot {_release.Version} is available", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(titleRow);
        header.Children.Add(new TextBlock { Text = $"You have {current}. {_release.Name}", Foreground = Muted, FontSize = 13, Margin = new Thickness(0, 8, 0, 14), TextTrimming = TextTrimming.CharacterEllipsis });
        root.Children.Add(header);

        var notes = new TextBox
        {
            Text = _release.DisplayNotes, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(0x12, 0x13, 0x16)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xCB, 0xD0)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x34, 0x36, 0x3B)),
            BorderThickness = new Thickness(1), Padding = new Thickness(10), FontSize = 13
        };
        System.Windows.Automation.AutomationProperties.SetName(notes, "Release notes");
        Grid.SetRow(notes, 1);
        root.Children.Add(notes);

        var footer = new StackPanel();
        footer.Children.Add(_status);
        footer.Children.Add(_buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private void ShowOfferButtons()
    {
        _buttons.Children.Clear();
        if (!string.IsNullOrEmpty(_release.PageUrl)) _buttons.Children.Add(MakeButton("View on GitHub", null, primary: false, OpenReleasePage));
        _buttons.Children.Add(MakeButton("Skip this version", null, primary: false, () => { SkipRequested?.Invoke(this, EventArgs.Empty); Close(); }));
        _buttons.Children.Add(MakeButton("Later", null, primary: false, Close));
        Button download = MakeButton("Download", LucideIconKind.Download, primary: true, () => _ = DownloadAsync());
        download.IsEnabled = _release.Installer is not null && _release.InstallerChecksum is not null;
        if (!download.IsEnabled) _status.Text = "This release has no verified Windows installer yet. Use View on GitHub to download it manually.";
        _buttons.Children.Add(download);
    }

    private async Task DownloadAsync()
    {
        foreach (UIElement child in _buttons.Children) child.IsEnabled = false;
        _status.Foreground = Muted;
        _status.Text = "Downloading…";
        var progress = new Progress<double>(fraction => _status.Text = $"Downloading… {fraction:P0}");
        try
        {
            _verifiedInstaller = await _download(progress, _cancellation.Token);
            ShowInstallButtons();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x87, 0x87));
            _status.Text = $"The update could not be downloaded: {ex.Message}";
            ShowOfferButtons();
        }
    }

    private void ShowInstallButtons()
    {
        _status.Foreground = Muted;
        _status.Text = "Downloaded and verified. Installing closes BetterShot and opens the installer. Your captures and settings are kept.";
        _buttons.Children.Clear();
        _buttons.Children.Add(MakeButton("Later", null, primary: false, Close));
        _buttons.Children.Add(MakeButton("Install now", LucideIconKind.Check, primary: true, () =>
        {
            if (_verifiedInstaller is not string path) return;
            InstallRequested?.Invoke(this, path);
            Close();
        }));
    }

    private void OpenReleasePage()
    {
        try { using Process? browser = Process.Start(new ProcessStartInfo { FileName = _release.PageUrl, UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { _status.Text = $"Could not open the browser: {ex.Message}"; }
    }

    private static Button MakeButton(string text, LucideIconKind? icon, bool primary, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is LucideIconKind kind) content.Children.Add(new LucideIcon { Icon = kind, Width = 15, Height = 15, Foreground = primary ? AccentText : Brushes.White, Margin = new Thickness(0, 0, 7, 0) });
        content.Children.Add(new TextBlock { Text = text, Foreground = primary ? AccentText : Brushes.White, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, Height = 36, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 4, 14, 4) };
        if (primary) { button.Background = Accent; button.BorderBrush = Accent; }
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => click();
        return button;
    }
}
