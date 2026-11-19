using BetterShot.Editing;
using BetterShot.Ocr;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace BetterShot;

/// <summary>
/// Library text features: search by name/date, typed markup, and text inside images (on-device OCR index),
/// the "Copy text" action, and the Settings > Library "Search text inside images" toggle.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan IndexStartDelay = TimeSpan.FromSeconds(4);
    private readonly DispatcherTimer _indexStartTimer = new() { Interval = IndexStartDelay };
    private OcrTextIndex? _textIndex;
    private OcrIndexer? _textIndexer;
    private bool _updatingTextSearchSetting;
    private bool _readingRecentText;

    internal OcrIndexer? TextIndexerForTest => _textIndexer;
    internal int VisibleRecentCountForTest => RecentListView.Cast<object>().Count();
    internal void SetSearchForTest(string query) => SearchBox.Text = query;
    internal void SuspendTextIndexingForTest() => _indexStartTimer.Stop();

    private ICollectionView RecentListView => CollectionViewSource.GetDefaultView(_recentItems);
    private string SearchQuery => SearchBox.Text.Trim();

    /// <summary>Called once from the constructor after the library is loaded.</summary>
    private void InitializeTextSearch(string? historyRoot)
    {
        RecentListView.Filter = item => item is RecentCaptureItem recent && MatchesSearch(recent);
        PreviewKeyDown += FocusSearch_PreviewKeyDown;
        if (_historyStore is not null)
        {
            string path = historyRoot is null ? OcrTextIndex.DefaultPath : Path.Combine(historyRoot, "ocr-index.json");
            try { _textIndex = new OcrTextIndex(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { _textIndex = null; }
        }
        if (_textIndex is not null)
        {
            _textIndex.Prune(_recentItems.Select(item => item.Document.Id));
            SaveTextIndex();
            _textIndexer = new OcrIndexer(_textIndex, Dispatcher);
            _textIndexer.Changed += TextIndexer_Changed;
            // Reading starts after launch settles so startup and the first capture stay responsive.
            _indexStartTimer.Tick += (_, _) => { _indexStartTimer.Stop(); ApplyTextSearchSetting(); };
            _indexStartTimer.Start();
        }
        LoadTextSearchSettingIntoControls();
    }

    /// <summary>Queues a new or edited capture for background reading and refreshes an active search.</summary>
    private void QueueTextIndex(CaptureDocument document, bool priority = false)
    {
        if (_textIndexer is { Enabled: true }) _textIndexer.Enqueue(document, priority);
        if (!CaptureSearch.IsEmptyQuery(SearchQuery)) RefreshSearch();
    }

    private void RemoveFromTextIndex(CaptureDocument document)
    {
        _textIndexer?.Remove(document);
        if (!CaptureSearch.IsEmptyQuery(SearchQuery)) RefreshSearch();
    }

    private void ShutdownTextSearch()
    {
        _indexStartTimer.Stop();
        if (_textIndexer is not null)
        {
            _textIndexer.Changed -= TextIndexer_Changed;
            _textIndexer.Dispose();
        }
        CaptureTextReader.CancelAll();
    }

    private void ApplyTextSearchSetting()
    {
        if (_textIndexer is null || _textIndex is null) { LoadTextSearchSettingIntoControls(); return; }
        bool enabled = CurrentSettings.SearchTextInImages;
        _textIndexer.Enabled = enabled;
        if (enabled) foreach (RecentCaptureItem item in _recentItems) _textIndexer.Enqueue(item.Document);
        else
        {
            _textIndex.Clear();
            SaveTextIndex();
        }
        LoadTextSearchSettingIntoControls();
        RefreshSearch();
    }

    private void SaveTextIndex()
    {
        try { _textIndex?.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void TextIndexer_Changed(object? sender, EventArgs e)
    {
        UpdateTextIndexStatus();
        if (!CaptureSearch.IsEmptyQuery(SearchQuery)) RefreshSearch();
    }

    private void LoadTextSearchSettingIntoControls()
    {
        _updatingTextSearchSetting = true;
        try { SearchTextCheckBox.IsChecked = CurrentSettings.SearchTextInImages; }
        finally { _updatingTextSearchSetting = false; }
        UpdateTextIndexStatus();
    }

    private void UpdateTextIndexStatus()
    {
        TextIndexStatusText.Text = TextIndexStatus();
        TextIndexStatusText.Visibility = string.IsNullOrEmpty(TextIndexStatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private string TextIndexStatus()
    {
        if (!CurrentSettings.SearchTextInImages) return "Text reading is off.";
        if (_textIndexer is null) return _historyStore is null ? "Text search works when the library is stored on this PC." : "Text search is unavailable because its index could not be opened.";
        int total = _recentItems.Count, indexed = _recentItems.Count(item => _textIndex?.TextFor(item.Document.Id) is not null);
        return _textIndexer.State switch
        {
            OcrIndexState.Unavailable => _textIndexer.UnavailableReason ?? OcrUnavailableException.NoLanguageMessage,
            OcrIndexState.Working => $"Reading text in your captures… {indexed} of {total} done.",
            OcrIndexState.Off => "Text reading starts shortly.",
            _ => total == 0 ? "Text in new captures will be searchable." : $"Text in {indexed} of {total} capture{(total == 1 ? "" : "s")} is searchable."
        };
    }

    private void SearchText_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _updatingTextSearchSetting) return;
        bool enabled = SearchTextCheckBox.IsChecked == true;
        try
        {
            UpdateSettings(settings => settings with { SearchTextInImages = enabled });
            SetStatus(enabled ? "BetterShot will read text in your captures on this PC for search." : "Text reading is off. Stored text was removed.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save the text search setting: {FriendlyMessage(ex)}", error: true, persistent: true);
        }
        _indexStartTimer.Stop();
        ApplyTextSearchSetting();
    }

    private bool MatchesSearch(RecentCaptureItem item)
    {
        string query = SearchQuery;
        if (CaptureSearch.IsEmptyQuery(query)) return true;
        return CaptureSearch.Matches(query, SearchableFields(item));
    }

    private IEnumerable<string?> SearchableFields(RecentCaptureItem item)
    {
        foreach (string label in CaptureSearch.DateLabels(item.Timestamp, DateTime.Today)) yield return label;
        yield return item.Details;
        foreach (TextAnnotation text in item.Document.Items.OfType<TextAnnotation>()) yield return text.Text;
        if (CurrentSettings.SearchTextInImages) yield return _textIndex?.TextFor(item.Document.Id);
    }

    private void RefreshSearch()
    {
        RecentListView.Refresh();
        UpdateSearchUi();
    }

    private void UpdateSearchUi()
    {
        string query = SearchQuery;
        bool searching = !CaptureSearch.IsEmptyQuery(query);
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool empty = searching && _recentItems.Count > 0 && RecentListView.IsEmpty;
        SearchEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        string hint = !CurrentSettings.SearchTextInImages
            ? " Text inside images isn't searched because text reading is off in Settings."
            : _textIndexer is { State: OcrIndexState.Working } ? " Some captures are still being read." : string.Empty;
        SearchEmptyText.Text = $"No captures match “{query}”.{hint}";
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        RefreshSearch();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0) { SearchBox.Clear(); e.Handled = true; }
        else if (e.Key is Key.Enter or Key.Down && !RecentListView.IsEmpty)
        {
            RecentList.SelectedItem = RecentListView.Cast<object>().First();
            if (RecentList.ItemContainerGenerator.ContainerFromItem(RecentList.SelectedItem) is UIElement container) container.Focus();
            e.Handled = true;
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void FocusSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control || RecentState.Visibility != Visibility.Visible) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }

    private async void CopyRecentText_Click(object sender, RoutedEventArgs e)
    {
        CaptureDocument? document = _latest;
        if (document is null || _readingRecentText) return;
        _readingRecentText = true;
        SetStatus("Reading the text…", persistent: true);
        try
        {
            RecognizedText text = await CaptureTextReader.ReadVisibleAsync(document, default);
            if (text.IsEmpty) { SetStatus("No text found in this capture."); return; }
            string? error = await ClipboardText.SetAsync(text.FullText);
            if (error is null) SetStatus("Copied to clipboard");
            else SetStatus($"Could not copy: {error}", true);
        }
        catch (OcrUnavailableException ex) { SetStatus(ex.Message, error: true, persistent: true); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { SetStatus($"Couldn't read the text: {FriendlyMessage(ex)}", true); }
        finally { _readingRecentText = false; }
    }
}
