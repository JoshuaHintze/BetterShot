using BetterShot.Controls;
using BetterShot.Ocr;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BetterShot.Editing;

/// <summary>
/// On-device text actions: Copy all text, Copy text from selection (rubber band), and automatic redaction of
/// detected emails, numbers, and keys. Text is always read from the original pixels with redactions masked.
/// </summary>
public sealed partial class EditorWindow
{
    private const double CompactToolbarWidth = 840;
    private const double MinimumSelectionDrag = 4;
    private static readonly Brush HintFill = Frozen(Color.FromArgb(40, 255, 212, 0));
    private static readonly Brush HintSelectedFill = Frozen(Color.FromArgb(110, 255, 212, 0));
    private static readonly Brush HintStroke = Frozen(Color.FromArgb(150, 255, 212, 0));
    private readonly Canvas _ocrOverlay = new() { Background = Brushes.Transparent, Visibility = Visibility.Collapsed, Cursor = Cursors.Cross };
    private readonly TextBlock _toastText = new() { Foreground = Brushes.White, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
    private readonly Border _toast;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private readonly List<(Rectangle Shape, TextWord Word)> _wordHints = [];
    private readonly CancellationTokenSource _ocrCancellation = new();
    private Rectangle? _selectionBand;
    private Point? _selectionStart;
    private bool _selectingText;
    private RecognizedText? _selectionText;
    private Button? _doneButton;

    internal bool SelectingTextForTest => _selectingText;
    internal string ToastTextForTest => _toastText.Text;
    internal Task CopyAllTextForTest() => CopyAllTextAsync();
    internal Task<int> RedactDetectedForTest() => RedactDetectedAsync();
    internal void BeginTextSelectionForTest() => BeginTextSelection();
    internal Task CopySelectionForTest(Rect sourceRegion) => CopySelectionAsync(sourceRegion);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private Border CreateToast() => new()
    {
        Child = _toastText, Background = Brush("#F0232427"), BorderBrush = Brush("#45474B"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 0, 22),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Opacity = 0,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 16, Opacity = .5, ShadowDepth = 3 }
    };

    private void InitializeOcr()
    {
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); FadeToast(false); };
        _ocrOverlay.MouseLeftButtonDown += OcrOverlay_MouseLeftButtonDown;
        _ocrOverlay.MouseMove += OcrOverlay_MouseMove;
        _ocrOverlay.MouseLeftButtonUp += OcrOverlay_MouseLeftButtonUp;
        _ocrOverlay.LostMouseCapture += (_, _) => { if (_selectionStart is not null) ClearSelectionBand(); };
        _ocrOverlay.SizeChanged += (_, _) => DrawWordHints();
        SizeChanged += (_, _) => UpdateCompactToolbar();
        Closed += (_, _) => { _ocrCancellation.Cancel(); _toastTimer.Stop(); };
    }

    /// <summary>Split button: the main part copies all text; the chevron offers both scopes.</summary>
    private FrameworkElement BuildOcrButton()
    {
        Button main = IconButton(LucideIconKind.ScanText, "Copy all text (Ctrl+Shift+C)", (_, _) => _ = CopyAllTextAsync(), 28);
        Button chevron = IconButton(LucideIconKind.ChevronDown, "Text options", (_, _) => { }, 16, 36, 11);
        main.Margin = new Thickness(0); chevron.Margin = new Thickness(0);
        main.Background = Brushes.Transparent; chevron.Background = Brushes.Transparent;
        chevron.Padding = new Thickness(0, 5, 3, 5);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(main); row.Children.Add(chevron);
        var chrome = new Border { Child = row, CornerRadius = new CornerRadius(9), Margin = new Thickness(1, 0, 1, 0), Background = Brush("#303133") };
        chevron.Click += (_, _) => OpenOcrMenu(chrome);
        return chrome;
    }

    private void OpenOcrMenu(FrameworkElement target)
    {
        ContextMenu menu = DarkMenu();
        menu.Items.Add(DarkMenuItem("Copy all text", LucideIconKind.ScanText, "Ctrl+Shift+C", (_, _) => _ = CopyAllTextAsync()));
        menu.Items.Add(DarkMenuItem("Copy text from selection", LucideIconKind.Crop, null, (_, _) => BeginTextSelection()));
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void AddEffectsMenuActions(ContextMenu menu)
    {
        menu.Items.Add(new Separator { Background = Brush("#4A4B4E") });
        menu.Items.Add(DarkMenuItem("Find and cover emails, numbers, and keys automatically", LucideIconKind.ScanText, null, (_, _) => _ = RedactDetectedAsync()));
    }

    private bool HandleOcrShortcut(Key key, ModifierKeys modifiers)
    {
        if (_selectingText && key == Key.Escape) { CancelTextSelection("Text selection cancelled."); return true; }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.C) { _ = CopyAllTextAsync(); return true; }
        return false;
    }

    /// <summary>Below this width the Done label collapses to its icon so the single-row toolbar never clips.</summary>
    private void UpdateCompactToolbar()
    {
        if (_doneButton?.Content is not StackPanel { Children: [LucideIcon icon, TextBlock label] }) return;
        bool compact = ActualWidth < CompactToolbarWidth;
        _doneButton.Width = compact ? 36 : 82;
        _doneButton.Padding = compact ? new Thickness(5) : new Thickness(10, 5, 10, 5);
        label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        icon.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 7, 0);
        CompactSquareButtons(_toolbarBar, compact);
    }

    private Grid? _toolbarBar;
    private readonly Dictionary<Button, (double Width, Thickness Margin)> _fullToolbarButtonSizes = new();

    /// <summary>Square 36 DIP toolbar buttons shrink to 32 DIP with tighter gaps in compact mode, so tools, Ask Claude, OCR, Copy, Save and Done stay on one row at the 780 DIP minimum width.</summary>
    private void CompactSquareButtons(DependencyObject? parent, bool compact)
    {
        if (parent is null) return;
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is Button button && !ReferenceEquals(button, _doneButton))
            {
                if (!_fullToolbarButtonSizes.TryGetValue(button, out var full))
                {
                    if (button.Width != 36) continue;
                    full = (button.Width, button.Margin);
                    _fullToolbarButtonSizes[button] = full;
                }
                button.Width = compact ? 32 : full.Width;
                button.Margin = compact ? new Thickness(1, 0, 1, 0) : full.Margin;
            }
            else if (child is Panel or Border or Grid) CompactSquareButtons(child as DependencyObject, compact);
        }
    }

    private async Task CopyAllTextAsync()
    {
        CancelTextSelection(null);
        CommitPendingEdits();
        RecognizedText? text = await ReadTextAsync();
        if (text is null) return;
        if (text.IsEmpty) { ShowToast("No text found in this capture."); return; }
        await CopyToClipboardAsync(text.FullText);
    }

    private void BeginTextSelection()
    {
        CommitPendingEdits();
        if (_start is not null) { _image.ReleaseMouseCapture(); CancelDrag(); }
        if (_tool != EditorTool.Select && _selected is not null) { _selected = null; Refresh(); }
        _selectingText = true;
        _selectionText = null;
        _ocrOverlay.Visibility = Visibility.Visible;
        _status.Text = "Drag over the text to copy, or click to cancel";
        ShowToast("Drag over the text to copy, or click to cancel", persistent: true);
        _ = PrefetchSelectionTextAsync();
    }

    private async Task PrefetchSelectionTextAsync()
    {
        try
        {
            RecognizedText text = await CaptureTextReader.ReadVisibleAsync(_document, _ocrCancellation.Token);
            if (!_selectingText) return;
            _selectionText = text;
            DrawWordHints();
        }
        catch (Exception ex) when (ex is OperationCanceledException or OcrUnavailableException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Errors are reported when the selection is copied; hints are only a convenience.
        }
    }

    private void CancelTextSelection(string? message)
    {
        if (!_selectingText) return;
        _selectingText = false;
        ClearSelectionBand();
        ClearWordHints();
        _ocrOverlay.Visibility = Visibility.Collapsed;
        _selectionText = null;
        if (message is not null) { _status.Text = message; ShowToast(message); }
        else FadeToast(false);
        _image.Focus();
    }

    private void OcrOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_selectingText) return;
        _selectionStart = e.GetPosition(_image);
        _selectionBand = new Rectangle { Stroke = Brush("#FFD400"), StrokeThickness = 1.5, StrokeDashArray = [4, 3], Fill = Frozen(Color.FromArgb(34, 255, 212, 0)), IsHitTestVisible = false };
        _ocrOverlay.Children.Add(_selectionBand);
        UpdateSelectionBand(e.GetPosition(_image));
        _ocrOverlay.CaptureMouse();
        e.Handled = true;
    }

    private void OcrOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_selectionStart is null || e.LeftButton != MouseButtonState.Pressed) return;
        UpdateSelectionBand(e.GetPosition(_image));
    }

    private void OcrOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectionStart is not Point start) return;
        Point end = e.GetPosition(_image);
        e.Handled = true;
        _selectionStart = null;
        _ocrOverlay.ReleaseMouseCapture();
        var display = new Rect(start, end);
        if (display.Width < MinimumSelectionDrag && display.Height < MinimumSelectionDrag)
        {
            CancelTextSelection("Text selection cancelled.");
            return;
        }
        Rect source = OcrRegionMapper.DisplayToSource(display, DisplayScale, _layout);
        CancelTextSelection(null);
        _ = CopySelectionAsync(source);
    }

    private double DisplayScale => _image.ActualWidth / Math.Max(1, _layout.OutputWidth);

    private void UpdateSelectionBand(Point current)
    {
        if (_selectionStart is not Point start || _selectionBand is null) return;
        var display = new Rect(start, current);
        Canvas.SetLeft(_selectionBand, display.X); Canvas.SetTop(_selectionBand, display.Y);
        _selectionBand.Width = display.Width; _selectionBand.Height = display.Height;
        Rect source = OcrRegionMapper.DisplayToSource(display, DisplayScale, _layout);
        foreach ((Rectangle shape, TextWord word) in _wordHints)
            shape.Fill = RecognizedText.Intersects(word.Bounds, source) ? HintSelectedFill : HintFill;
    }

    private void ClearSelectionBand()
    {
        _selectionStart = null;
        if (_selectionBand is not null) _ocrOverlay.Children.Remove(_selectionBand);
        _selectionBand = null;
        if (_ocrOverlay.IsMouseCaptured) _ocrOverlay.ReleaseMouseCapture();
    }

    /// <summary>Outlines recognized words so the user can see what a selection will pick up.</summary>
    private void DrawWordHints()
    {
        ClearWordHints();
        if (!_selectingText || _selectionText is null || _cropping) return;
        double scale = DisplayScale;
        foreach (TextWord word in _selectionText.Lines.SelectMany(line => line.Words))
        {
            Rect display = OcrRegionMapper.SourceToDisplay(word.Bounds, scale, _layout);
            if (display.IsEmpty) continue;
            var shape = new Rectangle { Width = display.Width + 2, Height = display.Height + 2, Fill = HintFill, Stroke = HintStroke, StrokeThickness = 1, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false };
            Canvas.SetLeft(shape, display.X - 1); Canvas.SetTop(shape, display.Y - 1);
            _ocrOverlay.Children.Insert(0, shape);
            _wordHints.Add((shape, word));
        }
    }

    private void ClearWordHints()
    {
        foreach ((Rectangle shape, _) in _wordHints) _ocrOverlay.Children.Remove(shape);
        _wordHints.Clear();
    }

    private async Task CopySelectionAsync(Rect sourceRegion)
    {
        if (sourceRegion.IsEmpty) { ShowToast("No text in selection"); return; }
        RecognizedText? text = await ReadTextAsync();
        if (text is null) return;
        string selected = text.TextInside(sourceRegion);
        if (string.IsNullOrWhiteSpace(selected)) { ShowToast("No text in selection"); return; }
        await CopyToClipboardAsync(selected);
    }

    /// <summary>Covers detected sensitive items with the current redaction style as a single undo step. Returns the count.</summary>
    private async Task<int> RedactDetectedAsync()
    {
        CancelTextSelection(null);
        CommitPendingEdits();
        RecognizedText? text = await ReadTextAsync(visibleOnly: false);
        if (text is null) return 0;
        Rect canvas = _document.CanvasRect;
        Rect[] regions = SensitiveTextDetector.Find(text)
            .Select(match => SensitiveTextDetector.PaddedBounds(match.Bounds, canvas))
            .Where(region => !region.IsEmpty)
            .ToArray();
        if (regions.Length == 0) { ShowToast("Nothing found"); _status.Text = "No emails, phone numbers, card numbers, keys, or IP addresses were found."; return 0; }
        ToolStyle style = _styles.Get(EditorTool.Redact);
        Annotation[] redactions = regions.Select(region => new RectangleAnnotation(region, style.Stroke, style.Width, true).WithStyle(style)).ToArray();
        _selected = null;
        _document.AddRange(redactions);
        string message = $"Covered {regions.Length} item{(regions.Length == 1 ? "" : "s")}";
        ShowToast(message);
        _status.Text = message + ". Undo (Ctrl+Z) removes them all; redactions are always burned into exports.";
        return regions.Length;
    }

    /// <summary>Runs OCR with progress and error feedback. Returns null when reading failed or was cancelled.</summary>
    private async Task<RecognizedText?> ReadTextAsync(bool visibleOnly = true)
    {
        ShowToast("Reading the text…", persistent: true);
        _status.Text = "Reading the text on this PC…";
        try
        {
            return visibleOnly
                ? await CaptureTextReader.ReadVisibleAsync(_document, _ocrCancellation.Token)
                : await CaptureTextReader.ReadAsync(_document, _ocrCancellation.Token);
        }
        catch (OperationCanceledException) { return null; }
        catch (OcrUnavailableException ex) { ShowToast("Text reading isn't set up on this PC."); _status.Text = ex.Message; return null; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowToast("Couldn't read the text.");
            _status.Text = $"Couldn't read the text: {ex.Message}";
            return null;
        }
    }

    private async Task CopyToClipboardAsync(string text)
    {
        string? error = await ClipboardText.SetAsync(text);
        if (error is null)
        {
            ShowToast("Copied to clipboard");
            int lines = text.Split('\n').Length;
            _status.Text = $"Copied {text.Length:N0} character{(text.Length == 1 ? "" : "s")} of text ({lines} line{(lines == 1 ? "" : "s")}) to the clipboard.";
        }
        else { ShowToast("Couldn't copy the text."); _status.Text = $"Could not copy: {error}"; }
    }

    private void ShowToast(string message, bool persistent = false)
    {
        _toastText.Text = message;
        _toastTimer.Stop();
        FadeToast(true);
        if (!persistent) _toastTimer.Start();
    }

    private void FadeToast(bool show) =>
        _toast.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 110 : 240)));
}
