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
    private enum Tool { Select, Arrow, Rectangle, Pen, Text, Step, Redact }
    private readonly CaptureDocument _document;
    private readonly Image _image = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true, Focusable = true };
    private readonly Canvas _imageOverlay = new() { Background = null, ClipToBounds = false };
    private readonly Grid _imageSurface = new() { ClipToBounds = false };
    private readonly PreviewLayer _previewLayer = new() { IsHitTestVisible = false };
    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Dictionary<Tool, Button> _tools = [];
    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(155, 160, 168)), FontSize = 12 };
    private readonly Button _colorButton;
    private readonly Button _widthButton;
    private Color _currentColor = Color.FromRgb(255, 69, 77);
    private double _currentWidth = 4;
    private Tool _tool = Tool.Arrow;
    private Annotation? _selected;
    private Point? _start;
    private readonly List<Point> _penPoints = [];
    private Annotation? _preview;
    private bool _fit = true;
    private double _zoom = 1;

    public EditorWindow(CaptureDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _colorButton = ActionButton("", "Annotation color", (_, _) => OpenColorMenu(), 42);
        _widthButton = ActionButton("4 px", "Stroke width", (_, _) => OpenWidthMenu(), 54);
        _colorButton.Height = 36; _widthButton.Height = 36;
        UpdateColorButton();
        Title = "BetterShot Editor"; Width = 1180; Height = 790; MinWidth = 780; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brush("#151617"); Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        Content = BuildLayout();
        InitializeNavigation();
        _document.Changed += DocumentChanged;
        Closing += (_, _) => CommitTextEdit();
        Deactivated += Editor_Deactivated;
        Closed += (_, _) => _document.Changed -= DocumentChanged;
        Loaded += (_, _) => { Refresh(); FitImage(); };
        _scroller.ScrollChanged += (_, e) => { if (_fit && (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)) FitImage(); };
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        SelectTool(Tool.Arrow);
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
        AddTool(left, Tool.Select, LucideIconKind.MousePointer2, "Select and move annotations (V)");
        AddTool(left, Tool.Arrow, LucideIconKind.ArrowUpRight, "Arrow (A)");
        AddTool(left, Tool.Rectangle, LucideIconKind.Square, "Rectangle (R)");
        AddTool(left, Tool.Pen, LucideIconKind.PenLine, "Freehand pen (P)");
        AddTool(left, Tool.Text, LucideIconKind.Type, "Text (T)");
        AddTool(left, Tool.Step, LucideIconKind.ListOrdered, "Numbered step (S)");
        AddTool(left, Tool.Redact, LucideIconKind.RectangleEllipsis, "Opaque redaction (B)");
        left.Children.Add(Separator());
        left.Children.Add(_colorButton); left.Children.Add(Spacer(3)); left.Children.Add(_widthButton);
        left.Children.Add(Separator());
        left.Children.Add(IconButton(LucideIconKind.Undo2, "Undo (Ctrl+Z)", (_, _) => { CommitTextEdit(); _selected = null; _document.Undo(); }));
        left.Children.Add(IconButton(LucideIconKind.Redo2, "Redo (Ctrl+Y)", (_, _) => { CommitTextEdit(); _selected = null; _document.Redo(); }));
        bar.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(IconButton(LucideIconKind.Copy, "Copy rendered image", Copy_Click));
        right.Children.Add(IconButton(LucideIconKind.Save, "Save rendered image", Save_Click));
        var done = IconTextButton(LucideIconKind.Check, "Done", "Close editor", (_, _) => Close(), 82); done.Background = Brush("#FFD400"); done.Foreground = Brushes.Black;
        right.Children.Add(done); Grid.SetColumn(right, 2); bar.Children.Add(right);
        toolbar.Child = bar; root.Children.Add(toolbar);

        var canvasArea = new Grid { Background = Brush("#111213") };
        _scroller.Background = Brush("#111213"); _scroller.Padding = new Thickness(44); _scroller.Focusable = false;
        PreviewMouseDown += Scroller_PreviewMouseDown;
        _previewLayer.PixelWidth = _document.Original.PixelWidth;
        _imageSurface.Children.Add(_image); _imageSurface.Children.Add(_previewLayer); _imageSurface.Children.Add(_imageOverlay);
        var imageBorder = new Border { Background = Brushes.Black, BorderBrush = Brush("#4B4D50"), BorderThickness = new Thickness(1), Child = _imageSurface,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 28, Opacity = .55, ShadowDepth = 5 } };
        _image.MouseLeftButtonDown += Image_MouseDown; _image.MouseMove += Image_MouseMove; _image.MouseLeftButtonUp += Image_MouseUp;
        _image.LostMouseCapture += (_, _) => Dispatcher.BeginInvoke(() => { if (_start is not null && !_image.IsMouseCaptured) CancelDrag(); });
        _scroller.Content = imageBorder; canvasArea.Children.Add(_scroller); Grid.SetRow(canvasArea, 1); root.Children.Add(canvasArea);

        var footerBar = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        footerBar.ColumnDefinitions.Add(new ColumnDefinition()); footerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.TextTrimming = TextTrimming.CharacterEllipsis; _status.VerticalAlignment = VerticalAlignment.Center; footerBar.Children.Add(_status);
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        zoom.Children.Add(IconButton(LucideIconKind.Maximize, "Fit image to window (0)", (_, _) => { _fit = true; FitImage(); }, 32, 28, 14));
        zoom.Children.Add(IconButton(LucideIconKind.Minus, "Zoom out", (_, _) => SetZoom(_zoom / 1.2), 32, 28, 14));
        zoom.Children.Add(IconButton(LucideIconKind.Plus, "Zoom in", (_, _) => SetZoom(_zoom * 1.2), 32, 28, 14));
        Grid.SetColumn(zoom, 1); footerBar.Children.Add(zoom);
        var footer = new Border { Background = Brush("#1B1C1D"), BorderBrush = Brush("#2A2B2D"), BorderThickness = new Thickness(0,1,0,0), Child = footerBar };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        return root;
    }

    private void AddTool(Panel panel, Tool tool, LucideIconKind icon, string tip)
    {
        var b = IconButton(icon, tip, (_, _) => SelectTool(tool)); _tools[tool] = b; panel.Children.Add(b);
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
        System.Windows.Automation.AutomationProperties.SetName(b, tip); b.Template = RoundedButtonTemplate(); b.Click += click; return b;
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
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true }; focus.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("#FFF07A"), "chrome")); template.Triggers.Add(focus); return template;
    }

    private void OpenColorMenu()
    {
        var menu=DarkMenu();
        foreach (var choice in new[] { new ColorChoice("Yellow",Color.FromRgb(255,212,0)),new ColorChoice("Red",Color.FromRgb(255,69,77)),new ColorChoice("Blue",Color.FromRgb(44,137,255)),new ColorChoice("Green",Color.FromRgb(32,195,108)),new ColorChoice("White",Colors.White) })
        {
            var row=new StackPanel{Orientation=Orientation.Horizontal}; row.Children.Add(new Border{Width=14,Height=14,CornerRadius=new CornerRadius(7),Background=new SolidColorBrush(choice.Value),Margin=new Thickness(0,0,9,0)}); row.Children.Add(new TextBlock{Text=choice.Name,Foreground=Brushes.White});
            var item=new MenuItem{Header=row,Padding=new Thickness(10,7,22,7),Background=Brush("#292A2C"),Foreground=Brushes.White}; item.Click+=(_,_)=>{_currentColor=choice.Value;UpdateColorButton();}; menu.Items.Add(item);
        }
        menu.PlacementTarget=_colorButton; menu.Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen=true;
    }

    private void OpenWidthMenu()
    {
        var menu=DarkMenu(); foreach(double width in new[]{2d,4d,7d,12d}) { var item=new MenuItem{Header=new TextBlock{Text=$"{width:0} px",Foreground=Brushes.White},Padding=new Thickness(12,7,30,7),Background=Brush("#292A2C"),Foreground=Brushes.White}; item.Click+=(_,_)=>{_currentWidth=width;SetButtonText(_widthButton,$"{width:0} px");};menu.Items.Add(item); }
        menu.PlacementTarget=_widthButton;menu.Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom;menu.IsOpen=true;
    }

    private static ContextMenu DarkMenu()=>new(){Background=Brush("#292A2C"),Foreground=Brushes.White,BorderBrush=Brush("#4A4B4E"),BorderThickness=new Thickness(1),Padding=new Thickness(3)};
    private void UpdateColorButton()
    {
        _colorButton.Content=new Border{Width=16,Height=16,CornerRadius=new CornerRadius(8),Background=new SolidColorBrush(_currentColor),BorderBrush=Brush("#D9DADC"),BorderThickness=new Thickness(1)};
    }
    private static void SetButtonText(Button button,string text){var label=new TextBlock{Text=text};label.SetBinding(TextBlock.ForegroundProperty,new System.Windows.Data.Binding(nameof(Button.Foreground)){Source=button});button.Content=label;}

    private void SelectTool(Tool tool)
    {
        CommitTextEdit();
        if (_start is not null) { _image.ReleaseMouseCapture(); CancelDrag(); }
        _tool = tool; foreach (var pair in _tools) { pair.Value.Background = pair.Key == tool ? Brush("#FFD400") : Brush("#303133"); pair.Value.Foreground = pair.Key == tool ? Brushes.Black : Brushes.White; }
        _status.Text = tool == Tool.Select ? "Click an annotation to select it · drag to move · Delete removes" : $"{tool} tool · drag on the image";
    }

    private void DocumentChanged(object? sender, EventArgs e) => Dispatcher.Invoke(Refresh);
    private void Refresh() { _image.Source = RenderPreview(); _previewLayer.Show(_preview, _tool == Tool.Select); if (_fit) FitImage(); RefreshTextOverlay(); }
    private BitmapSource RenderPreview()
    {
        if (_selected is null && _preview is null) return _document.Render();
        var v = new DrawingVisual();
        using (DrawingContext dc = v.RenderOpen())
        {
            dc.DrawImage(_document.Original, new Rect(0,0,_document.Original.PixelWidth,_document.Original.PixelHeight));
            foreach (var a in _document.Items)
            {
                if (ReferenceEquals(a, _editingText)) continue;
                if (_preview is not null && ReferenceEquals(a, _selected) && _tool == Tool.Select) continue;
                a.Draw(dc, a is not TextAnnotation && ReferenceEquals(a, _selected));
            }
        }
        var r = new RenderTargetBitmap(_document.Original.PixelWidth,_document.Original.PixelHeight,96,96,PixelFormats.Pbgra32); r.Render(v); r.Freeze(); return r;
    }

    private Point PixelPoint(MouseEventArgs e)
    {
        Point p = e.GetPosition(_image); return new Point(Math.Clamp(p.X / _image.ActualWidth * _document.Original.PixelWidth, 0, _document.Original.PixelWidth), Math.Clamp(p.Y / _image.ActualHeight * _document.Original.PixelHeight, 0, _document.Original.PixelHeight));
    }

    private void Image_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_textEditor is not null) CommitTextEdit();
        _image.Focus(); Point p = PixelPoint(e); _start = p; _penPoints.Clear(); _penPoints.Add(p); _image.CaptureMouse();
        if (_tool == Tool.Select) { _selected = _document.Items.Reverse().FirstOrDefault(a => a.Hit(p, 7 / Math.Max(_zoom, .1))); Refresh(); if (e.ClickCount==2 && _selected is TextAnnotation text) { _start=null;_image.ReleaseMouseCapture();EditText(text); } }
        else if (_tool == Tool.Text) { _start = null; _image.ReleaseMouseCapture(); AddText(p); }
        else if (_tool == Tool.Step) { int n = _document.Items.OfType<StepAnnotation>().Select(x => x.Number).DefaultIfEmpty(0).Max() + 1; _document.Add(new StepAnnotation(p, n, CurrentColor, Math.Max(15, CurrentWidth * 4))); _start = null; _image.ReleaseMouseCapture(); }
        e.Handled = true;
    }

    private void Image_MouseMove(object sender, MouseEventArgs e)
    {
        if (_start is null || e.LeftButton != MouseButtonState.Pressed) return;
        Point end=PixelPoint(e), start=_start.Value;
        if (_tool==Tool.Pen) _penPoints.Add(end);
        bool startedMove = _preview is null && _tool == Tool.Select;
        _preview = _tool switch { Tool.Select when _selected is not null => _selected.Move(end-start), Tool.Arrow => new ArrowAnnotation(start,end,CurrentColor,CurrentWidth), Tool.Rectangle => new RectangleAnnotation(new Rect(start,end),CurrentColor,CurrentWidth), Tool.Redact => new RectangleAnnotation(new Rect(start,end),CurrentColor,CurrentWidth,true), Tool.Pen when _penPoints.Count>1 => new PenAnnotation(_penPoints.ToArray(),CurrentColor,CurrentWidth), _=>null };
        // The in-progress shape is drawn on a vector layer so dragging never re-renders the full-resolution bitmap.
        if (startedMove && _preview is not null) Refresh(); else _previewLayer.Show(_preview, _tool == Tool.Select);
    }
    private void Image_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null) return; Point end = PixelPoint(e); Point start = _start.Value; _image.ReleaseMouseCapture(); _start = null; _preview=null; Refresh();
        if (_tool == Tool.Select && _selected is not null) { Vector d = end - start; if (d.Length > 1) { Annotation moved = _selected.Move(d); _document.Replace(_selected, moved); _selected = moved; Refresh(); } return; }
        if ((end - start).Length < 2 && _tool is not Tool.Pen) return;
        Annotation? added = _tool switch { Tool.Arrow => new ArrowAnnotation(start,end,CurrentColor,CurrentWidth), Tool.Rectangle => new RectangleAnnotation(new Rect(start,end),CurrentColor,CurrentWidth), Tool.Redact => new RectangleAnnotation(new Rect(start,end),CurrentColor,CurrentWidth,true), Tool.Pen when _penPoints.Count > 1 => new PenAnnotation(_penPoints.ToArray(),CurrentColor,CurrentWidth), _ => null };
        if (added is not null) _document.Add(added);
    }

    private void AddText(Point p) => BeginTextEdit(p, null);

    private void EditText(TextAnnotation text) => BeginTextEdit(text.Position, text);

    private void CancelDrag() { _start=null;_preview=null;_penPoints.Clear();Refresh(); }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
        for (int attempt=1; attempt<=5; attempt++) try { Clipboard.SetImage(_document.Render()); _status.Text="Copied rendered PNG to the clipboard."; return; } catch (COMException) when(attempt<5) { await Task.Delay(attempt*70); } catch (Exception ex) { _status.Text=$"Could not copy: {ex.Message}"; return; }
        _status.Text="Clipboard is busy. Please try again.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
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
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key==Key.Escape && _textGestureStart is not null) { CancelTextGesture(); e.Handled=true; }
        else if (e.Key==Key.Escape && _start is not null) { _image.ReleaseMouseCapture();CancelDrag();e.Handled=true; }
        else if (e.Key==Key.Delete && _selected is not null) { Annotation deleting=_selected; _selected=null; _document.Remove(deleting); e.Handled=true; }
        else if (Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.Z) { CommitTextEdit(); _selected=null; _document.Undo(); e.Handled=true; }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && (e.Key==Key.Y || (e.Key==Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))) { CommitTextEdit(); _selected=null; _document.Redo(); e.Handled=true; }
        else if (Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.C) { Copy_Click(this,new RoutedEventArgs());e.Handled=true; }
        else if (Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.S) { Save_Click(this,new RoutedEventArgs());e.Handled=true; }
        else if (Keyboard.Modifiers==ModifierKeys.None) { Tool? t=e.Key switch { Key.V=>Tool.Select,Key.A=>Tool.Arrow,Key.R=>Tool.Rectangle,Key.P=>Tool.Pen,Key.T=>Tool.Text,Key.S=>Tool.Step,Key.B=>Tool.Redact,_=>null}; if(t is not null){SelectTool(t.Value);e.Handled=true;} else if(e.Key==Key.D0||e.Key==Key.NumPad0){_fit=true;FitImage();} }
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
        _zoom=Math.Min(1,Math.Min(aw/_document.Original.PixelWidth,ah/_document.Original.PixelHeight)); ApplyZoom();
    }
    private void SetZoom(double zoom){_fit=false;_zoom=Math.Clamp(zoom,.1,4);ApplyZoom();}
    private void ApplyZoom(){double w=Math.Max(1,_document.Original.PixelWidth*_zoom),h=Math.Max(1,_document.Original.PixelHeight*_zoom);_image.Width=w;_image.Height=h;_imageSurface.Width=w;_imageSurface.Height=h;_imageOverlay.Width=w;_imageOverlay.Height=h;RefreshTextOverlay();_status.Text=$"{_document.Original.PixelWidth} × {_document.Original.PixelHeight} px · {_zoom:P0}";}
    private Color CurrentColor => _currentColor;
    private double CurrentWidth => _currentWidth;
    private static Border Separator()=>new(){Width=1,Height=24,Background=Brush("#47494B"),Margin=new Thickness(6,6,6,6)};
    private static FrameworkElement Spacer(double width)=>new Border{Width=width};
    private static SolidColorBrush Brush(string value)=>new((Color)ColorConverter.ConvertFromString(value));
    private sealed record ColorChoice(string Name, Color Value) { public override string ToString()=>$"●  {Name}"; }

    private sealed class PreviewLayer : FrameworkElement
    {
        private Annotation? _annotation;
        private bool _selected;
        public double PixelWidth { get; set; } = 1;
        public void Show(Annotation? annotation, bool selected)
        {
            if (annotation is null && _annotation is null) return;
            _annotation = annotation; _selected = selected; InvalidateVisual();
        }
        protected override void OnRender(DrawingContext dc)
        {
            if (_annotation is null || ActualWidth <= 0) return;
            double scale = ActualWidth / Math.Max(1, PixelWidth);
            dc.PushTransform(new ScaleTransform(scale, scale)); _annotation.Draw(dc, _selected); dc.Pop();
        }
    }
}
