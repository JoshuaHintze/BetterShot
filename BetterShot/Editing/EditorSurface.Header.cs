using BetterShot.Controls;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace BetterShot.Editing;

/// <summary>Header ("Mark up", Copy Text, Ask Claude, Copy, Done), the zoom pill, and where the tool pill and inspector sit.</summary>
public sealed partial class EditorSurface
{
    private const double ToolbarGap = 14;
    private readonly StackPanel _headerLeft = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _headerRight = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border _headerToolbarSlot = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _floatingToolbarSlot = new();
    private readonly TextBlock _titleText = new() { Text = "Mark up", FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 1) };
    private Grid _canvasArea = null!;
    private Button? _libraryButton;
    private Button? _fitButton;
    private Button? _actualSizeButton;
    private Button? _inspectorButton;
    private Button _doneButton = null!;
    private TextBlock? _ocrLabel;
    private TextBlock? _copyLabel;
    private LucideIcon? _copyIcon;

    internal bool ToolbarInHeaderForTest => ReferenceEquals(_headerToolbarSlot.Child, _toolbarPill);
    internal bool ToolbarExtrasVisibleForTest => _toolbarExtras.Visibility == Visibility.Visible;
    internal FrameworkElement ToolbarForTest => _toolbarPill;
    internal FrameworkElement HeaderRightForTest => _headerRight;

    private UIElement BuildHeader()
    {
        var header = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (_libraryControls)
        {
            Button back = IconButton(LucideIconKind.ChevronLeft, "Back to the library", (_, _) => { CommitPendingEdits(); BackRequested?.Invoke(this, EventArgs.Empty); }, 32, 32, 18);
            _libraryButton = IconButton(LucideIconKind.PanelLeft, "Hide the library", (_, _) => LibraryToggleRequested?.Invoke(this, EventArgs.Empty), 32, 32, 18);
            _headerLeft.Children.Add(back);
            _headerLeft.Children.Add(_libraryButton);
        }
        _headerLeft.Children.Add(_titleText);
        header.Children.Add(_headerLeft);

        Grid.SetColumn(_headerToolbarSlot, 1);
        header.Children.Add(_headerToolbarSlot);

        _inspectorButton = IconButton(LucideIconKind.SlidersHorizontal, "Hide inspector (I)", (_, _) => ToggleInspector(), 32, 32, 18);
        _headerRight.Children.Add(_inspectorButton);
        _headerRight.Children.Add(BuildOcrButton());
        Button ask = IconButton(LucideIconKind.Sparkles, "Ask Claude about this image", AskClaude_Click, 32, 32, 18);
        ask.Foreground = Brush("#FFD400");
        _headerRight.Children.Add(ask);
        if (!_libraryControls) _headerRight.Children.Add(IconButton(LucideIconKind.Save, "Save as… (Ctrl+S)", Save_Click, 32, 32, 18));
        Button copy = IconTextButton(LucideIconKind.Copy, "Copy", "Copy the finished image (Ctrl+C)", Copy_Click, 0);
        copy.Background = Brushes.Transparent; copy.Height = 32; copy.FontSize = 14;
        if (copy.Content is StackPanel { Children: [LucideIcon copyIcon, TextBlock copyLabel] }) { _copyIcon = copyIcon; _copyLabel = copyLabel; }
        _headerRight.Children.Add(copy);
        _doneButton = IconTextButton(LucideIconKind.Check, "Done", "Done (Ctrl+Enter)", (_, _) => Done(), 82);
        _doneButton.Background = Brush("#FFD400"); _doneButton.Foreground = Brushes.Black; _doneButton.Height = 32; _doneButton.FontSize = 14;
        _doneButton.Margin = new Thickness(6, 0, 0, 0); _doneButton.Template = ButtonTemplate(9);
        _headerRight.Children.Add(_doneButton);
        Grid.SetColumn(_headerRight, 2);
        header.Children.Add(_headerRight);

        // In the library window the header doubles as the title bar: empty space drags the window, controls stay clickable.
        WindowChrome.SetIsHitTestVisibleInChrome(_headerLeft, true);
        WindowChrome.SetIsHitTestVisibleInChrome(_headerToolbarSlot, true);
        WindowChrome.SetIsHitTestVisibleInChrome(_headerRight, true);
        return header;
    }

    private FrameworkElement BuildZoomPill()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dimensionsLabel);
        row.Children.Add(Separator());
        row.Children.Add(IconButton(LucideIconKind.ZoomOut, "Zoom out (Ctrl+wheel)", (_, _) => SetZoom(_zoom / 1.2), 28, 28, 16));
        row.Children.Add(_zoomLabel);
        row.Children.Add(IconButton(LucideIconKind.ZoomIn, "Zoom in (Ctrl+wheel)", (_, _) => SetZoom(_zoom * 1.2), 28, 28, 16));
        row.Children.Add(Separator());
        _fitButton = SegmentButton("Fit", "Fit to window (0)", () => { _fit = true; FitImage(); });
        _actualSizeButton = SegmentButton("100%", "Actual size", () => SetZoom(1));
        var segments = new StackPanel { Orientation = Orientation.Horizontal };
        segments.Children.Add(_fitButton); segments.Children.Add(_actualSizeButton);
        row.Children.Add(segments);
        return new Border
        {
            Child = row, CornerRadius = new CornerRadius(17), Background = Brush("#E81C1D20"), BorderBrush = Brush("#34363A"), BorderThickness = new Thickness(1),
            Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 18, Opacity = .45, ShadowDepth = 3, Direction = 270 }
        };
    }

    private Button SegmentButton(string text, string tip, Action click)
    {
        var label = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Content = label, ToolTip = tip, Height = 28, MinWidth = 46, Padding = new Thickness(9, 0, 9, 0), Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Center };
        label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        button.Template = ButtonTemplate(14);
        button.Click += (_, _) => click();
        return button;
    }

    private void UpdateZoomSegments()
    {
        if (_fitButton is null || _actualSizeButton is null) return;
        bool actual = !_fit && Math.Abs(_zoom - 1) < .001;
        _fitButton.Background = _fit ? Brush("#3A3B3F") : Brushes.Transparent;
        _actualSizeButton.Background = actual ? Brush("#3A3B3F") : Brushes.Transparent;
        _fitButton.Foreground = _fit ? Brushes.White : Brush("#B9BBC0");
        _actualSizeButton.Foreground = actual ? Brushes.White : Brush("#B9BBC0");
    }

    /// <summary>
    /// Puts the tool pill in the header when the window is wide enough (with quick colors and width), otherwise in its own
    /// row above the canvas; quick colors are dropped before the pill would clip. The tool row itself never wraps.
    /// </summary>
    private void UpdateChromeLayout()
    {
        if (!IsLoaded || ActualWidth <= 0) return;
        Size infinite = new(double.PositiveInfinity, double.PositiveInfinity);
        _toolbarExtras.Visibility = Visibility.Visible;
        _toolbarPill.Measure(infinite);
        double fullWidth = _toolbarPill.DesiredSize.Width;
        _toolbarExtras.Visibility = Visibility.Collapsed;
        _toolbarPill.Measure(infinite);
        double compactWidth = _toolbarPill.DesiredSize.Width;

        _titleText.Visibility = Visibility.Collapsed;
        SetHeaderLabels(true);
        _headerLeft.Measure(infinite);
        _headerRight.Measure(infinite);
        double headerSpace = ActualWidth - 28 - _headerLeft.DesiredSize.Width - _headerRight.DesiredSize.Width - ToolbarGap * 2;
        bool inHeader = headerSpace >= fullWidth;
        MoveToolbar(inHeader ? _headerToolbarSlot : _floatingToolbarSlot);
        if (!inHeader) FitHeader(infinite);

        double dockWidth = _inspectorDock.Child is null ? 0 : InspectorWidth + _inspectorDock.Margin.Left + _inspectorDock.Margin.Right;
        double canvasWidth = ActualWidth - dockWidth;
        bool showExtras = inHeader || canvasWidth >= fullWidth + ToolbarGap;
        _toolbarExtras.Visibility = showExtras ? Visibility.Visible : Visibility.Collapsed;
        // When the pill is wider than the canvas column it centers over the whole editor instead of clipping.
        bool spanAll = !inHeader && canvasWidth < (showExtras ? fullWidth : compactWidth) + ToolbarGap;
        Grid.SetColumn(_floatingToolbarSlot, spanAll ? 0 : 1);
        Grid.SetColumnSpan(_floatingToolbarSlot, spanAll ? 2 : 1);
        UpdateCanvasPadding();
    }

    /// <summary>When the header is tight, the "Mark up" title goes first, then the Copy Text and Copy labels (icons stay).</summary>
    private void FitHeader(Size infinite)
    {
        double available = ActualWidth - 36;
        SetHeaderLabels(true);
        _titleText.Visibility = Visibility.Visible;
        if (HeaderWidth(infinite) <= available) return;
        _titleText.Visibility = Visibility.Collapsed;
        if (HeaderWidth(infinite) <= available) return;
        SetHeaderLabels(false);
    }

    private double HeaderWidth(Size infinite)
    {
        _headerLeft.Measure(infinite);
        _headerRight.Measure(infinite);
        return _headerLeft.DesiredSize.Width + _headerRight.DesiredSize.Width;
    }

    private void SetHeaderLabels(bool visible)
    {
        Visibility visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_ocrLabel is not null) _ocrLabel.Visibility = visibility;
        if (_copyLabel is not null) _copyLabel.Visibility = visibility;
        if (_copyIcon is not null) _copyIcon.Margin = new Thickness(0, 0, visible ? 8 : 0, 0);
    }

    private void MoveToolbar(Border target)
    {
        if (ReferenceEquals(target.Child, _toolbarPill)) return;
        _headerToolbarSlot.Child = null;
        _floatingToolbarSlot.Child = null;
        target.Child = _toolbarPill;
        _floatingToolbarSlot.Visibility = ReferenceEquals(target, _floatingToolbarSlot) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Keeps the screenshot clear of the floating inspector and the zoom pill.</summary>
    private void UpdateCanvasPadding()
    {
        if (_canvasArea is null) return;
        bool floating = _inspectorFloatSlot.Child is not null;
        double left = floating ? InspectorWidth + _inspectorFloatSlot.Margin.Left + 20 : 28;
        _scroller.Padding = new Thickness(left, 10, 24, 52);
        if (floating) _inspectorCard.MaxHeight = Math.Max(160, _canvasArea.ActualHeight - _inspectorFloatSlot.Margin.Top - _inspectorFloatSlot.Margin.Bottom);
        else _inspectorCard.ClearValue(MaxHeightProperty);
        _status.MaxWidth = Math.Max(0, _canvasArea.ActualWidth / 2 - 250);
        _status.Visibility = _status.MaxWidth < 80 ? Visibility.Collapsed : Visibility.Visible;
        if (_fit) FitImage();
    }

    /// <summary>Checks that the header and tool pill controls are laid out without overlapping or leaving the surface.</summary>
    internal void VerifyChromeFitsForTest(string state)
    {
        UpdateLayout();
        var client = new Rect(new Point(), RenderSize);
        foreach (Panel row in new Panel[] { _headerLeft, _headerRight, _toolRow })
        {
            if (!row.IsVisible) continue;
            Rect? previous = null;
            foreach (FrameworkElement child in row.Children.OfType<FrameworkElement>().Where(element => element.IsVisible))
            {
                Rect bounds = child.TransformToAncestor(this).TransformBounds(new Rect(child.RenderSize));
                if (bounds.Left < client.Left - .5 || bounds.Right > client.Right + .5 || bounds.Top < client.Top - .5 || bounds.Bottom > client.Bottom + .5)
                    throw new InvalidOperationException($"Editor control {System.Windows.Automation.AutomationProperties.GetName(child)} is clipped at {state}.");
                if (previous is Rect prior && prior.Right > bounds.Left + .5)
                    throw new InvalidOperationException($"Editor controls overlap at {state}.");
                previous = bounds;
            }
        }
        Rect left = _headerLeft.TransformToAncestor(this).TransformBounds(new Rect(_headerLeft.RenderSize));
        Rect right = _headerRight.TransformToAncestor(this).TransformBounds(new Rect(_headerRight.RenderSize));
        if (left.Right > right.Left + .5) throw new InvalidOperationException($"The editor header overlaps itself at {state}.");
        if (ToolbarInHeaderForTest)
        {
            Rect pill = _toolbarPill.TransformToAncestor(this).TransformBounds(new Rect(_toolbarPill.RenderSize));
            if (pill.Left < left.Right - .5 || pill.Right > right.Left + .5) throw new InvalidOperationException($"The tool pill overlaps the header at {state}.");
        }
    }
}
