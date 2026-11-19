using Microsoft.Win32;
using BetterShot.Controls;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Editing;

public sealed partial class EditorWindow : Window
{
    private readonly CaptureDocument _document;
    private EditorStyleStore _styles;
    private readonly Image _image = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true, Focusable = true };
    private readonly Canvas _imageOverlay = new() { Background = null, ClipToBounds = false };
    private readonly Grid _imageSurface = new() { ClipToBounds = false };
    private readonly PreviewLayer _previewLayer = new() { IsHitTestVisible = false };
    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(155, 160, 168)), FontSize = 12 };
    private readonly TextBlock _dimensionsLabel = new() { Foreground = new SolidColorBrush(Color.FromRgb(190, 194, 200)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock _zoomLabel = new() { Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Width = 44 };
    private readonly Button _styleButton;
    private EditorTool _tool = EditorTool.Arrow;
    private Annotation? _selected;
    private Point? _start;
    private readonly List<Point> _penPoints = [];
    private Annotation? _preview;
    private bool _fit = true;
    private double _zoom = 1;
    private CompositionLayout _layout;

    public EditorWindow(CaptureDocument document) : this(document, null) { }

    internal EditorWindow(CaptureDocument document, EditorStyleStore? styles)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _styles = styles ?? EditorStyleStore.Shared;
        _layout = _document.Layout();
        _styleButton = ActionButton("", "Style options for the current tool", (_, _) => OpenOptions(), 58);
        _styleButton.Height = 36;
        Title = "BetterShot Editor"; Width = 1180; Height = 790; MinWidth = 780; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brush("#151617"); Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        _toast = CreateToast();
        Content = BuildLayout();
        InitializeNavigation();
        InitializeOcr();
        _document.Changed += DocumentChanged;
        Closing += (_, _) => { CommitPendingEdits(); _styles.Flush(); };
        Deactivated += Editor_Deactivated;
        Closed += (_, _) => _document.Changed -= DocumentChanged;
        Loaded += (_, _) => { Refresh(); FitImage(); };
        _scroller.ScrollChanged += (_, e) => { if (_fit && (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)) FitImage(); };
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        SelectTool(EditorTool.Arrow);
    }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });

        var toolbar = new Border { Background = Brush("#202122"), BorderBrush = Brush("#303234"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 10, 12, 10), Height = 60 };
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        BuildToolButtons(left);
        left.Children.Add(Separator());
        left.Children.Add(_styleButton);
        left.Children.Add(Separator());
        left.Children.Add(IconButton(LucideIconKind.Undo2, "Undo (Ctrl+Z)", (_, _) => UndoEdit()));
        left.Children.Add(IconButton(LucideIconKind.Redo2, "Redo (Ctrl+Y)", (_, _) => RedoEdit()));
        bar.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(BuildOcrButton());
        right.Children.Add(IconButton(LucideIconKind.Copy, "Copy rendered image (Ctrl+C)", Copy_Click));
        right.Children.Add(IconButton(LucideIconKind.Save, "Save rendered image (Ctrl+S)", Save_Click));
        var done = IconTextButton(LucideIconKind.Check, "Done", "Close editor (Ctrl+Enter)", (_, _) => Done(), 82); done.Background = Brush("#FFD400"); done.Foreground = Brushes.Black;
        _doneButton = done;
        right.Children.Add(done); Grid.SetColumn(right, 2); bar.Children.Add(right);
        toolbar.Child = bar; root.Children.Add(toolbar);

        var canvasArea = new Grid { Background = Brush("#111213") };
        _scroller.Background = Brush("#111213"); _scroller.Padding = new Thickness(44); _scroller.Focusable = false;
        PreviewMouseDown += Scroller_PreviewMouseDown;
        _imageSurface.Children.Add(_image); _imageSurface.Children.Add(_previewLayer); _imageSurface.Children.Add(_imageOverlay); _imageSurface.Children.Add(_cropOverlay); _imageSurface.Children.Add(_ocrOverlay);
        var imageBorder = new Border { Background = Brushes.Black, BorderBrush = Brush("#4B4D50"), BorderThickness = new Thickness(1), Child = _imageSurface,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 28, Opacity = .55, ShadowDepth = 5 } };
        _image.MouseLeftButtonDown += Image_MouseDown; _image.MouseMove += Image_MouseMove; _image.MouseLeftButtonUp += Image_MouseUp;
        _image.MouseRightButtonUp += Image_MouseRightButtonUp;
        _image.LostMouseCapture += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_image.IsMouseCaptured) return;
            if (_start is not null) CancelDrag();
            if (_cropDragStart is not null) EndCropDrag();
        });
        _scroller.Content = imageBorder; canvasArea.Children.Add(_scroller); canvasArea.Children.Add(_toast); Grid.SetRow(canvasArea, 1); root.Children.Add(canvasArea);

        var footerBar = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        footerBar.ColumnDefinitions.Add(new ColumnDefinition()); footerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.TextTrimming = TextTrimming.CharacterEllipsis; _status.VerticalAlignment = VerticalAlignment.Center; footerBar.Children.Add(_status);
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        zoom.Children.Add(_dimensionsLabel);
        zoom.Children.Add(IconButton(LucideIconKind.Minus, "Zoom out (Ctrl+wheel)", (_, _) => SetZoom(_zoom / 1.2), 32, 28, 14));
        zoom.Children.Add(_zoomLabel);
        zoom.Children.Add(IconButton(LucideIconKind.Plus, "Zoom in (Ctrl+wheel)", (_, _) => SetZoom(_zoom * 1.2), 32, 28, 14));
        zoom.Children.Add(IconButton(LucideIconKind.Maximize, "Fit image to window (0)", (_, _) => { _fit = true; FitImage(); }, 32, 28, 14));
        var actualSize = ActionButton("100%", "Actual size (100%)", (_, _) => SetZoom(1), 46); actualSize.Height = 28; actualSize.FontSize = 12;
        zoom.Children.Add(actualSize);
        Grid.SetColumn(zoom, 1); footerBar.Children.Add(zoom);
        var footer = new Border { Background = Brush("#1B1C1D"), BorderBrush = Brush("#2A2B2D"), BorderThickness = new Thickness(0,1,0,0), Child = footerBar };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        return root;
    }

    private Button IconButton(LucideIconKind kind, string tip, RoutedEventHandler click, double width = 36, double height = 36, double iconSize = 18)
    {
        var icon = new LucideIcon { Icon = kind, Width = iconSize, Height = iconSize };
        var b = new Button { Content = icon, ToolTip = tip, Width = width, Height = height, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(5), Foreground = Brushes.White, Background = Brush("#303133"), BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
        icon.SetBinding(Control.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = b });
        System.Windows.Automation.AutomationProperties.SetName(b, tip.Split('(')[0].Trim()); b.Template = RoundedButtonTemplate(); b.Click += click; return b;
    }

    private Button IconTextButton(LucideIconKind kind, string text, string tip, RoutedEventHandler click, double width)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new LucideIcon { Icon = kind, Width = 18, Height = 18, Margin = new Thickness(0,0,7,0) };
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        row.Children.Add(icon); row.Children.Add(label);
        var b = new Button { Content = row, ToolTip = tip, Width = width, Height = 36, Margin = new Thickness(4,0,0,0), Padding = new Thickness(10,5,10,5), Foreground = Brushes.White, Background = Brush("#303133"), BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
        icon.SetBinding(Control.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = b }); label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = b });
        System.Windows.Automation.AutomationProperties.SetName(b, tip.Split('(')[0].Trim()); b.Template = RoundedButtonTemplate(); b.Click += click; return b;
    }

    private Button ActionButton(string text, string tip, RoutedEventHandler click, double width = 46)
    {
        var label = new TextBlock { Text = text, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var b = new Button { Content = label, ToolTip = tip, Width = width, Height = 40, Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(5),
            Foreground = Brushes.White, Background = Brush("#303133"), BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(b, tip.Split('(')[0].Trim());
        label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = b });
        b.Template = RoundedButtonTemplate(); b.Click += click; return b;
    }

    private static ControlTemplate RoundedButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "chrome"); border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9)); border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty)); border.SetValue(Border.BorderBrushProperty, Brushes.Transparent); border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content); template.VisualTree = border;
        var over = new Trigger { Property = IsMouseOverProperty, Value = true }; over.Setters.Add(new Setter(OpacityProperty, .84)); template.Triggers.Add(over);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true }; pressed.Setters.Add(new Setter(OpacityProperty, .62)); template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(OpacityProperty, .38)); template.Triggers.Add(disabled);
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true }; focus.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("#FFF07A"), "chrome")); template.Triggers.Add(focus); return template;
    }

    private static ContextMenu DarkMenu()=>new(){Background=Brush("#292A2C"),Foreground=Brushes.White,BorderBrush=Brush("#4A4B4E"),BorderThickness=new Thickness(1),Padding=new Thickness(3)};

    private static MenuItem DarkMenuItem(string header, LucideIconKind? icon, string? gesture, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = header, Padding = new Thickness(10, 7, 22, 7), Background = Brush("#292A2C"), Foreground = Brushes.White, InputGestureText = gesture ?? "" };
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
        _previewLayer.Show(_preview, _tool == EditorTool.Select);
        if (_fit && IsLoaded) FitImage(); else ApplyZoom();
        UpdateToolVisuals();
        UpdateStyleButton();
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
            EditorTool.Callout when preview => CalloutTemplate(end, start, style) with { Text = "Callout" },
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

    private void CommitPendingEdits()
    {
        CommitTextEdit();
        if (_cropping) ApplyCrop();
    }

    private void Done()
    {
        CommitPendingEdits();
        Close();
    }

    private void UndoEdit() { CommitTextEdit(); if (_cropping) CancelCrop(); _selected = null; _document.Undo(); }
    private void RedoEdit() { CommitTextEdit(); if (_cropping) CancelCrop(); _selected = null; _document.Redo(); }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits();
        for (int attempt=1; attempt<=5; attempt++) try { Clipboard.SetImage(_document.Render()); _status.Text="Copied rendered PNG to the clipboard."; return; } catch (COMException) when(attempt<5) { await Task.Delay(attempt*70); } catch (Exception ex) { _status.Text=$"Could not copy: {ex.Message}"; return; }
        _status.Text="Clipboard is busy. Please try again.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits();
        var d = new SaveFileDialog { Title="Save screenshot",Filter="PNG image (*.png)|*.png",DefaultExt=".png",AddExtension=true,FileName=$"BetterShot-{DateTime.Now:yyyy-MM-dd-HHmmss}.png" }; if(d.ShowDialog(this)!=true)return;
        string path=Path.GetFullPath(d.FileName), temp=Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try { var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(_document.Render())); using(var s=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) encoder.Save(s); File.Move(temp,path,true); _status.Text=$"Saved {Path.GetFileName(path)}."; }
        catch(Exception ex){_status.Text=$"Could not save: {ex.Message}";} finally { try { if(File.Exists(temp)) File.Delete(temp); } catch { } }
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

    /// <summary>Editor keyboard commands. Nothing fires while a text field (inline text or callout) owns the keyboard.</summary>
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
        if (!IsLoaded) return; double aw=Math.Max(100,_scroller.ViewportWidth-90), ah=Math.Max(100,_scroller.ViewportHeight-90); if(double.IsNaN(aw)||double.IsNaN(ah))return;
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
    }
    private Vector ViewOffset => _layout.Offset;
    internal BitmapSource? ImageSourceForTest => _image.Source as BitmapSource;
    internal void RefreshForTest() => Refresh();
    internal void UseStylesForTest(EditorStyleStore styles) { _styles = styles; Refresh(); }
    private static Border Separator()=>new(){Width=1,Height=24,Background=Brush("#47494B"),Margin=new Thickness(4,6,4,6)};
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
