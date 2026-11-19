using BetterShot.Recording;
using BetterShot.Persistence;
using BetterShot.Updates;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;

namespace BetterShot;

/// <summary>
/// Update checks against GitHub Releases: daily in the background (when enabled and not capturing) and on demand from
/// Settings. Offers are shown in <see cref="UpdateWindow"/>; nothing is installed without the user clicking Install now.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan FirstBackgroundCheckDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BackgroundCheckPollInterval = TimeSpan.FromHours(1);
    private static readonly Lazy<HttpClient> UpdateApiClient = new(() => GitHubReleaseClient.CreateHttpClient());
    private static readonly Lazy<HttpClient> UpdateDownloadClient = new(CreateUpdateDownloadClient);
    private DispatcherTimer? _updateTimer;
    private bool _checkingForUpdates;
    private UpdateWindow? _updateWindow;

    private static SemanticVersion CurrentVersion => SemanticVersion.FromAssemblyVersion(Assembly.GetExecutingAssembly().GetName().Version);

    /// <summary>True while a capture of any kind is in progress; background update checks wait until it finishes.</summary>
    private bool IsCaptureInProgress => _capturing || _recordingState.State is not (RecordingState.Idle or RecordingState.Completed or RecordingState.Failed or RecordingState.Canceled);

    private void StartBackgroundUpdateChecks()
    {
        if (_updateTimer is not null) return;
        _updateTimer = new DispatcherTimer { Interval = FirstBackgroundCheckDelay };
        _updateTimer.Tick += UpdateTimer_Tick;
        _updateTimer.Start();
        Closed += (_, _) => _updateTimer?.Stop();
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (_updateTimer is not null) _updateTimer.Interval = BackgroundCheckPollInterval;
        CaptureSettings settings = CurrentSettings;
        if (!settings.AutoCheckForUpdates || IsCaptureInProgress || !UpdatePolicy.IsCheckDue(settings.LastUpdateCheckUtc, DateTime.UtcNow)) return;
        _ = CheckForUpdatesAsync(manual: false);
    }

    private void LoadUpdateSettingsIntoControls()
    {
        CaptureSettings settings = CurrentSettings;
        AutoUpdateCheckBox.IsChecked = settings.AutoCheckForUpdates;
        if (!_checkingForUpdates)
            UpdateStatusText.Text = settings.LastUpdateCheckUtc is DateTime last
                ? $"Last checked {last.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture)}."
                : "Not checked yet.";
    }

    private void AutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _updatingSettings) return;
        bool enabled = AutoUpdateCheckBox.IsChecked == true;
        TrySaveSettings(settings => settings with { AutoCheckForUpdates = enabled }, "the update preference");
        SetStatus(enabled ? "BetterShot will check for updates once a day." : "Automatic update checks are off.");
    }

    private void CheckForUpdates_Click(object sender, RoutedEventArgs e) => _ = CheckForUpdatesAsync(manual: true);

    /// <summary>Background checks stay silent on errors or when up to date; manual checks always report the result.</summary>
    internal async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingForUpdates) return;
        _checkingForUpdates = true;
        CheckForUpdatesButton.IsEnabled = false;
        if (manual) UpdateStatusText.Text = "Checking for updates…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            ReleaseInfo? release = await new GitHubReleaseClient(UpdateApiClient.Value).GetLatestAsync(timeout.Token);
            RememberUpdateCheck();
            UpdateAvailability availability = UpdatePolicy.Evaluate(release, CurrentVersion, CurrentSettings.SkippedUpdateVersion, manual);
            ReportUpdateResult(availability, release, manual);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or IOException)
        {
            if (manual) UpdateStatusText.Text = "Could not reach GitHub to check for updates. Check your connection and try again.";
        }
        finally
        {
            _checkingForUpdates = false;
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private void ReportUpdateResult(UpdateAvailability availability, ReleaseInfo? release, bool manual)
    {
        string checkedAt = $"Checked {DateTime.Now.ToString("h:mm tt", CultureInfo.CurrentCulture)}.";
        switch (availability)
        {
            case UpdateAvailability.Available when release is not null:
                UpdateStatusText.Text = $"BetterShot {release.Version} is available. {checkedAt}";
                ShowUpdateWindow(release, activate: manual);
                break;
            case UpdateAvailability.NoReleases:
                UpdateStatusText.Text = $"No BetterShot releases are published yet. {checkedAt}";
                break;
            case UpdateAvailability.Skipped:
                UpdateStatusText.Text = $"Skipping BetterShot {release?.Version}. {checkedAt}";
                break;
            default:
                UpdateStatusText.Text = $"BetterShot {CurrentVersion} is up to date. {checkedAt}";
                break;
        }
    }

    private void RememberUpdateCheck()
    {
        try { UpdateSettings(settings => settings with { LastUpdateCheckUtc = DateTime.UtcNow }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* The next check simply happens sooner. */ }
    }

    private void ShowUpdateWindow(ReleaseInfo release, bool activate)
    {
        if (_updateWindow is { IsLoaded: true }) { if (activate) _updateWindow.Activate(); return; }
        var downloader = new UpdateDownloader(UpdateDownloadClient.Value);
        var window = new UpdateWindow(release, CurrentVersion, (progress, cancellation) => downloader.DownloadVerifiedInstallerAsync(release, progress, cancellation))
        {
            ShowActivated = activate
        };
        window.SkipRequested += (_, _) => SkipUpdate(release);
        window.InstallRequested += (_, path) => LaunchInstallerAndExit(path);
        window.Closed += (_, _) => _updateWindow = null;
        _updateWindow = window;
        window.Show();
    }

    private void SkipUpdate(ReleaseInfo release)
    {
        string version = release.Version.ToString();
        TrySaveSettings(settings => settings with { SkippedUpdateVersion = version }, "the skipped version");
        UpdateStatusText.Text = $"BetterShot {version} will not be offered again. Use Check for updates to see it anyway.";
    }

    /// <summary>Runs only after the user clicked Install now on a hash-verified installer. BetterShot exits so setup can replace it.</summary>
    private void LaunchInstallerAndExit(string installerPath)
    {
        try
        {
            using Process? installer = Process.Start(new ProcessStartInfo { FileName = installerPath, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            SetStatus($"Could not start the installer: {FriendlyMessage(ex)}", error: true, persistent: true);
            return;
        }
        ExitApplication();
    }

    private static HttpClient CreateUpdateDownloadClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(GitHubReleaseClient.UserAgent);
        return client;
    }
}
