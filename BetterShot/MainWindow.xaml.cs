using BetterShot.Capture;
using BetterShot.Editing;
using BetterShot.Platform;
using BetterShot.Persistence;
using BetterShot.Presentation;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using CaptureMode = BetterShot.Capture.CaptureMode;

namespace BetterShot;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty RecentCardWidthProperty = DependencyProperty.Register(nameof(RecentCardWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(240d));
    public static readonly DependencyProperty RecentImageHeightProperty = DependencyProperty.Register(nameof(RecentImageHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(125d));
    public double RecentCardWidth { get => (double)GetValue(RecentCardWidthProperty); private set => SetValue(RecentCardWidthProperty, value); }
    public double RecentImageHeight { get => (double)GetValue(RecentImageHeightProperty); private set => SetValue(RecentImageHeightProperty, value); }
    private readonly CaptureService _captureService = new();
    private readonly KeyboardShortcutHook _shortcutHook;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly System.Drawing.Icon _applicationIcon;
    private readonly CapturePresentationManager _presentation = new();
    private readonly CaptureHistoryStore? _historyStore;
    private readonly Dictionary<CaptureDocument, EventHandler> _documentHandlers = new();
    private readonly DispatcherTimer _retentionTimer;
    private readonly ObservableCollection<RecentCaptureItem> _recentItems = new();
    private readonly DispatcherTimer _statusResetTimer;
    private HwndSource? _source;
    private CaptureDocument? _latest;
    private bool _exitRequested;
    private bool _capturing;
    private bool _updatingShortcut;
    private bool _updatingStartup;
    private readonly WindowsStartupRegistration? _startupRegistration;

    internal CapturePresentationManager Presentation => _presentation;
    internal CaptureDocument? SelectedDocumentForTest => _latest;
    internal BitmapSource? PreviewSourceForTest => PreviewImage.Source as BitmapSource;
    internal bool SettingsVisibleForTest => SettingsPanel.Visibility == Visibility.Visible;
    internal bool TrayVisibleForTest => _trayIcon.Visible;
    internal bool ShortcutEnabledForTest => _shortcutHook.IsEnabled;

    public MainWindow(bool enableHistory = false, string? historyRoot = null, string? startupDirectory = null)
    {
        _statusResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _statusResetTimer.Tick += (_, _) => { _statusResetTimer.Stop(); SetStatusCore(""); };
        InitializeComponent();
        RecentList.DataContext = _recentItems;
        if (enableHistory)
        {
            try { _historyStore = new CaptureHistoryStore(historyRoot ?? CaptureHistoryStore.DefaultRoot); }
            catch (Exception ex)
            {
                _historyStore = null;
                SetStatus($"Capture history is unavailable; captures will remain available for this session: {FriendlyMessage(ex)}", true);
            }
        }
        else _historyStore = null;
        _presentation.DeleteRequested += Presentation_DeleteRequested;
        _presentation.Removed += Presentation_Removed;
        _retentionTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _retentionTimer.Tick += (_, _) => PurgeExpired();
        if (_historyStore is not null)
        {
            SelectRetentionChoice(_historyStore.RetentionDays);
            try
            {
                foreach (CaptureDocument document in _historyStore.Load(DateTime.UtcNow)) AddRecentDocument(document, false);
                if (_recentItems.Count > 0) SelectRecent(_recentItems[0]);
                if (_historyStore.LastLoadErrors.Count > 0) SetStatus($"Skipped {_historyStore.LastLoadErrors.Count} capture{(_historyStore.LastLoadErrors.Count == 1 ? "" : "s")} that could not be loaded.", true);
            }
            catch (Exception ex) { SetStatus($"Could not load capture history: {FriendlyMessage(ex)}", true); }
            _retentionTimer.Start();
        }
        else SelectRetentionChoice(7);
        _presentation.DragAsFile = _historyStore?.DragAsFile ?? false;
        DragModeComboBox.SelectedIndex = _presentation.DragAsFile ? 1 : 0;
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"BetterShot {version?.ToString(3) ?? "1.0.0"}";
        _shortcutHook = new KeyboardShortcutHook(() => Dispatcher.BeginInvoke(() => _ = CaptureAsync(CaptureMode.Area)));
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Capture area", null, (_, _) => Dispatcher.BeginInvoke(() => _ = CaptureAsync(CaptureMode.Area)));
        menu.Items.Add("Capture window", null, (_, _) => Dispatcher.BeginInvoke(() => _ = CaptureAsync(CaptureMode.Window)));
        menu.Items.Add("Capture display", null, (_, _) => Dispatcher.BeginInvoke(() => _ = CaptureAsync(CaptureMode.Display)));
        menu.Items.Add("Open BetterShot", null, (_, _) => Dispatcher.BeginInvoke(ShowAndActivate));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(ExitApplication));
        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/BetterShot.ico")).Stream)
        using (var loadedIcon = new System.Drawing.Icon(stream, 32, 32))
            _applicationIcon = (System.Drawing.Icon)loadedIcon.Clone();
        _trayIcon = new Forms.NotifyIcon { Icon = _applicationIcon, Text = "BetterShot", ContextMenuStrip = menu, Visible = true };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowAndActivate);
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        if (_historyStore?.ReplaceScreenshotShortcut == true) ApplyShortcut(true);
        if (enableHistory && (historyRoot is null || startupDirectory is not null))
        {
            _startupRegistration = new WindowsStartupRegistration(startupDirectory);
            RestoreWindowsStartup();
        }
    }


    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double contentWidth = Math.Max(0, ActualWidth - 72);
        RecentCardWidth = Math.Clamp(Math.Floor((contentWidth - 72) / 4), 170, 300);
        RecentImageHeight = Math.Clamp(RecentCardWidth * 0.52, 100, 130);
    }
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source.AddHook(WndProc);
        FitInitialWindowToWorkArea();
        if (!GlobalHotKey.Register(_source.Handle)) SetStatus("Ctrl+Shift+F9 is unavailable; use Capture or the tray menu.", true);
    }

    private void FitInitialWindowToWorkArea()
    {
        if (_source?.CompositionTarget is not { } target) return;
        var area = Forms.Screen.FromHandle(_source.Handle).WorkingArea;
        double scaleX = target.TransformToDevice.M11, scaleY = target.TransformToDevice.M22;
        double availableWidth = area.Width / scaleX - 24;
        double availableHeight = area.Height / scaleY - 24;
        if (Width <= availableWidth && Height <= availableHeight) return;
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left / scaleX + (area.Width / scaleX - Width) / 2;
        Top = area.Top / scaleY + (area.Height / scaleY - Height) / 2;
    }
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == GlobalHotKey.WmHotKey && wParam.ToInt32() == GlobalHotKey.Id) { handled = true; _ = CaptureAsync(CaptureMode.Area); }
        return IntPtr.Zero;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Settings_Click(object sender, RoutedEventArgs e) => SetSettingsVisible(SettingsPanel.Visibility != Visibility.Visible);
    private void SettingsClose_Click(object sender, RoutedEventArgs e) => SetSettingsVisible(false);
    private void SetSettingsVisible(bool visible)
    {
        SettingsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) ReplaceShortcutCheckBox.Focus(); else SettingsButton.Focus();
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsPanel.Visibility == Visibility.Visible) { SetSettingsVisible(false); e.Handled = true; }
    }

    private void Licenses_Click(object sender, RoutedEventArgs e)
    {
        if (LicensePanel.Visibility == Visibility.Visible) { LicensePanel.Visibility = Visibility.Collapsed; return; }
        if (string.IsNullOrWhiteSpace(LicenseText.Text))
        {
            string path = Path.Combine(AppContext.BaseDirectory, "ThirdPartyNotices", "Lucide-LICENSE.txt");
            try { LicenseText.Text = File.ReadAllText(path); }
            catch { LicenseText.Text = "Lucide icons · ISC License\n\nThe license file is included with distributed BetterShot builds."; }
        }
        LicensePanel.Visibility = Visibility.Visible;
    }

    private void Area_Click(object sender, RoutedEventArgs e) => _ = CaptureAsync(CaptureMode.Area);
    private void Window_Click(object sender, RoutedEventArgs e) => _ = CaptureAsync(CaptureMode.Window);
    private void Display_Click(object sender, RoutedEventArgs e) => _ = CaptureAsync(CaptureMode.Display);
    private void OpenRecent_Click(object sender, RoutedEventArgs e) { if (_latest is not null) _presentation.OpenEditor(_latest); }
    private void PreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { if (_latest is not null) _presentation.OpenEditor(_latest); }

    private void RecentList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentCaptureItem item) return;
        CaptureHeading.Text = RecentList.SelectedIndex == 0 ? "Latest capture" : "Selected capture";
        _latest = item.Document;
        PreviewImage.Source = item.Thumbnail;
        SelectedTimeText.Text = item.Timestamp.Date == DateTime.Today ? $"Today, {item.Timestamp:h:mm tt}" : item.Timestamp.ToString("MMM d, h:mm tt");
        SelectedMetaText.Text = $"{item.Document.Original.PixelWidth:N0} × {item.Document.Original.PixelHeight:N0} · PNG";
    }

    private async void CopyRecent_Click(object sender, RoutedEventArgs e)
    {
        CaptureDocument? document = _latest;
        if (document is null) return;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try { Clipboard.SetImage(document.Render()); SetStatus("Copied rendered capture."); return; }
            catch (COMException) when (attempt < 5) { await Task.Delay(attempt * 70); }
            catch (Exception ex) { SetStatus($"Could not copy: {FriendlyMessage(ex)}", true); return; }
        }
        SetStatus("Clipboard is busy. Please try again.", true);
    }

    private void SaveRecent_Click(object sender, RoutedEventArgs e)
    {
        CaptureDocument? document = _latest;
        if (document is null) return;
        var dialog = new SaveFileDialog { Title = "Save capture", Filter = "PNG image (*.png)|*.png", DefaultExt = ".png", AddExtension = true, FileName = $"BetterShot-{DateTime.Now:yyyy-MM-dd-HHmmss}.png" };
        if (dialog.ShowDialog(this) != true) return;
        string path = Path.GetFullPath(dialog.FileName);
        string temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(document.Render()));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(stream);
            File.Move(temp, path, true); SetStatus($"Saved {Path.GetFileName(path)}.");
        }
        catch (Exception ex) { SetStatus($"Could not save: {FriendlyMessage(ex)}", true); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private async Task CaptureAsync(CaptureMode mode)
    {
        if (_capturing) return;
        _capturing = true; SetCaptureControls(false); SetStatus($"Capturing {mode.ToString().ToLowerInvariant()}…", persistent: true);
        CaptureVisibilitySnapshot snapshot = _presentation.HideForCapture();
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(180);
            BitmapSource? image = await _captureService.CaptureAsync(mode);
            if (image is null) { _presentation.RestoreAfterCapture(snapshot); SetStatus("Capture canceled."); return; }
            _presentation.RestoreAfterCapture(snapshot, this);
            await CompleteCaptureAsync(image);
        }
        catch (Exception ex)
        {
            _presentation.RestoreAfterCapture(snapshot); SetStatus($"Capture failed: {FriendlyMessage(ex)}", true); ShowAndActivate();
        }
        finally { _capturing = false; SetCaptureControls(true); }
    }

    internal async Task<CaptureThumbnailWindow> CompleteCaptureAsync(BitmapSource image, Action<BitmapSource>? writeClipboard = null)
    {
        writeClipboard ??= Clipboard.SetImage;
        string? copyError = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try { writeClipboard(image); break; }
            catch (COMException) when (attempt < 5) { await Task.Delay(attempt * 60); }
            catch (Exception ex) { copyError = FriendlyMessage(ex); break; }
        }
        CaptureThumbnailWindow thumbnail = PresentCapturedImage(image);
        if (copyError is null) SetStatus($"Captured {image.PixelWidth:N0} × {image.PixelHeight:N0} and copied to clipboard.");
        else SetStatus($"Captured, but could not copy to clipboard: {copyError}. Use Copy on the thumbnail to retry.", error: true, persistent: true);
        return thumbnail;
    }

    internal CaptureThumbnailWindow PresentCapturedImage(BitmapSource image)
    {
        if (!image.IsFrozen && image.CanFreeze) image.Freeze();
        return PresentDocument(new CaptureDocument(image));
    }

    internal CaptureThumbnailWindow PresentDocument(CaptureDocument document)
    {
        AddRecentDocument(document, true);
        CaptureThumbnailWindow thumbnail = _presentation.Present(document);
        SelectRecent(_recentItems.First(item => ReferenceEquals(item.Document, document)));
        try { _historyStore?.Save(document); }
        catch (Exception ex) { SetStatus($"Capture is available, but could not be stored: {FriendlyMessage(ex)}", true); }
        PurgeExpired();
        return thumbnail;
    }

    private void AddRecentDocument(CaptureDocument document, bool insertFirst)
    {
        var item = new RecentCaptureItem(document);
        if (insertFirst) _recentItems.Insert(0, item); else _recentItems.Add(item);
        EventHandler handler = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!_documentHandlers.ContainsKey(document)) return;
            item.Refresh();
            if (ReferenceEquals(_latest, document)) PreviewImage.Source = item.Thumbnail;
            try { _historyStore?.Save(document); }
            catch (Exception ex) { SetStatus($"Changes are available, but could not be stored: {FriendlyMessage(ex)}", true); }
        });
        _documentHandlers[document] = handler;
        document.Changed += handler;
        if (!insertFirst) _presentation.Retain(document);
        EmptyState.Visibility = Visibility.Collapsed;
        RecentState.Visibility = Visibility.Visible;
    }

    private void SelectRecent(RecentCaptureItem item)
    {
        RecentList.SelectedItem = item;
        RecentList.ScrollIntoView(item);
    }

    private void DetachDocument(CaptureDocument document)
    {
        if (!_documentHandlers.Remove(document, out EventHandler? handler)) return;
        document.Changed -= handler;
    }

    private void Presentation_DeleteRequested(object? sender, CaptureDocumentEventArgs e) => DeleteDocument(e.Document);

    private void DeleteDocument(CaptureDocument document)
    {
        _presentation.CloseEditor(document);
        DetachDocument(document);
        try { _historyStore?.Delete(document.Id); }
        catch (Exception ex)
        {
            RecentCaptureItem? item = _recentItems.FirstOrDefault(candidate => ReferenceEquals(candidate.Document, document));
            if (item is not null) AttachDocument(document, item);
            SetStatus($"Could not delete capture: {FriendlyMessage(ex)}", true);
            return;
        }
        _presentation.Delete(document);
        SetStatus("Capture deleted permanently.");
    }

    private void AttachDocument(CaptureDocument document, RecentCaptureItem item)
    {
        if (_documentHandlers.ContainsKey(document)) return;
        EventHandler handler = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!_documentHandlers.ContainsKey(document)) return;
            item.Refresh();
            if (ReferenceEquals(_latest, document)) PreviewImage.Source = item.Thumbnail;
            try { _historyStore?.Save(document); }
            catch (Exception ex) { SetStatus($"Changes are available, but could not be stored: {FriendlyMessage(ex)}", true); }
        });
        _documentHandlers[document] = handler;
        document.Changed += handler;
    }

    private void Presentation_Removed(object? sender, CaptureDocumentEventArgs e)
    {
        DetachDocument(e.Document);
        RecentCaptureItem? item = _recentItems.FirstOrDefault(candidate => ReferenceEquals(candidate.Document, e.Document));
        if (item is not null) _recentItems.Remove(item);
        if (_recentItems.Count == 0)
        {
            _latest = null;
            PreviewImage.Source = null;
            RecentState.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
        }
        else SelectRecent(_recentItems[0]);
    }

    private void DeleteRecent_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: CaptureDocument document }) DeleteDocument(document);
    }

    private void RecentList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || RecentList.SelectedItem is not RecentCaptureItem item) return;
        e.Handled = true;
        DeleteDocument(item.Document);
    }

    private void DragMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || DragModeComboBox.SelectedItem is not ComboBoxItem item) return;
        bool asFile = item.Tag?.ToString() == "file";
        bool previous = _presentation.DragAsFile;
        try
        {
            _historyStore?.SetDragAsFile(asFile);
            _presentation.DragAsFile = asFile;
        }
        catch (Exception ex)
        {
            _presentation.DragAsFile = previous;
            DragModeComboBox.SelectedIndex = previous ? 1 : 0;
            SetStatus($"Could not save drag preference: {FriendlyMessage(ex)}", error: true, persistent: true);
        }
    }

    private void RetentionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _historyStore is null || RetentionComboBox.SelectedItem is not ComboBoxItem item || !int.TryParse(item.Tag?.ToString(), out int days)) return;
        try
        {
            _historyStore.SetRetentionDays(days);
            if (PurgeExpired()) SetStatus($"Capture history will be kept for {days} day{(days == 1 ? "" : "s")}.");
        }
        catch (Exception ex)
        {
            SelectRetentionChoice(_historyStore.RetentionDays);
            SetStatus($"Could not update capture retention: {FriendlyMessage(ex)}", true);
        }
    }

    private void SelectRetentionChoice(int days)
    {
        RetentionComboBox.SelectedItem = RetentionComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == days.ToString());
    }

    private bool PurgeExpired()
    {
        if (_historyStore is null || _capturing) return true;
        DateTime utcNow = DateTime.UtcNow;
        CaptureDocument[] candidates = _presentation.Recent
            .Where(document => utcNow - document.CreatedUtc >= TimeSpan.FromDays(_historyStore.RetentionDays)).ToArray();
        foreach (CaptureDocument document in candidates)
        {
            _presentation.CloseEditor(document);
            DetachDocument(document);
        }
        try
        {
            IReadOnlyList<Guid> expired = _historyStore.PurgeExpired(utcNow);
            if (_historyStore.LastLoadErrors.Count > 0)
            {
                SetStatus($"Could not delete {_historyStore.LastLoadErrors.Count} expired capture{(_historyStore.LastLoadErrors.Count == 1 ? "" : "s")}.", true);
                foreach (CaptureDocument document in candidates)
                    if (!expired.Contains(document.Id) && _recentItems.FirstOrDefault(item => ReferenceEquals(item.Document, document)) is { } item) AttachDocument(document, item);
                foreach (CaptureDocument document in candidates.Where(document => expired.Contains(document.Id))) _presentation.Delete(document);
                return false;
            }
            foreach (CaptureDocument document in candidates)
            {
                if (expired.Contains(document.Id)) _presentation.Delete(document);
                else if (_recentItems.FirstOrDefault(item => ReferenceEquals(item.Document, document)) is { } item) AttachDocument(document, item);
            }
        }
        catch (Exception ex)
        {
            foreach (CaptureDocument document in candidates)
                if (_recentItems.FirstOrDefault(item => ReferenceEquals(item.Document, document)) is { } item) AttachDocument(document, item);
            SetStatus($"Could not clean up capture history: {FriendlyMessage(ex)}", true);
            return false;
        }
        return true;
    }
    internal void InitializeInTray() => new WindowInteropHelper(this).EnsureHandle();

    private void RestoreWindowsStartup()
    {
        if (_startupRegistration is null || _historyStore is null) return;
        try
        {
            if (_historyStore.StartWithWindows) _startupRegistration.EnsureEnabled();
            else _startupRegistration.Disable();
        }
        catch (Exception ex) { SetStatus($"Could not update Windows startup: {FriendlyMessage(ex)}", error: true, persistent: true); }
        _updatingStartup = true;
        try { StartWithWindowsCheckBox.IsChecked = _historyStore.StartWithWindows; }
        finally { _updatingStartup = false; }
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _updatingStartup || _startupRegistration is null || _historyStore is null) return;
        bool enabled = StartWithWindowsCheckBox.IsChecked == true;
        bool previous = _historyStore.StartWithWindows;
        try
        {
            if (enabled) _startupRegistration.EnsureEnabled(); else _startupRegistration.Disable();
            _historyStore.SetStartWithWindows(enabled);
            SetStatus(enabled ? "BetterShot will start in your tray when you sign in." : "Automatic Windows startup is off.");
        }
        catch (Exception ex)
        {
            try { if (previous) _startupRegistration.EnsureEnabled(); else _startupRegistration.Disable(); } catch { }
            _updatingStartup = true;
            try { StartWithWindowsCheckBox.IsChecked = previous; }
            finally { _updatingStartup = false; }
            SetStatus($"Could not save Windows startup: {FriendlyMessage(ex)}", error: true, persistent: true);
        }
    }

    private void ReplaceShortcut_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _updatingShortcut || _shortcutHook is null) return;
        bool enabled = ReplaceShortcutCheckBox.IsChecked == true;
        string? saveError = null;
        try
        {
            if (_historyStore is not null) _historyStore.SetReplaceScreenshotShortcut(enabled);
            else saveError = "The choice could not be saved because settings storage is unavailable.";
        }
        catch (Exception ex) { saveError = $"The choice could not be saved: {FriendlyMessage(ex)}."; }
        bool active = ApplyShortcut(enabled);
        if (saveError is not null)
            SetStatus($"{(active ? "Win+Shift+S is enabled for now." : "Win+Shift+S is off.")} {saveError}", error: true, persistent: true);
    }

    private bool ApplyShortcut(bool enabled)
    {
        bool active = enabled && _shortcutHook.Enable();
        if (!enabled) _shortcutHook.Disable();
        _updatingShortcut = true;
        try { ReplaceShortcutCheckBox.IsChecked = active; }
        finally { _updatingShortcut = false; }
        CaptureShortcutText.Text = active ? "Win + Shift + S" : "Ctrl + Shift + F9";
        AreaButton.ToolTip = active ? "Select a region (Win + Shift + S). Fallback: Ctrl + Shift + F9" : "Select a region (Ctrl + Shift + F9)";
        if (enabled && !active)
            SetStatus("Could not enable Win+Shift+S. BetterShot will retry next launch if the choice was saved. Ctrl+Shift+F9 remains available.", error: true, persistent: true);
        else SetStatus(active ? "Win+Shift+S is enabled." : "Windows keeps Win+Shift+S.");
        return active;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested) { e.Cancel = true; Hide(); SetStatus("Still available from the notification area."); return; }
        Cleanup();
    }

    private void ExitApplication() { _exitRequested = true; Close(); Application.Current.Shutdown(); }
    internal void CloseForTest() { _exitRequested = true; Close(); }
    private void Cleanup()
    {
        _retentionTimer.Stop();
        foreach (CaptureDocument document in _presentation.Recent.ToArray()) _presentation.CloseEditor(document);
        if (_historyStore is not null)
        {
            foreach (CaptureDocument document in _presentation.Recent)
            {
                try { _historyStore.Save(document); }
                catch { }
            }
        }
        foreach (CaptureDocument document in _documentHandlers.Keys.ToArray()) DetachDocument(document);
        _presentation.DeleteRequested -= Presentation_DeleteRequested;
        _presentation.Removed -= Presentation_Removed;
        _statusResetTimer.Stop(); _shortcutHook.Dispose();
        if (_source is not null) { GlobalHotKey.Unregister(_source.Handle); _source.RemoveHook(WndProc); }
        _trayIcon.Visible = false; _trayIcon.Dispose(); _applicationIcon.Dispose(); _presentation.Dispose();
    }

    private void ShowAndActivate() { ShowActivated = true; Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); Focus(); }
    private void SetCaptureControls(bool enabled) { AreaButton.IsEnabled = enabled; WindowButton.IsEnabled = enabled; DisplayButton.IsEnabled = enabled; }
    private void SetStatus(string text, bool error = false, bool persistent = false)
    {
        SetStatusCore(text, error); _statusResetTimer.Stop(); if (!persistent) _statusResetTimer.Start();
    }
    private void SetStatusCore(string text, bool error = false)
    {
        StatusText.Text = text; StatusText.Foreground = error ? new SolidColorBrush(Color.FromRgb(255, 135, 135)) : (Brush)FindResource("MutedBrush");
    }
    private static string FriendlyMessage(Exception ex) => ex switch { UnauthorizedAccessException => "access was denied", IOException => "the file is in use or unavailable", _ => string.IsNullOrWhiteSpace(ex.Message) ? "an unexpected error occurred" : ex.Message };

    internal void PresentDocumentForTest(CaptureDocument document) => PresentDocument(document);
    internal void OpenSettingsForTest() => SetSettingsVisible(true);
    internal void ShowAndActivateForTest() => ShowAndActivate();
    internal void CloseToTrayForTest() => Close();
}

internal sealed class RecentCaptureItem : INotifyPropertyChanged
{
    internal RecentCaptureItem(CaptureDocument document) { Document = document; Timestamp = document.CreatedUtc.ToLocalTime(); Thumbnail = document.Render(); }
    public CaptureDocument Document { get; }
    internal DateTime Timestamp { get; }
    public BitmapSource Thumbnail { get; private set; }
    public string Details => $"{Timestamp:h:mm:ss tt} · {Document.Original.PixelWidth:N0} × {Document.Original.PixelHeight:N0} pixels";
    public string ShortLabel => Timestamp.ToString("h:mm tt");
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() { Thumbnail = Document.Render(); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
}



