using BetterShot.Export;
using BetterShot.Persistence;
using BetterShot.Presentation;
using BetterShot.Recording;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CaptureMode = BetterShot.Capture.CaptureMode;
using Forms = System.Windows.Forms;

namespace BetterShot;

/// <summary>
/// Screen recording (Phase 6): Record button, tray menu, and hotkey; region/window/display selection; countdown;
/// floating controls and overlays while recording; finalizing into the save folder; the recordings library strip;
/// and Settings > Recording.
/// </summary>
public partial class MainWindow
{
    private readonly RecordingStateMachine _recordingState = new();
    private readonly ObservableCollection<RecordingLibraryItem> _recordingItems = new();
    private readonly List<Window> _recordingOverlays = [];
    private readonly DispatcherTimer _recordingMonitor = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private RecordingLibraryStore? _recordingLibrary;
    private string _recordingTempFolder = Path.Combine(Path.GetTempPath(), "BetterShot", "RecordingsInProgress");
    private ScreenRecorder? _recorder;
    private RecordingTarget? _recordingTarget;
    private RecordingEventLog? _recordingEvents;
    private RecordingControlsWindow? _recordingControls;
    private RecordingInputOverlay? _inputOverlay;
    private InputActivityHook? _inputHook;
    private CaptureVisibilitySnapshot? _recordingHiddenWindows;
    private CancellationTokenSource? _recordingCountdown;
    private CaptureSettings _recordingSettings = CaptureSettings.Default;
    private DateTime _recordingStartedLocal;
    private string? _recordingFailureNote;
    private bool _recordingDevicesLoaded;
    private Point _recordingCardPointerDown;

    internal RecordingState RecordingStateForTest => _recordingState.State;
    internal IReadOnlyList<RecordingLibraryItem> RecordingItemsForTest => _recordingItems;

    private void InitializeRecording(string? historyRoot)
    {
        RecordingsList.ItemsSource = _recordingItems;
        _recordingMonitor.Tick += RecordingMonitor_Tick;
        _recordingState.Changed += (_, _) => UpdateRecordButton();
        if (historyRoot is null) return;
        try
        {
            _recordingLibrary = new RecordingLibraryStore(historyRoot);
            _recordingTempFolder = _recordingLibrary.InProgressFolder;
            foreach (RecordingEntry entry in _recordingLibrary.Load()) _recordingItems.Add(new RecordingLibraryItem(entry));
            ReportInterruptedRecordings();
        }
        catch (Exception ex) { SetStatus($"Recordings library is unavailable: {FriendlyMessage(ex)}", true); }
        UpdateRecordingsPanel();
    }

    private void ReportInterruptedRecordings()
    {
        IReadOnlyList<string> leftovers = _recordingLibrary?.FindInterruptedRecordings() ?? [];
        if (leftovers.Count == 0) return;
        SetStatus($"{leftovers.Count} unfinished recording{(leftovers.Count == 1 ? " was" : "s were")} kept in {_recordingTempFolder}.", error: true, persistent: true);
    }

    private Forms.ToolStripMenuItem CreateRecordingTrayMenu()
    {
        var record = new Forms.ToolStripMenuItem("Record screen");
        record.DropDownItems.Add("Record a region…", null, (_, _) => Dispatcher.BeginInvoke(() => _ = ToggleRecordingAsync(CaptureMode.Area)));
        record.DropDownItems.Add("Record a window…", null, (_, _) => Dispatcher.BeginInvoke(() => _ = ToggleRecordingAsync(CaptureMode.Window)));
        record.DropDownItems.Add("Record a display…", null, (_, _) => Dispatcher.BeginInvoke(() => _ = ToggleRecordingAsync(CaptureMode.Display)));
        var stop = new Forms.ToolStripMenuItem("Stop recording", null, (_, _) => Dispatcher.BeginInvoke(() => _ = StopRecordingAsync())) { Visible = false };
        record.DropDownItems.Add(stop);
        record.DropDownOpening += (_, _) =>
        {
            bool capturing = _recordingState.IsCapturing;
            for (int index = 0; index < 3; index++) record.DropDownItems[index].Visible = !capturing;
            stop.Visible = capturing;
        };
        return record;
    }

    private void Record_Click(object sender, RoutedEventArgs e) => _ = ToggleRecordingAsync(CaptureMode.Area);
    private void RecordMode_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingState.IsBusy) { _ = ToggleRecordingAsync(CaptureMode.Area); return; }
        RecordModePopup.IsOpen = !RecordModePopup.IsOpen;
    }
    private void RecordRegion_Click(object sender, RoutedEventArgs e) { RecordModePopup.IsOpen = false; _ = ToggleRecordingAsync(CaptureMode.Area); }
    private void RecordWindow_Click(object sender, RoutedEventArgs e) { RecordModePopup.IsOpen = false; _ = ToggleRecordingAsync(CaptureMode.Window); }
    private void RecordDisplay_Click(object sender, RoutedEventArgs e) { RecordModePopup.IsOpen = false; _ = ToggleRecordingAsync(CaptureMode.Display); }
    private void ToggleRecordingFromShortcut() => _ = ToggleRecordingAsync(CaptureMode.Area);

    /// <summary>Starts a recording, or stops/cancels the one in progress (the Record hotkey and button both toggle).</summary>
    private async Task ToggleRecordingAsync(CaptureMode mode)
    {
        switch (_recordingState.State)
        {
            case RecordingState.Countdown: _recordingCountdown?.Cancel(); return;
            case RecordingState.Recording or RecordingState.Paused: await StopRecordingAsync(); return;
            case RecordingState.Selecting or RecordingState.Starting or RecordingState.Stopping: return;
        }
        if (_capturing) return;
        try { await StartRecordingAsync(mode); }
        catch (Exception ex)
        {
            // Last-resort guard: StartRecordingAsync reports its own errors; this keeps an async void path from crashing.
            TearDownLiveRecordingUi();
            _recordingState.TryMove(RecordingState.Failed);
            SetStatus($"{RecordingException.FailedTitle}: {FriendlyMessage(ex)}", error: true, persistent: true);
        }
    }

    private async Task StartRecordingAsync(CaptureMode mode)
    {
        if (!ScreenRecorder.IsSupported)
        {
            ShowRecordingError(new RecordingException(RecordingProblem.Unsupported, "Screen recording needs Windows 10 version 2004 or later with graphics capture available."));
            return;
        }
        CommitPendingSettingsEdits();
        _recordingSettings = CurrentSettings;
        _recordingFailureNote = null;
        _recordingState.TryMove(RecordingState.Selecting);
        _recordingHiddenWindows = _presentation.HideForCapture();
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(180);
            if (await _captureService.SelectAsync(mode) is not Int32Rect selection) { CancelRecordingStart("Recording canceled."); return; }
            _recordingTarget = RecordingTargetResolver.Resolve(mode, selection);
            if (!await RunRecordingCountdownAsync()) { CancelRecordingStart("Recording canceled."); return; }
            _recordingState.TryMove(RecordingState.Starting);
            ShowDimOverlays(_recordingTarget);
            await BeginRecorderAsync(_recordingTarget);
        }
        catch (RecordingException ex)
        {
            TearDownLiveRecordingUi();
            _recordingState.TryMove(RecordingState.Failed);
            RestoreHiddenWindows();
            ShowRecordingError(ex);
        }
    }

    private async Task<bool> RunRecordingCountdownAsync()
    {
        int seconds = _recordingSettings.RecordingCountdownSeconds;
        _recordingState.TryMove(RecordingState.Countdown);
        if (seconds <= 0) return true;
        using var cancellation = new CancellationTokenSource();
        _recordingCountdown = cancellation;
        try { return await CaptureCountdownWindow.RunAsync(seconds, cancellation.Token, "Recording in"); }
        finally { _recordingCountdown = null; }
    }

    private async Task BeginRecorderAsync(RecordingTarget target)
    {
        CaptureSettings settings = _recordingSettings;
        var options = new RecordingSessionOptions(settings.RecordingFrameRate, settings.RecordMicrophone, settings.MicrophoneDeviceId,
            settings.RecordSystemAudio, settings.RecordCamera, settings.CameraDeviceId);
        string id = Guid.NewGuid().ToString("N");
        string moviePath = Path.Combine(_recordingTempFolder, id + ".mp4");
        string? cameraPath = settings.RecordCamera ? Path.Combine(_recordingTempFolder, id + "-camera.mp4") : null;
        _recordingEvents = new RecordingEventLog();
        _recordingStartedLocal = DateTime.Now;
        SetStatus("Starting recording…", persistent: true);
        _recorder = await ScreenRecorder.StartAsync(target, options, moviePath, cameraPath, _recordingEvents);
        _recorder.SourceClosed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _recordingFailureNote ??= "The recorded window or display closed.";
            _ = StopRecordingAsync();
        });
        _recordingState.TryMove(RecordingState.Recording);
        ShowRecordingControls(target, _recorder);
        StartInputHighlights(target, settings);
        _recordingMonitor.Start();
        SetStatus("Recording. Use the floating controls, the Record button, or your shortcut to stop.", persistent: true);
    }

    private void CancelRecordingStart(string message)
    {
        TearDownLiveRecordingUi();
        _recordingState.TryMove(RecordingState.Canceled);
        RestoreHiddenWindows();
        SetStatus(message);
    }

    private async Task TogglePauseAsync()
    {
        if (_recorder is null) return;
        if (_recordingState.State == RecordingState.Recording && await _recorder.PauseAsync())
        {
            _recordingState.TryMove(RecordingState.Paused);
            _recordingControls?.SetPaused(true);
            SetStatus("Recording paused.", persistent: true);
        }
        else if (_recordingState.State == RecordingState.Paused && await _recorder.ResumeAsync())
        {
            _recordingState.TryMove(RecordingState.Recording);
            _recordingControls?.SetPaused(false);
            SetStatus("Recording resumed.", persistent: true);
        }
    }

    private void ToggleMicrophoneMute()
    {
        if (_recorder is null || !_recorder.HasMicrophone) return;
        _recorder.MicrophoneMuted = !_recorder.MicrophoneMuted;
        _recordingControls?.SetMicrophoneMuted(_recorder.MicrophoneMuted);
    }

    /// <summary>The camera keeps recording so the sidecar stays in sync; the toggle is logged for the editor to hide it.</summary>
    private void ToggleCameraVisibility()
    {
        if (_recorder is null || _recordingControls is null || !_recorder.HasCamera) return;
        bool hidden = !_recordingControls.IsCameraHidden;
        _recordingControls.SetCameraHidden(hidden);
        _recordingEvents?.AddCamera(_recorder.Clock.Elapsed, hidden ? "off" : "on");
    }

    private void RecordingMonitor_Tick(object? sender, EventArgs e)
    {
        if (_recorder?.Failure is not Exception failure || !_recordingState.IsCapturing) return;
        _recordingFailureNote ??= $"{RecordingException.FailedTitle}: {FriendlyMessage(failure)}";
        _ = StopRecordingAsync();
    }

    private async Task StopRecordingAsync()
    {
        if (_recorder is not ScreenRecorder recorder || !_recordingState.TryMove(RecordingState.Stopping)) return;
        TearDownLiveRecordingUi();
        SetStatus("Finishing recording…", persistent: true);
        try
        {
            RecordingOutcome outcome = await recorder.StopAsync();
            RecordingEntry entry = FinalizeRecording(outcome);
            _recordingState.TryMove(RecordingState.Completed);
            RestoreHiddenWindows();
            PresentRecording(entry, outcome.Warning);
        }
        catch (RecordingException ex)
        {
            _recordingState.TryMove(RecordingState.Failed);
            RestoreHiddenWindows();
            ShowRecordingError(ex);
        }
        catch (Exception ex)
        {
            _recordingState.TryMove(RecordingState.Failed);
            RestoreHiddenWindows();
            ShowRecordingError(new RecordingException(RecordingProblem.CouldNotFinish, $"{RecordingException.CouldNotFinishTitle}: {FriendlyMessage(ex)}", ex));
        }
        finally
        {
            _recorder = null;
            _recordingTarget = null;
        }
    }

    /// <summary>Moves the finished movie (and camera sidecar) into the save folder and writes the event log.</summary>
    private RecordingEntry FinalizeRecording(RecordingOutcome outcome)
    {
        CaptureSettings settings = _recordingSettings;
        string folder = CurrentSettings.EffectiveSaveFolder;
        string finalPath;
        string? cameraPath = null;
        try
        {
            Directory.CreateDirectory(folder);
            string fileName = FileNameTemplate.BuildFileName(settings.RecordingFileNamePattern, _recordingStartedLocal, CurrentSettings.FileNameCounter, ".mp4");
            finalPath = FileNameTemplate.ResolveUniquePath(folder, fileName, File.Exists);
            File.Move(outcome.MoviePath, finalPath);
        }
        catch (Exception ex)
        {
            throw new RecordingException(RecordingProblem.CouldNotFinish,
                $"{RecordingException.CouldNotFinishTitle}: it could not be moved to {folder} ({FriendlyMessage(ex)}). The movie was kept at {outcome.MoviePath}.", ex);
        }
        if (FileNameTemplate.UsesCounter(settings.RecordingFileNamePattern))
        {
            try { UpdateSettings(current => current with { FileNameCounter = current.FileNameCounter + 1 }); }
            catch (Exception) { } // The recording is saved; a stale counter only affects the next suggested name.
        }
        if (outcome.CameraPath is string cameraTemp && File.Exists(cameraTemp))
        {
            string stem = Path.GetFileNameWithoutExtension(finalPath);
            try
            {
                cameraPath = FileNameTemplate.ResolveUniquePath(folder, stem + "-camera.mp4", File.Exists);
                File.Move(cameraTemp, cameraPath);
            }
            catch (Exception) { cameraPath = cameraTemp; } // Keep the sidecar where it is; the entry still points at it.
        }
        try { _recordingEvents?.WriteSidecar(finalPath); }
        catch (Exception) { } // Click/key events are an editing aid; the movie itself is complete.
        return new RecordingEntry(Guid.NewGuid(), finalPath, cameraPath, _recordingStartedLocal.ToUniversalTime(), outcome.Duration.TotalSeconds, outcome.Width, outcome.Height);
    }

    private void PresentRecording(RecordingEntry entry, string? warning)
    {
        try { _recordingLibrary?.Add(entry); }
        catch (Exception ex) { warning ??= $"It is saved, but the library list could not be updated: {FriendlyMessage(ex)}"; }
        var item = new RecordingLibraryItem(entry);
        _recordingItems.Insert(0, item);
        UpdateRecordingsPanel();
        if (CurrentSettings.ShowThumbnail)
        {
            RecordingThumbnailWindow thumbnail = RecordingThumbnailWindow.Present(entry);
            thumbnail.DeleteRequested += (_, _) => DeleteRecording(item);
        }
        if (_recordingSettings.ReviewAfterRecording) TryOpenRecording(entry.MoviePath);
        string name = Path.GetFileName(entry.MoviePath);
        string summary = $"Saved {name} ({RecordingFiles.FormatDuration(entry.DurationSeconds)}).";
        string? problem = _recordingFailureNote ?? warning;
        SetStatus(problem is null ? summary : $"{summary} {problem}", error: problem is not null, persistent: problem is not null);
        _recordingFailureNote = null;
    }

    private void ShowRecordingControls(RecordingTarget target, ScreenRecorder recorder)
    {
        var controls = new RecordingControlsWindow(() => recorder.Clock.Elapsed, recorder.HasMicrophone, recorder.HasCamera);
        controls.StopRequested += (_, _) => _ = StopRecordingAsync();
        controls.PauseToggled += (_, _) => _ = TogglePauseAsync();
        controls.MicrophoneToggled += (_, _) => ToggleMicrophoneMute();
        controls.CameraToggled += (_, _) => ToggleCameraVisibility();
        controls.Show();
        Forms.Screen screen = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(target.MonitorBounds.X, target.MonitorBounds.Y, target.MonitorBounds.Width, target.MonitorBounds.Height));
        System.Drawing.Rectangle work = screen.WorkingArea;
        controls.PlaceNear(target.ScreenArea, new Int32Rect(work.X, work.Y, work.Width, work.Height));
        _recordingControls = controls;
    }

    private void ShowDimOverlays(RecordingTarget target)
    {
        if (target.Kind != RecordingTargetKind.Region) return;
        foreach (Forms.Screen screen in Forms.Screen.AllScreens)
        {
            var bounds = new Int32Rect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
            var dim = new RecordingDimOverlay(target.ScreenArea);
            dim.ShowCovering(bounds);
            _recordingOverlays.Add(dim);
        }
    }

    /// <summary>Clicks are always logged for the editor; keystrokes only when "Show keystrokes" is on (privacy).</summary>
    private void StartInputHighlights(RecordingTarget target, CaptureSettings settings)
    {
        bool drawOverlay = target.Kind != RecordingTargetKind.Window && (settings.HighlightClicks || settings.ShowKeystrokes);
        if (drawOverlay)
        {
            _inputOverlay = new RecordingInputOverlay();
            _inputOverlay.ShowCovering(target.ScreenArea);
            _recordingOverlays.Add(_inputOverlay);
        }
        _inputHook = new InputActivityHook(
            (x, y) => Dispatcher.BeginInvoke(() => OnRecordingClick(x, y, settings.HighlightClicks)),
            combo => Dispatcher.BeginInvoke(() => OnRecordingKeyCombo(combo)));
        if (!_inputHook.Install(clicks: true, keys: settings.ShowKeystrokes))
            SetStatus("Recording, but click or keystroke highlights are unavailable.", error: true, persistent: true);
    }

    private void OnRecordingClick(int screenX, int screenY, bool highlight)
    {
        if (_recorder is null || _recordingTarget is null || _recordingState.State != RecordingState.Recording) return;
        Int32Rect area = _recordingTarget.Kind == RecordingTargetKind.Window
            ? RecordingTargetResolver.WindowBounds(_recordingTarget.Window) ?? _recordingTarget.ScreenArea
            : _recordingTarget.ScreenArea;
        if (RecordingGeometry.ToVideoPixels(screenX, screenY, area) is not Point video) return;
        _recordingEvents?.AddClick(_recorder.Clock.Elapsed, video.X, video.Y);
        if (highlight) _inputOverlay?.ShowClick(screenX, screenY);
    }

    private void OnRecordingKeyCombo(string combo)
    {
        if (_recorder is null || _recordingState.State != RecordingState.Recording) return;
        _recordingEvents?.AddKey(_recorder.Clock.Elapsed, combo);
        _inputOverlay?.ShowKeystroke(combo);
    }

    private void TearDownLiveRecordingUi()
    {
        _recordingMonitor.Stop();
        _recordingCountdown?.Cancel();
        _inputHook?.Dispose();
        _inputHook = null;
        _recordingControls?.Close();
        _recordingControls = null;
        foreach (Window overlay in _recordingOverlays) overlay.Close();
        _recordingOverlays.Clear();
        _inputOverlay = null;
    }

    private void RestoreHiddenWindows()
    {
        if (_recordingHiddenWindows is not CaptureVisibilitySnapshot snapshot) return;
        _recordingHiddenWindows = null;
        _presentation.RestoreAfterCapture(snapshot, this);
    }

    private void ShowRecordingError(RecordingException error)
    {
        SetStatus(error.Message, error: true, persistent: true);
        if (error.PrivacySettingsUri is string uri)
        {
            MessageBoxResult answer = MessageBox.Show($"{error.Message}\n\nOpen Windows privacy settings to allow access?", error.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) TryOpenRecording(uri);
            return;
        }
        MessageBox.Show(error.Message, error.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void UpdateRecordButton()
    {
        bool live = _recordingState.IsCapturing;
        RecordButtonText.Text = live ? "Stop" : "Record";
        RecordButtonIcon.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        RecordStopSquare.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        RecordModeButton.IsEnabled = !_recordingState.IsBusy;
        RefreshRecordButtonToolTip();
    }

    /// <summary>At narrow widths the Record button shows only its icon so the capture row never clips.</summary>
    private void SetRecordButtonCompact(bool compact)
    {
        RecordButtonText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RecordButton.MinWidth = compact ? 0 : 104;
        RecordButton.Padding = compact ? new Thickness(12, 6, 4, 6) : new Thickness(14, 6, 14, 6);
    }

    private void RefreshRecordButtonToolTip()
    {
        string text = _recordingState.IsCapturing ? "Stop recording" : "Pick a region, then record it";
        RecordButton.ToolTip = WithShortcut(text, CurrentSettings.RecordHotkey);
    }

    private void UpdateRecordingsPanel() =>
        RecordingsPanel.Visibility = _recordingItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void CleanupRecording()
    {
        TearDownLiveRecordingUi();
        RecordingThumbnailWindow.CloseAll();
        if (_recorder is ScreenRecorder recorder && _recordingState.TryMove(RecordingState.Stopping))
        {
            // Exiting mid-recording: finish the file synchronously so it stays playable; it remains in the in-progress folder.
            // Run off the dispatcher so StopAsync's continuations cannot deadlock against this blocked UI thread.
            try { Task.Run(recorder.StopAsync).Wait(TimeSpan.FromSeconds(15)); }
            catch (Exception) { } // Nothing more can be done during shutdown; the temp file is reported on next launch.
        }
    }

    // ---------- Library strip ----------

    private static RecordingLibraryItem? ItemFrom(object sender) => (sender as FrameworkElement)?.Tag as RecordingLibraryItem ?? (sender as FrameworkElement)?.DataContext as RecordingLibraryItem;

    private void PlayRecording_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemFrom(sender) is { } item) TryOpenRecording(item.Entry.MoviePath);
    }

    private void ShowRecordingInFolder_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemFrom(sender) is not { } item) return;
        try { RecordingFiles.ShowInFolder(item.Entry.MoviePath); }
        catch (Exception ex) { SetStatus($"Could not open the folder: {FriendlyMessage(ex)}", true); }
    }

    private void DeleteRecording_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemFrom(sender) is { } item) DeleteRecording(item);
    }

    private void RecordingsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecordingsList.SelectedItem is RecordingLibraryItem item) TryOpenRecording(item.Entry.MoviePath);
    }

    private void RecordingsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (RecordingsList.SelectedItem is not RecordingLibraryItem item) return;
        if (e.Key == Key.Delete) { e.Handled = true; DeleteRecording(item); }
        else if (e.Key == Key.Enter) { e.Handled = true; TryOpenRecording(item.Entry.MoviePath); }
    }

    private void RecordingCard_MouseDown(object sender, MouseButtonEventArgs e) => _recordingCardPointerDown = e.GetPosition(this);

    private void RecordingCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || ItemFrom(sender) is not { } item) return;
        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _recordingCardPointerDown.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _recordingCardPointerDown.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!File.Exists(item.Entry.MoviePath)) return;
        try { DragDrop.DoDragDrop((DependencyObject)sender, RecordingFiles.CreateDragData(item.Entry.MoviePath), DragDropEffects.Copy); }
        catch (Exception ex) { SetStatus($"Could not drag the recording: {FriendlyMessage(ex)}", true); }
    }

    private void DeleteRecording(RecordingLibraryItem item)
    {
        string name = Path.GetFileName(item.Entry.MoviePath);
        if (MessageBox.Show($"Move \"{name}\" to the Recycle Bin?", "Delete recording", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { RecordingFiles.DeleteToRecycleBin(item.Entry); }
        catch (Exception ex) { SetStatus($"Could not delete {name}: {FriendlyMessage(ex)}", true); return; }
        try { _recordingLibrary?.Remove(item.Entry.Id); }
        catch (Exception ex) { SetStatus($"Deleted {name}, but the library list could not be updated: {FriendlyMessage(ex)}", true); }
        RecordingThumbnailWindow.CloseFor(item.Entry.Id);
        _recordingItems.Remove(item);
        UpdateRecordingsPanel();
        SetStatus($"Moved {name} to the Recycle Bin.");
    }

    private void TryOpenRecording(string pathOrUri)
    {
        try { RecordingFiles.Open(pathOrUri); }
        catch (Exception ex) { SetStatus($"Could not open {pathOrUri}: {FriendlyMessage(ex)}", true); }
    }

    // ---------- Settings > Recording ----------

    private void LoadRecordingSettingsIntoControls()
    {
        CaptureSettings settings = CurrentSettings;
        bool previous = _updatingSettings;
        _updatingSettings = true;
        try
        {
            SelectTagged(RecordingCountdownComboBox, settings.RecordingCountdownSeconds.ToString(CultureInfo.InvariantCulture));
            SelectTagged(RecordingFrameRateComboBox, settings.RecordingFrameRate.ToString(CultureInfo.InvariantCulture));
            RecordMicrophoneCheckBox.IsChecked = settings.RecordMicrophone;
            RecordSystemAudioCheckBox.IsChecked = settings.RecordSystemAudio;
            RecordCameraCheckBox.IsChecked = settings.RecordCamera;
            HighlightClicksCheckBox.IsChecked = settings.HighlightClicks;
            ShowKeystrokesCheckBox.IsChecked = settings.ShowKeystrokes;
            ReviewAfterRecordingCheckBox.IsChecked = settings.ReviewAfterRecording;
            if (!RecordingFileNamePatternBox.IsKeyboardFocusWithin) RecordingFileNamePatternBox.Text = settings.RecordingFileNamePattern;
            FillDevicePicker(MicrophoneComboBox, "Default microphone", settings.MicrophoneDeviceId, _microphones);
            FillDevicePicker(CameraComboBox, "Default camera", settings.CameraDeviceId, _cameras);
        }
        finally { _updatingSettings = previous; }
        MicrophoneComboBox.IsEnabled = settings.RecordMicrophone;
        CameraComboBox.IsEnabled = settings.RecordCamera;
        UpdateRecordingFileNamePreview();
        if (SettingsPanel.Visibility == Visibility.Visible) EnsureRecordingDevicesLoaded();
    }

    /// <summary>Called when Settings opens; device enumeration is deferred until then.</summary>
    private void EnsureRecordingDevicesLoaded()
    {
        if (!_recordingDevicesLoaded) _ = LoadRecordingDevicesAsync();
    }

    private IReadOnlyList<RecordingDevice> _microphones = [];
    private IReadOnlyList<RecordingDevice> _cameras = [];

    /// <summary>Enumerates microphones and cameras off the UI thread the first time Settings is shown.</summary>
    private async Task LoadRecordingDevicesAsync()
    {
        _recordingDevicesLoaded = true;
        try { _microphones = await Task.Run(RecordingDevices.Microphones); }
        catch (Exception) { _microphones = []; } // Audio service unavailable; the default device choice still works.
        try { _cameras = await RecordingDevices.CamerasAsync(); }
        catch (Exception) { _cameras = []; }
        LoadRecordingSettingsIntoControls();
    }

    private static void FillDevicePicker(ComboBox picker, string defaultLabel, string savedId, IReadOnlyList<RecordingDevice> devices)
    {
        picker.Items.Clear();
        picker.Items.Add(new ComboBoxItem { Content = defaultLabel, Tag = string.Empty });
        foreach (RecordingDevice device in devices) picker.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id, ToolTip = device.Name });
        if (!string.IsNullOrEmpty(savedId) && devices.All(device => device.Id != savedId))
            picker.Items.Add(new ComboBoxItem { Content = "Saved device (not connected)", Tag = savedId });
        SelectTagged(picker, savedId);
    }

    private void RecordingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _updatingSettings) return;
        bool microphone = RecordMicrophoneCheckBox.IsChecked == true, systemAudio = RecordSystemAudioCheckBox.IsChecked == true;
        bool camera = RecordCameraCheckBox.IsChecked == true, clicks = HighlightClicksCheckBox.IsChecked == true;
        bool keys = ShowKeystrokesCheckBox.IsChecked == true, review = ReviewAfterRecordingCheckBox.IsChecked == true;
        TrySaveSettings(settings => settings with
        {
            RecordMicrophone = microphone,
            RecordSystemAudio = systemAudio,
            RecordCamera = camera,
            HighlightClicks = clicks,
            ShowKeystrokes = keys,
            ReviewAfterRecording = review
        }, "the recording options");
    }

    private void RecordingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _updatingSettings) return;
        if (!TryParseSelectedTag(RecordingCountdownComboBox, out int countdown) || !TryParseSelectedTag(RecordingFrameRateComboBox, out int frameRate)) return;
        TrySaveSettings(settings => settings with { RecordingCountdownSeconds = countdown, RecordingFrameRate = frameRate }, "the recording options");
    }

    private void RecordingDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _updatingSettings) return;
        string microphone = SelectedTag(MicrophoneComboBox) ?? string.Empty, camera = SelectedTag(CameraComboBox) ?? string.Empty;
        TrySaveSettings(settings => settings with { MicrophoneDeviceId = microphone, CameraDeviceId = camera }, "the recording devices");
    }

    private void RecordingFileNamePattern_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) UpdateRecordingFileNamePreview();
    }

    private void RecordingFileNamePattern_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitRecordingFileNamePattern();

    private void RecordingFileNamePattern_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitRecordingFileNamePattern();
    }

    private void CommitRecordingFileNamePattern()
    {
        if (!IsInitialized || _updatingSettings) return;
        string pattern = RecordingFileNamePatternBox.Text.Trim();
        if (pattern.Length == 0) pattern = CaptureSettings.DefaultRecordingFileNamePattern;
        if (pattern == CurrentSettings.RecordingFileNamePattern) { RecordingFileNamePatternBox.Text = pattern; return; }
        TrySaveSettings(settings => settings with { RecordingFileNamePattern = pattern }, "the recording file name");
        RecordingFileNamePatternBox.Text = CurrentSettings.RecordingFileNamePattern;
    }

    private void UpdateRecordingFileNamePreview()
    {
        string name = FileNameTemplate.BuildFileName(RecordingFileNamePatternBox.Text, DateTime.Now, CurrentSettings.FileNameCounter, ".mp4");
        RecordingFileNamePreviewText.Text = "Preview: " + name;
        RecordingFileNamePreviewText.ToolTip = name;
    }
}

/// <summary>A recording card in the library strip; the shell thumbnail loads asynchronously.</summary>
internal sealed class RecordingLibraryItem : INotifyPropertyChanged
{
    internal RecordingLibraryItem(RecordingEntry entry)
    {
        Entry = entry;
        _ = LoadThumbnailAsync();
    }

    public RecordingEntry Entry { get; }
    public BitmapSource? Thumbnail { get; private set; }
    public string DurationText => RecordingFiles.FormatDuration(Entry.DurationSeconds);
    public string Label
    {
        get
        {
            DateTime local = Entry.CreatedUtc.ToLocalTime();
            return local.Date == DateTime.Today ? local.ToString("h:mm tt", CultureInfo.CurrentCulture) : local.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture);
        }
    }
    public string Details => $"{Path.GetFileName(Entry.MoviePath)} · {DurationText} · {Entry.Width:N0} × {Entry.Height:N0}";
    public event PropertyChangedEventHandler? PropertyChanged;

    private async Task LoadThumbnailAsync()
    {
        Thumbnail = await RecordingFiles.LoadThumbnailAsync(Entry.MoviePath, 240);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
    }
}
