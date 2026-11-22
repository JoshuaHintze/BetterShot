using BetterShot.Editing;
using BetterShot.Persistence;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BetterShot;

/// <summary>
/// The library and editor in one window (KhingShot layout): the Images/Videos sidebar with day groups, list or grid,
/// sort, search, and the + capture menu, beside the embedded <see cref="EditorSurface"/> for the open capture.
/// </summary>
public partial class MainWindow
{
    private const double LibraryWidth = 260;
    /// <summary>Below this window width an open editor hides the library unless the user shows it again.</summary>
    private const double LibraryAutoHideWidth = 1360;
    /// <summary>Room kept free at the top right for the Settings and caption buttons.</summary>
    private const double CaptionInset = 4 * 40 + 24;
    /// <summary>Distance from the window's right edge to the caption buttons in the library-only and editing layouts.</summary>
    private const double LibraryCaptionRight = 22, EditorCaptionRight = 14;
    private static readonly Brush SavingDot = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly Brush IdleDot = Frozen(Color.FromRgb(0x7A, 0x7C, 0x82));
    private readonly DispatcherTimer _libraryClock = new() { Interval = TimeSpan.FromMinutes(1) };
    private EditorSurface? _editor;
    private bool _libraryWanted;
    private bool _narrowLibraryWanted;
    private bool _gridMode;
    private bool _oldestFirst;
    private bool _showingVideos;
    private bool _syncingSelection;
    private DateTime _libraryDay = DateTime.Today;

    internal EditorSurface? EmbeddedEditorForTest => _editor;
    internal bool LibraryVisibleForTest => LibraryPanel.Visibility == Visibility.Visible;
    internal void OpenInEditorForTest(CaptureDocument document) => OpenInEditor(document);
    internal void CloseEditorForTest() => CloseEmbeddedEditor(commit: true);
    internal void ToggleLibraryForTest() => ToggleLibrary();
    internal void SetGridModeForTest(bool grid) => SetGridMode(grid);
    internal void ShowVideosForTest(bool videos) => ShowLibraryTab(videos);
    /// <summary>Selects a row the way a click does (an open editor follows the selection).</summary>
    internal void SelectDocumentForTest(CaptureDocument document) =>
        RecentList.SelectedItem = _recentItems.First(item => ReferenceEquals(item.Document, document));
    internal string SaveLocationForTest => SaveLocationText.Text;
    internal void SetCaptureMenuOpenForTest(bool open) => CaptureMenuPopup.IsOpen = open;
    internal IReadOnlyList<string> GroupNamesForTest => RecentListView.Groups?.OfType<CollectionViewGroup>().Select(group => group.Name?.ToString() ?? "").ToArray() ?? [];

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Called once from the constructor, after the library has loaded.</summary>
    private void InitializeLibrary()
    {
        _libraryWanted = CurrentSettings.EditorLibraryVisible;
        _narrowLibraryWanted = CurrentSettings.EditorLibraryNarrowVisible;
        RecentListView.SortDescriptions.Add(new SortDescription(nameof(RecentCaptureItem.Timestamp), ListSortDirection.Descending));
        RecentListView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RecentCaptureItem.DayGroup)));
        _presentation.EditInLibrary = document => { ShowAndActivate(); OpenInEditor(document); };
        PreviewKeyUp += (_, e) => { if (_editor is not null && e.Key == Key.Space && !_editor.IsKeyboardFocusWithin) _editor.HandleHostKeyUp(e.Key); };
        _libraryClock.Tick += (_, _) => RefreshLibraryClock();
        _libraryClock.Start();
        ShowLibraryTab(false);
        RefreshSaveLocation();
        UpdateLibraryEmptyState();
        UpdateLibraryLayout();
    }

    /// <summary>Opens a capture in the embedded editor, committing edits in the one it replaces.</summary>
    private void OpenInEditor(CaptureDocument document)
    {
        if (SettingsPanel.Visibility == Visibility.Visible) SetSettingsVisible(false);
        if (_showingVideos) ShowLibraryTab(false);
        SelectDocumentRow(document);
        if (_editor is not null && ReferenceEquals(_editor.Document, document)) { UpdateLibraryLayout(); return; }
        CloseEmbeddedEditor(commit: true, updateLayout: false);
        var editor = new EditorSurface(document, libraryControls: true) { PaintsBackdrop = false };
        editor.HeaderRightInset = CaptionInset;
        editor.DoneRequested += (_, _) => CloseEmbeddedEditor(commit: false);
        editor.BackRequested += (_, _) => CloseEmbeddedEditor(commit: false);
        editor.LibraryToggleRequested += (_, _) => ToggleLibrary();
        _editor = editor;
        EditorHost.Content = editor;
        UpdateLibraryLayout();
    }

    /// <summary>Returns to the library-only view. Done and Back have already committed pending edits.</summary>
    private void CloseEmbeddedEditor(bool commit, bool updateLayout = true)
    {
        if (_editor is null) return;
        EditorSurface editor = _editor;
        _editor = null;
        if (commit) editor.CommitPendingEdits();
        editor.Detach();
        EditorHost.Content = null;
        if (updateLayout) UpdateLibraryLayout();
    }

    /// <summary>A capture is being deleted: drop unfinished text or crop so they cannot recreate it.</summary>
    private void DiscardEmbeddedEditor(CaptureDocument document)
    {
        if (_editor is null || !ReferenceEquals(_editor.Document, document)) return;
        _editor.DiscardPendingEdits();
        CloseEmbeddedEditor(commit: false);
    }

    private void SelectDocumentRow(CaptureDocument document)
    {
        RecentCaptureItem? item = _recentItems.FirstOrDefault(candidate => ReferenceEquals(candidate.Document, document));
        if (item is null) return;
        if (!RecentListView.Contains(item) && SearchBox.Text.Length > 0) SearchBox.Clear();
        _syncingSelection = true;
        try { SelectRecent(item); }
        finally { _syncingSelection = false; }
        _latest = document;
    }

    private void ToggleLibrary()
    {
        bool visible = LibraryPanel.Visibility == Visibility.Visible;
        bool narrowEditor = _editor is not null && ActualWidth > 0 && ActualWidth < LibraryAutoHideWidth;
        if (narrowEditor)
        {
            _narrowLibraryWanted = !visible;
            if (_narrowLibraryWanted != CurrentSettings.EditorLibraryNarrowVisible)
                TrySaveSettings(settings => settings with { EditorLibraryNarrowVisible = _narrowLibraryWanted }, "the narrow editor library preference");
        }
        else
        {
            _libraryWanted = !visible;
            if (_libraryWanted != CurrentSettings.EditorLibraryVisible)
                TrySaveSettings(settings => settings with { EditorLibraryVisible = _libraryWanted }, "the editor library preference");
        }
        UpdateLibraryLayout();
    }

    /// <summary>Library only (full width), or library beside the editor, or the editor alone when narrow or hidden by the user.</summary>
    private void UpdateLibraryLayout()
    {
        bool editing = _editor is not null;
        bool roomy = ActualWidth <= 0 || ActualWidth >= LibraryAutoHideWidth;
        bool showLibrary = !editing || (roomy ? _libraryWanted : _narrowLibraryWanted);
        LibraryPanel.Visibility = showLibrary ? Visibility.Visible : Visibility.Collapsed;
        EditorHost.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        LibraryColumn.Width = !editing ? new GridLength(1, GridUnitType.Star) : showLibrary ? new GridLength(LibraryWidth) : new GridLength(0);
        EditorColumn.Width = editing ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        LibraryPanel.Margin = editing ? new Thickness(10, 10, 0, 10) : new Thickness(10);
        // While editing, the editor's backdrop runs the full width so the library card floats on it with no seam.
        RootGrid.Background = editing ? EditorSurface.Backdrop : null;
        // The caption buttons overlap the library header only while the library reaches the right edge.
        LibraryHeaderButtons.Margin = editing ? new Thickness(0) : new Thickness(0, 0, CaptionInset - 16, 0);
        const double headerButtonSize = 34;
        SortButton.Width = SortButton.Height = ViewModeButton.Width = ViewModeButton.Height = headerButtonSize;
        ViewModeButton.Margin = new Thickness(6, 0, 0, 0);
        VideosTabButton.Margin = new Thickness(14, 0, 0, 0);
        StyleTab(ImagesTabButton, !_showingVideos);
        StyleTab(VideosTabButton, _showingVideos);
        if (_editor is not null) _editor.LibraryVisible = showLibrary;
        Dispatcher.BeginInvoke(AlignCaptionButtons, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Centres the Settings/minimize/maximize/close buttons on the visible header row (library or editor) so they sit
    /// inside the card beside the header buttons instead of floating on the window's top edge.
    /// </summary>
    private void AlignCaptionButtons()
    {
        FrameworkElement? row = _editor is not null ? _editor.HeaderRightForTest : LibraryPanel.IsVisible ? LibraryHeaderButtons : null;
        double right = _editor is not null ? EditorCaptionRight : LibraryCaptionRight;
        double top = 10;
        if (row is not null && row.IsVisible && row.ActualHeight > 0 && CaptionButtons.ActualHeight > 0 && row.IsDescendantOf(RootGrid))
        {
            double center = row.TransformToAncestor(RootGrid).Transform(new Point(0, row.ActualHeight / 2)).Y;
            top = Math.Max(0, Math.Round(center - CaptionButtons.ActualHeight / 2));
        }
        var margin = new Thickness(0, top, right, 0);
        if (CaptionButtons.Margin != margin) CaptionButtons.Margin = margin;
        var settingsMargin = new Thickness(0, top + CaptionButtons.ActualHeight + 8, 0, 0);
        if (SettingsPanel.Margin != settingsMargin) SettingsPanel.Margin = settingsMargin;
    }

    private void ShowLibraryTab(bool videos)
    {
        _showingVideos = videos;
        ImagesPane.Visibility = videos ? Visibility.Collapsed : Visibility.Visible;
        VideosPane.Visibility = videos ? Visibility.Visible : Visibility.Collapsed;
        StyleTab(ImagesTabButton, !videos);
        StyleTab(VideosTabButton, videos);
        SearchFrame.IsEnabled = !videos;
        SortButton.Visibility = ViewModeButton.Visibility = videos ? Visibility.Hidden : Visibility.Visible;
    }

    private void StyleTab(Button tab, bool selected)
    {
        tab.FontSize = selected ? 18 : 16;
        tab.FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold;
        tab.Foreground = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x8E, 0x90, 0x97));
    }

    private void ImagesTab_Click(object sender, RoutedEventArgs e) => ShowLibraryTab(false);
    private void VideosTab_Click(object sender, RoutedEventArgs e) => ShowLibraryTab(true);

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        _oldestFirst = !_oldestFirst;
        RecentListView.SortDescriptions.Clear();
        RecentListView.SortDescriptions.Add(new SortDescription(nameof(RecentCaptureItem.Timestamp), _oldestFirst ? ListSortDirection.Ascending : ListSortDirection.Descending));
        SortButton.ToolTip = _oldestFirst ? "Oldest first" : "Newest first";
        if (RecentList.SelectedItem is not null) RecentList.ScrollIntoView(RecentList.SelectedItem);
    }

    private void ViewMode_Click(object sender, RoutedEventArgs e) => SetGridMode(!_gridMode);

    private void SetGridMode(bool grid)
    {
        _gridMode = grid;
        RecentList.ItemsPanel = (ItemsPanelTemplate)FindResource(grid ? "CaptureTilePanel" : "CaptureRowPanel");
        RecentList.ItemTemplate = (DataTemplate)FindResource(grid ? "CaptureTileTemplate" : "CaptureRowTemplate");
        RecentList.ItemContainerStyle = (Style)FindResource(grid ? "LibraryTile" : "LibraryRow");
        ViewModeIcon.Icon = grid ? Controls.LucideIconKind.List : Controls.LucideIconKind.LayoutGrid;
        ViewModeButton.ToolTip = grid ? "Show as a list" : "Show as a grid";
    }

    private void AddCapture_Click(object sender, RoutedEventArgs e) => CaptureMenuPopup.IsOpen = !CaptureMenuPopup.IsOpen;

    /// <summary>Any row in the + menu closes it, except the chevron that opens the recording choices.</summary>
    private void CaptureMenu_ItemClick(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, RecordModeButton) && RecordModePopup.IsOpen) return;
        CaptureMenuPopup.IsOpen = false;
    }

    private void MenuSettings_Click(object sender, RoutedEventArgs e) => SetSettingsVisible(true);

    private void RecentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(RecentList, source) is ListBoxItem { DataContext: RecentCaptureItem item })
            OpenInEditor(item.Document);
    }

    private void RecordingRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: RecordingLibraryItem item }) OpenRecordingEditor(item.Entry);
    }

    private static RecentCaptureItem? RowItem(object sender) => (sender as FrameworkElement)?.DataContext as RecentCaptureItem;

    private void RowMarkUp_Click(object sender, RoutedEventArgs e) { if (RowItem(sender) is { } item) OpenInEditor(item.Document); }
    private void RowCopy_Click(object sender, RoutedEventArgs e) { if (RowItem(sender) is { } item) { _latest = item.Document; CopyRecent_Click(sender, e); } }
    private void RowSave_Click(object sender, RoutedEventArgs e) { if (RowItem(sender) is { } item) { _latest = item.Document; SaveRecent_Click(sender, e); } }
    private void RowCopyText_Click(object sender, RoutedEventArgs e) { if (RowItem(sender) is { } item) { _latest = item.Document; CopyRecentText_Click(sender, e); } }
    private void RowAskClaude_Click(object sender, RoutedEventArgs e) { if (RowItem(sender) is { } item) _presentation.AskClaude(item.Document, this); }
    private void RowDelete_Click(object sender, RoutedEventArgs e) { if (RowItem(sender) is { } item) DeleteDocument(item.Document); }

    /// <summary>Keys pressed while focus is in the library (not in a text field) still reach the open editor's tools.</summary>
    private bool ForwardKeyToEditor(KeyEventArgs e)
    {
        if (_editor is null || SettingsPanel.Visibility == Visibility.Visible || _editor.IsKeyboardFocusWithin) return false;
        if (Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox) return false;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Delete or Key.Back or Key.Enter or Key.Up or Key.Down or Key.Left or Key.Right or Key.Tab or Key.Escape or Key.PageUp or Key.PageDown or Key.Home or Key.End) return false;
        return _editor.HandleHostKeyDown(key, Keyboard.Modifiers);
    }

    private void UpdateLibraryEmptyState()
    {
        bool any = _recentItems.Count > 0;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        RecentList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"Saving to Pictures › BetterShot" with a green dot while every capture is saved; otherwise where Save goes.</summary>
    private void RefreshSaveLocation()
    {
        CaptureSettings settings = CurrentSettings;
        string folder = settings.EffectiveSaveFolder;
        bool saving = settings.SavesEveryCapture;
        SaveLocationText.Text = (saving ? "Saving to " : "Saves to ") + FriendlyFolder(folder);
        SaveLocationDot.Fill = saving ? SavingDot : IdleDot;
        SaveLocationRow.ToolTip = saving ? $"Every capture is saved to {folder}" : $"Captures stay in the library; Save uses {folder}";
    }

    internal static string FriendlyFolder(string folder)
    {
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return folder; }
        (Environment.SpecialFolder Folder, string Name)[] known =
        [
            (Environment.SpecialFolder.MyPictures, "Pictures"), (Environment.SpecialFolder.DesktopDirectory, "Desktop"),
            (Environment.SpecialFolder.MyDocuments, "Documents"), (Environment.SpecialFolder.MyVideos, "Videos"), (Environment.SpecialFolder.UserProfile, "Home")
        ];
        foreach ((Environment.SpecialFolder special, string name) in known)
        {
            string root = Environment.GetFolderPath(special).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (root.Length == 0) continue;
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return name;
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return name + " › " + string.Join(" › ", full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
        }
        string[] parts = full.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[^2]} › {parts[^1]}" : full;
    }

    /// <summary>Keeps ages ("15h") current and regroups at midnight ("Today" becomes "Yesterday").</summary>
    private void RefreshLibraryClock()
    {
        foreach (RecentCaptureItem item in _recentItems) item.RefreshAge();
        if (_libraryDay == DateTime.Today) return;
        _libraryDay = DateTime.Today;
        foreach (RecentCaptureItem item in _recentItems) item.RefreshDayGroup();
        RecentListView.Refresh();
    }

    /// <summary>Shows the "Text" chip on captures whose text has been read for search.</summary>
    private void RefreshTextChips()
    {
        bool enabled = CurrentSettings.SearchTextInImages;
        foreach (RecentCaptureItem item in _recentItems)
            item.SetHasText(enabled && !string.IsNullOrWhiteSpace(_textIndex?.TextFor(item.Document.Id)));
    }
}

/// <summary>One capture in the library list or grid.</summary>
internal sealed class RecentCaptureItem : INotifyPropertyChanged
{
    internal RecentCaptureItem(CaptureDocument document) { Document = document; Timestamp = document.CreatedUtc.ToLocalTime(); Thumbnail = document.Render(); }
    public CaptureDocument Document { get; }
    public DateTime Timestamp { get; }
    public System.Windows.Media.Imaging.BitmapSource Thumbnail { get; private set; }
    public string Details => $"{Timestamp:h:mm:ss tt} · {Document.Original.PixelWidth:N0} × {Document.Original.PixelHeight:N0} pixels";
    /// <summary>The capture's name in the default file-name style, for example "2026-11-18 at 21.31.16".</summary>
    public string DisplayName => Timestamp.ToString("yyyy-MM-dd 'at' HH.mm.ss", CultureInfo.InvariantCulture);
    public string TimeText => Timestamp.ToString("HH.mm.ss", CultureInfo.InvariantCulture);
    public string AgeText => LibraryText.Age(DateTime.Now - Timestamp);
    public string DimensionsText => $"{Document.Original.PixelWidth} × {Document.Original.PixelHeight}";
    public string DayGroup => LibraryText.DayGroup(Timestamp.Date, DateTime.Today);
    public bool HasText { get; private set; }
    public bool HasMarkup => Document.Items.Count > 0;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh()
    {
        Thumbnail = Document.Render();
        Raise(nameof(Thumbnail));
        Raise(nameof(HasMarkup));
    }

    internal void RefreshAge() => Raise(nameof(AgeText));

    internal void RefreshDayGroup() => Raise(nameof(DayGroup));

    internal void SetHasText(bool value)
    {
        if (HasText == value) return;
        HasText = value;
        Raise(nameof(HasText));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Library wording for capture ages and day groups.</summary>
internal static class LibraryText
{
    internal static string Age(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays}d";
        if (age < TimeSpan.FromDays(365)) return $"{(int)(age.TotalDays / 7)}w";
        return $"{(int)(age.TotalDays / 365)}y";
    }

    internal static string DayGroup(DateTime day, DateTime today)
    {
        int days = (today.Date - day.Date).Days;
        if (days <= 0) return "Today";
        if (days == 1) return "Yesterday";
        if (days < 7) return day.ToString("dddd", CultureInfo.CurrentCulture);
        return day.Year == today.Year ? day.ToString("MMMM d", CultureInfo.CurrentCulture) : day.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture);
    }
}
