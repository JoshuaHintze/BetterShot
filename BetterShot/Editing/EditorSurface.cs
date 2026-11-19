using BetterShot.Controls;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using BetterShot.Export;

namespace BetterShot.Editing;

/// <summary>
/// The screenshot editor: header actions, tool pill, tool inspector, canvas, and zoom pill. Hosted by the main library
/// window and by the standalone <see cref="EditorWindow"/>, so both use one implementation.
/// </summary>
public sealed partial class EditorSurface : UserControl
{
    internal const double HeaderHeight = 64;
    private readonly CaptureDocument _document;
    private EditorStyleStore _styles;
    private readonly bool _libraryControls;
    private readonly Image _image = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true, Focusable = true };
    private readonly Canvas _imageOverlay = new() { Background = null, ClipToBounds = false };
    private readonly Grid _imageSurface = new() { ClipToBounds = false };
    private readonly PreviewLayer _previewLayer = new() { IsHitTestVisible = false };
    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(150, 153, 160)), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
    private readonly TextBlock _dimensionsLabel = new() { Foreground = new SolidColorBrush(Color.FromRgb(214, 216, 220)), FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
    private readonly TextBlock _zoomLabel = new() { Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Width = 52 };
    private Window? _hostWindow;
    private bool _detached;
    private EditorTool _tool = EditorTool.Arrow;
    private Annotation? _selected;
    private Point? _start;
    private readonly List<Point> _penPoints = [];
    private Annotation? _preview;
    private bool _fit = true;
    private double _zoom = 1;
    private CompositionLayout _layout;

    /// <summary>Done (or Ctrl+Enter). Pending edits are committed before this is raised.</summary>
    internal event EventHandler? DoneRequested;
    /// <summary>The header back chevron (library host only).</summary>
    internal event EventHandler? BackRequested;
    /// <summary>The header sidebar toggle (library host only).</summary>
    internal event EventHandler? LibraryToggleRequested;
    /// <summary>Raised once when the host is finished with this surface.</summary>
    internal event EventHandler? Detached;

    internal EditorSurface(CaptureDocument document, EditorStyleStore? styles = null, bool libraryControls = false)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _styles = styles ?? EditorStyleStore.Shared;
        _libraryControls = libraryControls;
        _layout = _document.Layout();
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Editing/EditorTheme.xaml", UriKind.Absolute) });
        Background = Brush("#141517"); Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 14;
        Focusable = false;
        _toast = CreateToast();
        Content = BuildLayout();
        PlaceInspector();
        InitializeNavigation();
        InitializeOcr();
        _document.Changed += DocumentChanged;
        Loaded += Surface_Loaded;
        Unloaded += Surface_Unloaded;
        SizeChanged += (_, _) => UpdateChromeLayout();
        _scroller.ScrollChanged += (_, e) => { if (_fit && (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)) FitImage(); };
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        SelectTool(EditorTool.Arrow);
    }

    internal CaptureDocument Document => _document;

    /// <summary>Keeps the right end of the header clear of the host window's caption buttons.</summary>
    internal double HeaderRightInset
    {
        get => _headerRight.Margin.Right;
        set { _headerRight.Margin = new Thickness(0, 0, Math.Max(0, value), 0); UpdateChromeLayout(); }
    }

    /// <summary>Tells the surface whether the library beside it is showing (updates the sidebar toggle's label).</summary>
    internal bool LibraryVisible
    {
        set
        {
            if (_libraryButton is null) return;
            string label = value ? "Hide the library" : "Show the library";
            _libraryButton.ToolTip = label;
            System.Windows.Automation.AutomationProperties.SetName(_libraryButton, label);
        }
    }

    private void Surface_Loaded(object sender, RoutedEventArgs e)
    {
        Window? host = Window.GetWindow(this);
        if (!ReferenceEquals(host, _hostWindow))
        {
            if (_hostWindow is not null) _hostWindow.Deactivated -= Editor_Deactivated;
            _hostWindow = host;
            if (_hostWindow is not null && !_detached) _hostWindow.Deactivated += Editor_Deactivated;
        }
        Refresh();
        UpdateChromeLayout();
        FitImage();
    }

    private void Surface_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null) _hostWindow.Deactivated -= Editor_Deactivated;
        _hostWindow = null;
        ResetNavigation();
    }

    /// <summary>Commits inline text and a pending crop. Used before export, close, and switching captures.</summary>
    internal void CommitPendingEdits()
    {
        CommitTextEdit();
        if (_cropping) ApplyCrop();
    }

    /// <summary>Drops inline text and a pending crop without touching the document (for a capture that is being deleted).</summary>
    internal void DiscardPendingEdits()
    {
        CancelTextEdit();
        if (_cropping) { EndCrop(); Refresh(); }
    }

    /// <summary>Stops listening to the document and saves editor preferences. The surface is not reused afterwards.</summary>
    internal void Detach()
    {
        if (_detached) return;
        _detached = true;
        _document.Changed -= DocumentChanged;
        if (_hostWindow is not null) _hostWindow.Deactivated -= Editor_Deactivated;
        _hostWindow = null;
        _ocrCancellation.Cancel();
        _toastTimer.Stop();
        _styles.Flush();
        Detached?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lets a host forward keys pressed while focus is outside the surface (for example, in the library list).</summary>
    internal bool HandleHostKeyDown(Key key, ModifierKeys modifiers)
    {
        if (_detached) return false;
        if (key == Key.Space && modifiers == ModifierKeys.None) return TryBeginSpaceNavigation(false);
        return HandleShortcut(key, modifiers, false);
    }

    internal void HandleHostKeyUp(Key key)
    {
        if (key == Key.Space && _spaceHeld) EndSpaceNavigation();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Background = CanvasBackdrop() };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(BuildHeader());

        var body = new Grid();
        Grid.SetRow(body, 1);
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        _floatingToolbarSlot.HorizontalAlignment = HorizontalAlignment.Center;
        _floatingToolbarSlot.Margin = new Thickness(12, 2, 12, 12);
        Grid.SetColumn(_floatingToolbarSlot, 1);
        body.Children.Add(_floatingToolbarSlot);
        Grid.SetRow(_inspectorDock, 1);
        body.Children.Add(_inspectorDock);
        FrameworkElement canvas = BuildCanvasArea();
        Grid.SetRow(canvas, 1); Grid.SetColumn(canvas, 1);
        body.Children.Add(canvas);
        root.Children.Add(body);
        BuildToolbarPill();
        return root;
    }

    /// <summary>A soft charcoal vignette behind the screenshot, lighter toward the upper middle.</summary>
    private static Brush CanvasBackdrop()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(.6, .36), GradientOrigin = new Point(.62, .28), RadiusX = .95, RadiusY = .95,
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x2C, 0x2C, 0x2F), 0),
                new GradientStop(Color.FromRgb(0x1D, 0x1D, 0x20), .5),
                new GradientStop(Color.FromRgb(0x11, 0x11, 0x13), 1)
            }
        };
        brush.Freeze();
        return brush;
    }

    private FrameworkElement BuildCanvasArea()
    {
        var area = new Grid { ClipToBounds = true };
        _canvasArea = area;
        _scroller.Background = Brushes.Transparent; _scroller.Focusable = false;
        PreviewMouseDown += Scroller_PreviewMouseDown;
        _imageSurface.Children.Add(_image); _imageSurface.Children.Add(_previewLayer); _imageSurface.Children.Add(_imageOverlay); _imageSurface.Children.Add(_cropOverlay); _imageSurface.Children.Add(_ocrOverlay);
        var imageBorder = new Border
        {
            Background = Brushes.Black, Child = _imageSurface, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 46, Opacity = .62, ShadowDepth = 10, Direction = 270 }
        };
        _image.MouseLeftButtonDown += Image_MouseDown; _image.MouseMove += Image_MouseMove; _image.MouseLeftButtonUp += Image_MouseUp;
        _image.MouseRightButtonUp += Image_MouseRightButtonUp;
        _image.LostMouseCapture += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_image.IsMouseCaptured) return;
            if (_start is not null) CancelDrag();
            if (_cropDragStart is not null) EndCropDrag();
        });
        _scroller.Content = imageBorder;
        area.Children.Add(_scroller);
        area.Children.Add(_inspectorFloatSlot);
        _status.HorizontalAlignment = HorizontalAlignment.Left; _status.VerticalAlignment = VerticalAlignment.Bottom; _status.Margin = new Thickness(22, 0, 0, 34);
        area.Children.Add(_status);
        area.Children.Add(BuildZoomPill());
        area.Children.Add(_toast);
        area.SizeChanged += (_, _) => UpdateCanvasPadding();
        return area;
    }

    private Button IconButton(LucideIconKind kind, string tip, RoutedEventHandler click, double width = 36, double height = 36, double iconSize = 18)
    {
        var icon = new LucideIcon { Icon = kind, Width = iconSize, Height = iconSize };
        var b = new Button { Content = icon, ToolTip = tip, Width = width, Height = height, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(0), Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Center };
        icon.SetBinding(Control.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = b });
        System.Windows.Automation.AutomationProperties.SetName(b, tip.Split('(')[0].Trim()); b.Template = ButtonTemplate(10); b.Click += click; return b;
    }

    private Button IconTextButton(LucideIconKind kind, string text, string tip, RoutedEventHandler click, double minimumWidth)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new LucideIcon { Icon = kind, Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) };
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        row.Children.Add(icon); row.Children.Add(label);
        var b = new Button { Content = row, ToolTip = tip, Height = 36, MinWidth = minimumWidth, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(12, 0, 14, 0), Foreground = Brushes.White, Background = Brush("#303133"), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Center };
        icon.SetBinding(Control.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = b }); label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = b });
        System.Windows.Automation.AutomationProperties.SetName(b, tip.Split('(')[0].Trim()); b.Template = ButtonTemplate(10); b.Click += click; return b;
    }

    private Button ActionButton(string text, string tip, RoutedEventHandler click, double width = 46)
    {
        var label = new TextBlock { Text = text, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var b = new Button { Content = label, ToolTip = tip, Width = width, Height = 40, Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(5),
            Foreground = Brushes.White, Background = Brush("#303133"), BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(b, tip.Split('(')[0].Trim());
        label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = b });
        b.Template = ButtonTemplate(9); b.Click += click; return b;
    }

    private static ControlTemplate RoundedButtonTemplate() => ButtonTemplate(9);

    private static readonly Brush HoverWash = Frozen(Color.FromArgb(28, 255, 255, 255));

    /// <summary>A flat rounded button: the Background fill, a soft white hover wash, and a yellow keyboard-focus ring.</summary>
    private static ControlTemplate ButtonTemplate(double radius)
    {
        var template = new ControlTemplate(typeof(Button));
        var chrome = new FrameworkElementFactory(typeof(Border), "chrome");
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        chrome.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var hover = new FrameworkElementFactory(typeof(Border), "hover");
        hover.SetValue(Border.CornerRadiusProperty, new CornerRadius(Math.Max(0, radius - 1)));
        hover.SetValue(Border.BackgroundProperty, HoverWash);
        hover.SetValue(OpacityProperty, 0.0);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        content.SetValue(HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(hover); grid.AppendChild(content); chrome.AppendChild(grid);
        template.VisualTree = chrome;
        var over = new Trigger { Property = IsMouseOverProperty, Value = true }; over.Setters.Add(new Setter(OpacityProperty, 1.0, "hover")); template.Triggers.Add(over);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true }; pressed.Setters.Add(new Setter(OpacityProperty, .72)); template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(OpacityProperty, .32)); template.Triggers.Add(disabled);
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true }; focus.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("#FFF07A"), "chrome")); template.Triggers.Add(focus);
        return template;
    }

    private static ContextMenu DarkMenu()=>new(){Background=Brush("#242527"),Foreground=Brushes.White,BorderBrush=Brush("#3F4044"),BorderThickness=new Thickness(1),Padding=new Thickness(4)};

    private static MenuItem DarkMenuItem(string header, LucideIconKind? icon, string? gesture, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = header, Padding = new Thickness(10, 7, 22, 7), Background = Brush("#242527"), Foreground = Brushes.White, InputGestureText = gesture ?? "" };
        if (icon is LucideIconKind kind) item.Icon = new LucideIcon { Icon = kind, Width = 16, Height = 16, Foreground = Brushes.White };
        item.Click += click;
        return item;
    }

    private void DocumentChanged(object? sender, EventArgs e) => Dispatcher.Invoke(Refresh);

    private void Refresh()
    {
        if (_selected is not null && !_document.Items.Contains(_selected)) _selected = null;
        _layout = _document.Layout(compose: !_cropping);
        _image.Source = RenderPreview();
        _previewLayer.Configure(_layout, _document.RenderContext);
        _previewLayer.Show(_preview ?? TextEditingPreview(), _tool == EditorTool.Select && _preview is not null);
        if (_fit && IsLoaded) FitImage(); else ApplyZoom();
        UpdateToolVisuals();
        UpdateInspector();
        UpdateToolbarExtras();
        UpdateUndoButtons();
    }

    private BitmapSource RenderPreview()
    {
        IEnumerable<Annotation> visible = _document.Items.Where(a =>
            !ReferenceEquals(a, _editingText) && !(_preview is not null && ReferenceEquals(a, _selected) && _tool == EditorTool.Select));
        return DocumentRenderer.Render(_document, visible, a => a is not TextAnnotation && ReferenceEquals(a, _selected), compose: !_cropping);
    }

    private Point PixelPoint(MouseEventArgs e)
    {
        Point display = e.GetPosition(_image);
        double outputX = display.X / Math.Max(1, _image.ActualWidth) * _layout.OutputWidth;
        double outputY = display.Y / Math.Max(1, _image.ActualHeight) * _layout.OutputHeight;
        Rect content = _layout.Content;
        return new Point(Math.Clamp(outputX - _layout.Offset.X, content.Left, content.Right), Math.Clamp(outputY - _layout.Offset.Y, content.Top, content.Bottom));
    }

    private void Image_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_textEditor is not null) CommitTextEdit();
        _image.Focus(); Point p = PixelPoint(e);
        if (_cropping) { BeginCropDrag(p); e.Handled = true; return; }
        _start = p; _penPoints.Clear(); _penPoints.Add(p); _image.CaptureMouse();
        if (_tool == EditorTool.Select) { _selected = HitTest(p); Refresh(); if (e.ClickCount==2 && _selected is TextAnnotation text) { _start=null;_image.ReleaseMouseCapture();EditText(text); } }
        else if (_tool == EditorTool.Text) { _start = null; _image.ReleaseMouseCapture(); AddText(p); }
        else if (_tool == EditorTool.Step) { _start = null; _image.ReleaseMouseCapture(); _document.Add(CreateStep(p)); }
        e.Handled = true;
    }

    private void Image_MouseMove(object sender, MouseEventArgs e)
    {
        if (_cropping) { if (_cropDragStart is not null && e.LeftButton == MouseButtonState.Pressed) UpdateCropDrag(PixelPoint(e)); return; }
        if (_start is null || e.LeftButton != MouseButtonState.Pressed) return;
        Point end=PixelPoint(e), start=_start.Value;
        if (_tool is EditorTool.Pen or EditorTool.Highlighter) _penPoints.Add(end);
        bool startedMove = _preview is null && _tool == EditorTool.Select;
        _preview = _tool == EditorTool.Select ? _selected?.Move(end - start) : CreateAnnotation(_tool, start, Constrain(_tool, start, end), preview: true);
        // The in-progress shape is drawn on a vector layer so dragging never re-renders the full-resolution bitmap.
        if (startedMove && _preview is not null) Refresh(); else _previewLayer.Show(_preview, _tool == EditorTool.Select);
    }

    private void Image_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropping) { if (_cropDragStart is not null) { UpdateCropDrag(PixelPoint(e)); _image.ReleaseMouseCapture(); EndCropDrag(); } return; }
        if (_start is null) return; Point end = PixelPoint(e); Point start = _start.Value; _image.ReleaseMouseCapture(); _start = null; _preview=null; Refresh();
        if (_tool == EditorTool.Select && _selected is not null) { Vector d = end - start; if (d.Length > 1) { Annotation moved = _selected.Move(d); _document.Replace(_selected, moved); _selected = moved; Refresh(); } return; }
        end = Constrain(_tool, start, end);
        if (_tool == EditorTool.Callout) { BeginCallout(start, end); return; }
        if ((end - start).Length < 2 && _tool is not (EditorTool.Pen or EditorTool.Highlighter or EditorTool.Magnify)) return;
        Annotation? added = CreateAnnotation(_tool, start, end, preview: false);
        if (added is not null) _document.Add(added);
    }

    private Annotation? HitTest(Point p) => _document.Items.Reverse().FirstOrDefault(a => a.Hit(p, 7 / Math.Max(_zoom, .1)));

    private static Point Constrain(EditorTool tool, Point start, Point end)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return end;
        return tool switch
        {
            EditorTool.Line or EditorTool.Arrow => GeometryUtil.Snap45(start, end),
            EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Redact or EditorTool.Spotlight => GeometryUtil.SnapSquare(start, end),
            _ => end
        };
    }

    private Annotation? CreateAnnotation(EditorTool tool, Point start, Point end, bool preview)
    {
        ToolStyle style = _styles.Get(tool);
        var box = new Rect(start, end);
        return tool switch
        {
            EditorTool.Arrow => new ArrowAnnotation(start, end, style.Stroke, style.Width).WithStyle(style),
            EditorTool.Line => new LineAnnotation(start, end, style.Stroke, style.Width).WithStyle(style),
            EditorTool.Rectangle => new RectangleAnnotation(box, style.Stroke, style.Width).WithStyle(style),
            EditorTool.Ellipse => new EllipseAnnotation(box, style.Stroke, style.Width).WithStyle(style),
            EditorTool.Redact => new RectangleAnnotation(box, style.Stroke, style.Width, true).WithStyle(style),
            EditorTool.Spotlight => new SpotlightAnnotation(box, style.CornerRadius),
            EditorTool.Pen when _penPoints.Count > 1 => new PenAnnotation(_penPoints.ToArray(), style.Stroke, style.Width).WithStyle(style),
            EditorTool.Highlighter when _penPoints.Count > 1 => new HighlighterAnnotation(_penPoints.ToArray(), style.Stroke, style.Width).WithStyle(style),
            EditorTool.Magnify => CreateMagnifier(start, end, style),
            EditorTool.Callout when preview => CalloutFromDrag(start, end, style) with { Text = "Callout" },
            _ => null
        };
    }

    private static MagnifyAnnotation CreateMagnifier(Point center, Point edge, ToolStyle style)
    {
        double dragged = (edge - center).Length;
        var magnifier = (MagnifyAnnotation)new MagnifyAnnotation(center, style.MagnifyDiameter / 2, style.MagnifyZoom, style.Stroke, style.Width).WithStyle(style);
        return dragged < 2 ? magnifier : magnifier with { Radius = Math.Max(8, dragged) };
    }

    private StepAnnotation CreateStep(Point center)
    {
        ToolStyle style = _styles.Get(EditorTool.Step);
        int number = _document.Items.OfType<StepAnnotation>().Select(x => x.Number).DefaultIfEmpty(0).Max() + 1;
        return (StepAnnotation)new StepAnnotation(center, number, style.Stroke, StepAnnotation.RadiusFor(style.Width)).WithStyle(style);
    }

    private void AddText(Point p) => BeginTextEdit(p, null);

    private void EditText(TextAnnotation text) => BeginTextEdit(text.Position, text);

    private void CancelDrag() { _start=null;_preview=null;_penPoints.Clear();Refresh(); }

    private void Done()
    {
        CommitPendingEdits();
        DoneRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UndoEdit() { CommitTextEdit(); if (_cropping) CancelCrop(); _selected = null; _document.Undo(); }
    private void RedoEdit() { CommitTextEdit(); if (_cropping) CancelCrop(); _selected = null; _document.Redo(); }

    private void UpdateUndoButtons()
    {
        if (_undoButton is not null) _undoButton.IsEnabled = _document.CanUndo;
        if (_redoButton is not null) _redoButton.IsEnabled = _document.CanRedo;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits();
        for (int attempt=1; attempt<=5; attempt++) try { Clipboard.SetImage(ImageExporter.PrepareForClipboard(_document.Render(), ExportOptions.Current)); _status.Text="Copied rendered PNG to the clipboard."; ShowToast("Copied to clipboard"); return; } catch (COMException) when(attempt<5) { await Task.Delay(attempt*70); } catch (Exception ex) { _status.Text=$"Could not copy: {ex.Message}"; return; }
        _status.Text="Clipboard is busy. Please try again.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits();
        try
        {
            string? path = ImageExporter.SaveWithDialog(Window.GetWindow(this), _document.Render(), ExportOptions.Current, _document.CreatedUtc.ToLocalTime(), 1);
            if (path is not null) _status.Text = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex) { _status.Text = $"Could not save: {ex.Message}"; }
    }

    private void AskClaude_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits();
        BetterShot.Ai.AskClaudeWindow.Show(Window.GetWindow(this), _document, Presentation.CapturePresentationManager.AskClaudeTitle(_document));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (TryBeginSpaceNavigation(Keyboard.FocusedElement is TextBox)) e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && (_spaceHeld || _isPanning)) { ResetNavigation(); e.Handled = true; return; }
        if (HandleShortcut(e.Key, Keyboard.Modifiers, Keyboard.FocusedElement is TextBox)) e.Handled = true;
    }

    /// <summary>Editor keyboard commands. Nothing fires while a text field (inline text, callout, or hex) owns the keyboard.</summary>
    private bool HandleShortcut(Key key, ModifierKeys modifiers, bool textInputFocused)
    {
        if (textInputFocused) return false;
        if (HandleOcrShortcut(key, modifiers)) return true;
        if (_cropping && modifiers == ModifierKeys.None && key == Key.Enter) { ApplyCrop(); return true; }
        if (_cropping && key == Key.Escape) { CancelCrop(); return true; }
        if (modifiers == ModifierKeys.Control && key == Key.Enter) { Done(); return true; }
        if (key == Key.Escape && _textGestureStart is not null) { CancelTextGesture(); return true; }
        if (key == Key.Escape && _start is not null) { _image.ReleaseMouseCapture(); CancelDrag(); return true; }
        if (modifiers == ModifierKeys.None && (key is Key.Delete or Key.Back) && _selected is not null) { DeleteSelected(); return true; }
        if (modifiers == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.Z: UndoEdit(); return true;
                case Key.Y: RedoEdit(); return true;
                case Key.C: Copy_Click(this, new RoutedEventArgs()); return true;
                case Key.S: Save_Click(this, new RoutedEventArgs()); return true;
                case Key.D: DuplicateSelected(); return true;
            }
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Z) { RedoEdit(); return true; }
        if (modifiers != ModifierKeys.None) return false;
        if (ToolForKey(key) is EditorTool tool) { SelectTool(tool); return true; }
        if (key == Key.I) { ToggleInspector(); return true; }
        if (key is Key.D0 or Key.NumPad0) { _fit = true; FitImage(); return true; }
        return false;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_spaceHeld) return;
        EndSpaceNavigation();
        e.Handled = true;
    }

    private void FitImage()
    {
        if (!IsLoaded) return; double aw=Math.Max(80,_scroller.ViewportWidth-8), ah=Math.Max(80,_scroller.ViewportHeight-8); if(double.IsNaN(aw)||double.IsNaN(ah))return;
        _zoom=Math.Min(1,Math.Min(aw/_layout.OutputWidth,ah/_layout.OutputHeight)); ApplyZoom();
    }
    private void SetZoom(double zoom){_fit=false;_zoom=Math.Clamp(zoom,.1,4);ApplyZoom();}
    private void ApplyZoom()
    {
        double w=Math.Max(1,_layout.OutputWidth*_zoom),h=Math.Max(1,_layout.OutputHeight*_zoom);
        _image.Width=w;_image.Height=h;_imageSurface.Width=w;_imageSurface.Height=h;_imageOverlay.Width=w;_imageOverlay.Height=h;_cropOverlay.Width=w;_cropOverlay.Height=h;
        RefreshTextOverlay(); UpdateCropOverlay();
        _dimensionsLabel.Text=$"{_layout.OutputWidth} × {_layout.OutputHeight} px";
        _zoomLabel.Text=$"{_zoom:P0}";
        UpdateZoomSegments();
    }
    private Vector ViewOffset => _layout.Offset;
    internal BitmapSource? ImageSourceForTest => _image.Source as BitmapSource;
    internal void RefreshForTest() => Refresh();
    internal void UseStylesForTest(EditorStyleStore styles) { _styles = styles; UpdateInspector(force: true); Refresh(); }
    private static Border Separator()=>new(){Width=1,Height=24,Background=Brush("#3A3B3F"),Margin=new Thickness(6,0,6,0),VerticalAlignment=VerticalAlignment.Center};
    private static SolidColorBrush Brush(string value)=>new((Color)ColorConverter.ConvertFromString(value));

    private sealed class PreviewLayer : FrameworkElement
    {
        private Annotation? _annotation;
        private bool _selected;
        private CompositionLayout _layout;
        private AnnotationRenderContext? _context;

        public void Configure(CompositionLayout layout, AnnotationRenderContext context) { _layout = layout; _context = context; }

        public void Show(Annotation? annotation, bool selected)
        {
            if (annotation is null && _annotation is null) return;
            _annotation = annotation; _selected = selected; InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_annotation is null || _context is null || ActualWidth <= 0) return;
            double scale = ActualWidth / Math.Max(1, _layout.OutputWidth);
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushClip(new RectangleGeometry(_layout.ContentInOutput));
            dc.PushTransform(new TranslateTransform(_layout.Offset.X, _layout.Offset.Y));
            if (_annotation is SpotlightAnnotation spotlight) spotlight.DrawOutline(dc, scale);
            _annotation.Draw(dc, _selected, _context);
            dc.Pop(); dc.Pop(); dc.Pop();
        }
    }
}
