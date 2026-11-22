using BetterShot.Controls;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BetterShot.Editing;

/// <summary>
/// The tool inspector card: which tool and what it affects (the next mark, or the selection), its variants, stroke color
/// (quick colors, palette, hex, screen eyedropper), width with a live preview, opacity, tool options, and style presets.
/// It docks beside the canvas or floats over it; both choices and visibility are remembered.
/// </summary>
public sealed partial class EditorSurface
{
    internal const double InspectorWidth = 240;
    private readonly Border _inspectorCard = new()
    {
        Width = InspectorWidth, CornerRadius = new CornerRadius(14), Background = Brush("#1E1F22"), BorderBrush = Brush("#303236"), BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private readonly Border _inspectorDock = new() { Margin = new Thickness(10, 0, 0, 10) };
    private readonly Border _inspectorFloatSlot = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 0, 10) };
    private (EditorTool Tool, Annotation? Selected) _inspectorContext = (EditorTool.Select, null);
    private bool _inspectorBuilt;
    private StrokePreview? _strokePreview;
    private bool _editingQuickColors;
    private int _quickColorSlot = -1;

    internal bool InspectorVisibleForTest => _inspectorCard.Parent is not null && _inspectorCard.IsVisible;
    internal bool InspectorDockedForTest => ReferenceEquals(_inspectorDock.Child, _inspectorCard);
    internal FrameworkElement InspectorForTest => _inspectorCard;
    internal void SetInspectorForTest(bool visible, bool docked) { _styles.ShowsInspector = visible; _styles.InspectorDocked = docked; PlaceInspector(); }
    internal void ApplyQuickColorForTest(int index) => ApplyStyleChange(style => style with { Stroke = _styles.QuickColors[index] }, null, true);
    internal void SetHexForTest(string hex) => CommitHex(hex);

    private void ToggleInspector()
    {
        _styles.ShowsInspector = !_styles.ShowsInspector;
        PlaceInspector();
    }

    private void ToggleInspectorDocking()
    {
        _styles.InspectorDocked = !_styles.InspectorDocked;
        PlaceInspector();
    }

    private void PlaceInspector()
    {
        bool show = _styles.ShowsInspector;
        Border? target = !show ? null : _styles.InspectorDocked ? _inspectorDock : _inspectorFloatSlot;
        if (!ReferenceEquals(_inspectorCard.Parent, target))
        {
            _inspectorDock.Child = null;
            _inspectorFloatSlot.Child = null;
            if (target is not null) target.Child = _inspectorCard;
        }
        bool floating = ReferenceEquals(target, _inspectorFloatSlot);
        _inspectorCard.VerticalAlignment = floating ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        _inspectorCard.Effect = floating ? new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 30, Opacity = .55, ShadowDepth = 6, Direction = 270 } : null;
        if (_inspectorButton is not null)
        {
            string tip = show ? "Hide inspector (I)" : "Show inspector (I)";
            _inspectorButton.ToolTip = tip;
            _inspectorButton.Foreground = show ? Brushes.White : Brush("#9A9CA2");
            System.Windows.Automation.AutomationProperties.SetName(_inspectorButton, tip.Split('(')[0].Trim());
        }
        if (show) UpdateInspector(force: !_inspectorBuilt);
        UpdateChromeLayout();
        UpdateCanvasPadding();
    }

    /// <summary>Rebuilds the card when the tool or selection changes (or when forced); slider drags only repaint the preview.</summary>
    private void UpdateInspector(bool force = false)
    {
        (EditorTool Tool, Annotation? Selected) context = (StyleTool, _selected);
        bool same = context.Tool == _inspectorContext.Tool && ReferenceEquals(context.Selected, _inspectorContext.Selected);
        if (!force && same && _inspectorBuilt) { _strokePreview?.InvalidateVisual(); return; }
        if (!same) { _editingQuickColors = false; _quickColorSlot = -1; }
        _inspectorContext = context;
        _inspectorBuilt = true;
        _inspectorCard.Child = BuildInspector(context.Tool);
    }

    private UIElement BuildInspector(EditorTool tool)
    {
        ToolInfo info = Info(tool);
        bool styled = HasStyleOptions(tool);
        ToolStyle style = styled ? CurrentStyle : _styles.Get(EditorTool.Arrow);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bool lineTool = tool is EditorTool.Arrow or EditorTool.Line;
        if (lineTool && GroupOf(tool) is { } lines)
        {
            var bar = new DockPanel { Margin = new Thickness(8, 7, 6, 5) };
            Button hide = IconButton(LucideIconKind.PanelLeftClose, "Hide inspector (I)", (_, _) => ToggleInspector(), 28, 28, 16);
            DockPanel.SetDock(hide, Dock.Right);
            bar.Children.Add(hide);
            var selector = VariantSegments(lines, tool);
            selector.Margin = new Thickness(0, 0, 4, 0);
            selector.ToolTip = _selected is null ? info.NextLabel : $"Selected {info.Noun}";
            bar.Children.Add(selector);
            grid.Children.Add(bar);
        }
        else grid.Children.Add(InspectorHeader(info));

        var top = new StackPanel();
        if (!lineTool && GroupOf(tool) is { Tools.Length: > 1 } group) top.Children.Add(VariantSegments(group, tool));
        top.Children.Add(new Border { Height = 1, Background = Brush("#2C2D31"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetRow(top, 1);
        grid.Children.Add(top);

        var body = new StackPanel { Margin = new Thickness(14, 4, 14, 10) };
        _strokePreview = null;
        if (styled) AddStyleSections(body, tool, style);
        else body.Children.Add(new TextBlock { Text = info.Hint, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#B5B7BC"), FontSize = 14, LineHeight = 20, Margin = new Thickness(0, 14, 0, 0) });
        var scroller = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        if (TryFindResource("EditorScrollBar") is Style scrollBar) scroller.Resources[typeof(ScrollBar)] = scrollBar;
        Grid.SetRow(scroller, 2);
        grid.Children.Add(scroller);

        FrameworkElement footer = InspectorFooter(tool, info, styled);
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);
        return grid;
    }

    private FrameworkElement InspectorHeader(ToolInfo info)
    {
        var header = new Grid { Margin = new Thickness(14, 12, 8, 9) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(10), Background = Brush("#3A3317"), BorderBrush = Brush("#5E5121"), BorderThickness = new Thickness(1),
            Child = new LucideIcon { Icon = info.Icon, Width = 19, Height = 19, Foreground = Brush("#FFD400"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        var titles = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(new TextBlock { Text = info.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(new Border
        {
            BorderBrush = Brush("#5A5C61"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = info.Shortcut, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("#D6D7DA") }
        });
        titles.Children.Add(name);
        var scope = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0), ToolTip = info.NextLabel };
        scope.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Brush("#FFD400"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 1, 8, 0) });
        string scopeText = _selected is not null && HasStyleOptions(info.Tool) ? $"Selected {info.Noun}" : info.NextLabel;
        scope.Children.Add(new TextBlock { Text = scopeText, FontSize = 13, Foreground = Brush("#C9CBD0"), TextTrimming = TextTrimming.CharacterEllipsis });
        titles.Children.Add(scope);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        Button hide = IconButton(LucideIconKind.PanelLeftClose, "Hide inspector (I)", (_, _) => ToggleInspector(), 30, 30, 16);
        hide.Foreground = Brush("#9A9CA2"); hide.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(hide, 2);
        header.Children.Add(hide);
        return header;
    }

    private FrameworkElement VariantSegments(ToolGroup group, EditorTool active)
    {
        var grid = new UniformGrid { Rows = 1, Columns = group.Tools.Length };
        foreach (EditorTool tool in group.Tools)
        {
            ToolInfo info = Info(tool);
            bool selected = tool == active;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            bool showLabel = group.Tools.Length <= 2;
            var icon = new LucideIcon { Icon = info.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, showLabel ? 6 : 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = info.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            content.Children.Add(icon);
            if (showLabel) content.Children.Add(label);
            var button = new Button
            {
                Content = content, Height = 30, Padding = new Thickness(4, 0, 4, 0), Cursor = Cursors.Hand, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = selected ? Brush("#2F3034") : Brushes.Transparent, Foreground = selected ? Brushes.White : Brush("#A7A9AF"), ToolTip = info.Tip
            };
            icon.SetBinding(Control.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
            if (showLabel) label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
            button.Template = ButtonTemplate(10);
            System.Windows.Automation.AutomationProperties.SetName(button, info.Name);
            button.Click += (_, _) => { if (_selected is not null) _selected = null; SelectTool(tool); };
            grid.Children.Add(button);
        }
        return new Border { Child = grid, Background = Brush("#141517"), CornerRadius = new CornerRadius(10), Padding = new Thickness(3), Margin = new Thickness(14, 0, 14, 9) };
    }

    private void AddStyleSections(StackPanel body, EditorTool tool, ToolStyle style)
    {
        if (tool == EditorTool.Redact)
        {
            body.Children.Add(SectionLabel("Style", null));
            body.Children.Add(Segments(Enum.GetValues<RedactionStyle>().Select(mode => (mode.ToString(), style.Redaction == mode, (Action)(() => ApplyStyleChange(s => s with { Redaction = mode }, null, true))))));
        }
        if (HasColor(tool, style))
        {
            string label = tool switch { EditorTool.Text => "Text color", EditorTool.Callout => "Bubble color", EditorTool.Redact => "Fill color", EditorTool.Magnify => "Border color", EditorTool.Step => "Badge color", _ => "Stroke color" };
            AddColorSection(body, label, style.Stroke, color => ApplyStyleChange(s => s with { Stroke = color }, null, true));
        }
        if (UsesTextSize(tool))
            body.Children.Add(InspectorSlider("Text size", style.FontSize, 10, 120, value => $"{value:0} pt", "font", value => s => s with { FontSize = value }));
        if (HasWidth(tool, style))
        {
            (string label, double minimum, double maximum) = tool switch
            {
                EditorTool.Step => ("Size", 2d, 20d), EditorTool.Redact => ("Strength", 2d, 40d), EditorTool.Magnify => ("Border", 1d, 16d), EditorTool.Highlighter => ("Width", 2d, 30d), _ => ("Width", 1d, 30d)
            };
            body.Children.Add(InspectorSlider(label, style.Width, minimum, maximum, value => $"{value:0} pt", "width", value => s => s with { Width = value }));
        }
        if (tool is not (EditorTool.Arrow or EditorTool.Line) && StrokePreview.Supports(tool))
        {
            _strokePreview = new StrokePreview(tool, () => CurrentStyle, _document.RenderContext) { Height = 46 };
            body.Children.Add(new Border { Child = _strokePreview, Background = Brush("#141517"), CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true, ToolTip = "Preview of the current style" });
        }
        if (tool is EditorTool.Rectangle or EditorTool.Ellipse)
        {
            body.Children.Add(SectionLabel("Fill", style.Fill is Color fill ? ColorName(fill) : "None"));
            body.Children.Add(SwatchRow(style.Fill, allowNone: true, color => ApplyStyleChange(s => s with { Fill = color }, null, true)));
        }
        if (tool == EditorTool.Text && style.TextBackground)
        {
            body.Children.Add(SectionLabel("Background color", style.Fill is Color fill ? ColorName(fill) : "Automatic"));
            body.Children.Add(SwatchRow(style.Fill, allowNone: true, color => ApplyStyleChange(s => s with { Fill = color }, null, true)));
        }
        if (tool is EditorTool.Rectangle or EditorTool.Spotlight)
            body.Children.Add(InspectorSlider("Corner radius", style.CornerRadius, 0, 40, value => $"{value:0} pt", "radius", value => s => s with { CornerRadius = value }));
        if (tool == EditorTool.Magnify)
        {
            body.Children.Add(SectionLabel("Zoom", $"{style.MagnifyZoom:0.#}Ã—"));
            body.Children.Add(Segments(MagnifyZoomPresets.Select(zoom => ($"{zoom:0.#}Ã—", Math.Abs(style.MagnifyZoom - zoom) < .01, (Action)(() => ApplyStyleChange(s => s with { MagnifyZoom = zoom }, null, true))))));
            body.Children.Add(InspectorSlider("Diameter", style.MagnifyDiameter, 40, 600, value => $"{value:0} pt", "diameter", value => s => s with { MagnifyDiameter = value }));
        }
        if (tool is not (EditorTool.Redact or EditorTool.Spotlight))
            body.Children.Add(InspectorSlider("Opacity", style.Opacity * 100, 10, 100, value => $"{value:0}%", "opacity", value => s => s with { Opacity = value / 100 }));
        if (tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Arrow or EditorTool.Line or EditorTool.Pen or EditorTool.Magnify)
            body.Children.Add(Switch("Dashed", style.Dashed, value => s => s with { Dashed = value }));
        if (tool is not (EditorTool.Redact or EditorTool.Spotlight or EditorTool.Highlighter))
            body.Children.Add(Switch("Shadow", style.Shadow, value => s => s with { Shadow = value }));
        if (tool == EditorTool.Text)
            body.Children.Add(Switch("Text background", style.TextBackground, value => s => s with { TextBackground = value }));
        if (tool == EditorTool.Redact)
        {
            Button find = IconTextButton(LucideIconKind.ScanText, "Find emails, numbers, and keys", "Find and cover emails, numbers, and keys automatically", (_, _) => _ = RedactDetectedAsync(), 0);
            find.Margin = new Thickness(0, 16, 0, 0); find.Height = 40; find.HorizontalAlignment = HorizontalAlignment.Stretch; find.Background = Brush("#2A2B2E");
            body.Children.Add(find);
        }
        AddPresetSection(body, tool);
    }

    private static TextBlock SectionHeading(string text) =>
        new() { Text = text, FontSize = 13, Foreground = Brush("#E6E7EA"), VerticalAlignment = VerticalAlignment.Center };

    private static FrameworkElement SectionLabel(string label, string? value)
    {
        var grid = new Grid { Margin = new Thickness(0, 11, 0, 6) };
        grid.Children.Add(SectionHeading(label));
        if (value is not null) grid.Children.Add(new TextBlock { Text = value, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
        return grid;
    }

    private void AddColorSection(StackPanel body, string label, Color current, Action<Color> apply)
    {
        body.Children.Add(SectionLabel(label, ColorName(current)));

        var quick = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        quick.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        quick.ColumnDefinitions.Add(new ColumnDefinition());
        quick.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        quick.Children.Add(new TextBlock { Text = "QUICK", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush("#8E9097"), VerticalAlignment = VerticalAlignment.Center });
        var quickRow = new StackPanel { Orientation = Orientation.Horizontal };
        for (int index = 0; index < _styles.QuickColors.Count; index++)
        {
            int slot = index;
            Color color = _styles.QuickColors[slot];
            bool ringed = _editingQuickColors ? slot == _quickColorSlot : SameColor(color, current);
            Button swatch = ColorDot(color, 22, ringed, _editingQuickColors ? $"Change quick color {slot + 1}" : ColorName(color), () =>
            {
                if (_editingQuickColors) { _quickColorSlot = slot; UpdateInspector(force: true); }
                else apply(color);
            });
            swatch.Margin = new Thickness(0, 0, 4, 0);
            quickRow.Children.Add(swatch);
        }
        Grid.SetColumn(quickRow, 1);
        quick.Children.Add(quickRow);
        Button customize = IconButton(LucideIconKind.SlidersHorizontal, _editingQuickColors ? "Finish customizing quick colors" : "Customize quick colors", (_, _) =>
        {
            _editingQuickColors = !_editingQuickColors;
            _quickColorSlot = _editingQuickColors ? 0 : -1;
            UpdateInspector(force: true);
        }, 28, 28, 15);
        customize.Foreground = _editingQuickColors ? Brush("#FFD400") : Brush("#A7A9AF");
        Grid.SetColumn(customize, 2);
        quick.Children.Add(customize);
        body.Children.Add(quick);
        if (_editingQuickColors)
            body.Children.Add(new TextBlock { Text = "Choose a quick color, then pick its new color below.", FontSize = 12, Foreground = Brush("#FFD400"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -4, 0, 10) });

        var palette = new UniformGrid { Rows = 1, Columns = PaletteColors.Length, Margin = new Thickness(0, 0, 0, 8) };
        foreach ((string name, Color value) in PaletteColors)
        {
            Color color = value;
            palette.Children.Add(ColorDot(color, 20, !_editingQuickColors && SameColor(color, current), name, () => ChooseColor(color, apply)));
        }
        body.Children.Add(palette);

        var custom = new Grid();
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        custom.ColumnDefinitions.Add(new ColumnDefinition());
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pickerFace = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        pickerFace.Children.Add(new Border { Width = 15, Height = 15, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(current), BorderBrush = Brush("#868891"), BorderThickness = new Thickness(1) });
        pickerFace.Children.Add(new TextBlock { Text = "Pick color", FontSize = 13, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var currentColor = new Button
        {
            Content = pickerFace,
            Height = 32, Padding = new Thickness(6, 0, 6, 0), Background = Brush("#292A2D"), Foreground = Brushes.White, BorderThickness = new Thickness(0), ToolTip = "Choose a custom color",
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        currentColor.Template = ButtonTemplate(8);
        System.Windows.Automation.AutomationProperties.SetName(currentColor, "Choose a custom color");
        currentColor.Click += (_, _) => ChooseCustomColor(current, color => ChooseColor(color, apply));
        custom.Children.Add(currentColor);
        var hexFrame = new Border { Height = 32, CornerRadius = new CornerRadius(8), Background = Brush("#141517"), BorderBrush = Brush("#303236"), BorderThickness = new Thickness(1), Margin = new Thickness(6, 0, 6, 0), Padding = new Thickness(8, 0, 6, 0) };

        var hex = new TextBox { Text = Hex(current), ToolTip = "Hex color", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxLength = 9 };
        if (TryFindResource("EditorField") is Style field) hex.Style = field;
        System.Windows.Automation.AutomationProperties.SetName(hex, "Hex color");
        hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitHex(hex.Text, apply); e.Handled = true; }
            else if (e.Key == Key.Escape) { hex.Text = Hex(current); _image.Focus(); e.Handled = true; }
        };
        hex.LostKeyboardFocus += (_, _) => { if (!string.Equals(hex.Text, Hex(current), StringComparison.OrdinalIgnoreCase)) CommitHex(hex.Text, apply); };
        hexFrame.Child = hex;
        Grid.SetColumn(hexFrame, 1);
        custom.Children.Add(hexFrame);
        Button pick = IconButton(LucideIconKind.Pipette, "Pick a color from the screen", (_, _) => _ = PickScreenColorAsync(apply), 32, 32, 16);
        pick.Background = Brush("#2A2B2E");
        Grid.SetColumn(pick, 2);
        custom.Children.Add(pick);
        body.Children.Add(custom);
    }

    private Button ColorDot(Color color, double size, bool ringed, string name, Action click)
    {
        bool dark = color.R + color.G + color.B < 90;
        var dot = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(color), Stroke = dark ? Brush("#5A5C61") : Brush("#30FFFFFF"), StrokeThickness = 1 };
        var face = new Grid { Width = size + 8, Height = size + 8 };
        if (ringed) face.Children.Add(new Ellipse { Width = size + 7, Height = size + 7, Stroke = Brushes.White, StrokeThickness = 2 });
        face.Children.Add(dot);
        var button = new Button { Content = face, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = name, HorizontalAlignment = HorizontalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Template = ButtonTemplate(size);
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>While customizing, a chosen color replaces the selected quick color; otherwise it is applied.</summary>
    private void ChooseColor(Color color, Action<Color> apply)
    {
        if (_editingQuickColors && _quickColorSlot >= 0)
        {
            _styles.SetQuickColor(_quickColorSlot, color);
            UpdateToolbarExtras();
            UpdateInspector(force: true);
            return;
        }
        apply(color);
    }

    private void CommitHex(string text) => CommitHex(text, color => ApplyStyleChange(s => s with { Stroke = color }, null, true));

    private void CommitHex(string text, Action<Color> apply)
    {
        if (TryParseHex(text, out Color color)) ChooseColor(color, apply);
        else { _status.Text = "Enter a hex color such as #FF4245."; UpdateInspector(force: true); }
    }

    internal static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        string value = (text ?? "").Trim().TrimStart('#');
        if (value.Length == 3) value = string.Concat(value.Select(character => new string(character, 2)));
        if (value.Length == 8) value = value[2..];
        if (value.Length != 6 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private async Task PickScreenColorAsync(Action<Color> apply)
    {
        CommitTextEdit();
        Color? picked = await ScreenColorPicker.PickAsync();
        if (_detached) return;
        if (picked is Color color) ChooseColor(color, apply);
        else _status.Text = "Color picking canceled.";
    }

    private FrameworkElement SwatchRow(Color? current, bool allowNone, Action<Color?> choose)
    {
        var row = new WrapPanel();
        if (allowNone)
        {
            var none = new Button { Content = new LucideIcon { Icon = LucideIconKind.Ban, Width = 18, Height = 18, Foreground = Brush("#C9CBD0") }, Width = 38, Height = 38, Padding = new Thickness(0), Background = current is null ? Brush("#3A3B3F") : Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "None", HorizontalContentAlignment = HorizontalAlignment.Center };
            none.Template = ButtonTemplate(19);
            System.Windows.Automation.AutomationProperties.SetName(none, "No fill");
            none.Click += (_, _) => choose(null);
            row.Children.Add(none);
        }
        foreach (Color color in _styles.QuickColors.Concat(PaletteColors.Select(entry => entry.Value)))
        {
            Color value = color;
            row.Children.Add(ColorDot(value, 24, current is Color selected && SameColor(selected, value), ColorName(value), () => choose(value)));
        }
        return row;
    }

    private FrameworkElement Segments(IEnumerable<(string Label, bool Selected, Action Click)> choices)
    {
        var items = choices.ToArray();
        var grid = new UniformGrid { Rows = 1, Columns = Math.Max(1, items.Length) };
        foreach ((string label, bool selected, Action click) in items)
        {
            var text = new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var button = new Button { Content = text, Height = 36, Background = selected ? Brush("#2F3034") : Brushes.Transparent, Foreground = selected ? Brushes.White : Brush("#A7A9AF"), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Center };
            text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
            button.Template = ButtonTemplate(9);
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.Click += (_, _) => click();
            grid.Children.Add(button);
        }
        return new Border { Child = grid, Background = Brush("#141517"), CornerRadius = new CornerRadius(12), Padding = new Thickness(4) };
    }

    private FrameworkElement InspectorSlider(string label, double value, double minimum, double maximum, Func<double, string> format, string key, Func<double, Func<ToolStyle, ToolStyle>> change)
    {
        var panel = new StackPanel();
        var heading = new Grid { Margin = new Thickness(0, 11, 0, 4) };
        heading.Children.Add(SectionHeading(label));
        var valueText = new TextBlock { Text = format(value), FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (key == "width" && StyleTool is (EditorTool.Arrow or EditorTool.Line))
        {
            _strokePreview = new StrokePreview(StyleTool, () => CurrentStyle, _document.RenderContext)
            {
                Width = 68, Height = 22, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false
            };
            heading.Children.Add(_strokePreview);
        }
        heading.Children.Add(valueText);
        panel.Children.Add(heading);
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = Math.Clamp(value, minimum, maximum), IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = 4 };
        if (TryFindResource("EditorSlider") is Style style) slider.Style = style;
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        slider.PreviewMouseLeftButtonDown += (_, _) => _styleGesture++;
        slider.PreviewKeyDown += (_, _) => _styleGesture++;
        slider.ValueChanged += (_, e) =>
        {
            valueText.Text = format(e.NewValue);
            ApplyStyleChange(change(e.NewValue), $"{key}:{_styleGesture}", false);
        };
        panel.Children.Add(slider);
        return panel;
    }

    private CheckBox Switch(string text, bool value, Func<bool, Func<ToolStyle, ToolStyle>> change)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 9, 0, 0), FontSize = 13 };
        if (TryFindResource("EditorSwitch") is Style style) box.Style = style;
        box.Checked += (_, _) => ApplyStyleChange(change(true), null, false);
        box.Unchecked += (_, _) => ApplyStyleChange(change(false), null, false);
        return box;
    }

    private void AddPresetSection(StackPanel body, EditorTool tool)
    {
        var heading = new TextBlock { Text = "Style presets", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 7) };
        body.Children.Add(heading);
        var wrap = new WrapPanel();
        Button add = IconButton(LucideIconKind.Plus, "Save Style as Preset", (_, _) => SaveCurrentStyleAsPreset(), 32, 32, 16);
        add.Background = Brush("#26272A"); add.Template = ButtonTemplate(20); add.Margin = new Thickness(0, 0, 8, 8);
        wrap.Children.Add(add);
        foreach (ToolStyle preset in _styles.Presets(tool))
        {
            ToolStyle chosen = preset;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(chosen.Stroke), Stroke = Brush("#55FFFFFF"), StrokeThickness = 1, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = PresetSummary(tool, chosen), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var chip = new Button { Content = content, Height = 32, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(9, 0, 10, 0), Background = Brush("#26272A"), Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "Apply preset Â· right-click to remove" };
            chip.Template = ButtonTemplate(20);
            System.Windows.Automation.AutomationProperties.SetName(chip, $"Apply preset {PresetSummary(tool, chosen)}");
            chip.Click += (_, _) => ApplyStyleChange(_ => chosen, null, true);
            ContextMenu menu = DarkMenu();
            menu.Items.Add(DarkMenuItem("Remove preset", LucideIconKind.Trash2, null, (_, _) => { _styles.RemovePreset(tool, chosen); UpdateInspector(force: true); }));
            chip.ContextMenu = menu;
            wrap.Children.Add(chip);
        }
        body.Children.Add(wrap);
    }

    private FrameworkElement InspectorFooter(EditorTool tool, ToolInfo info, bool styled)
    {
        var panel = new StackPanel { Margin = new Thickness(14, 7, 8, 8) };
        var remember = new Grid();
        remember.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        remember.ColumnDefinitions.Add(new ColumnDefinition());
        remember.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (styled) remember.ToolTip = $"{info.Name} remembers these settings for next time";
        bool docked = _styles.InspectorDocked;
        Button dock = IconButton(docked ? LucideIconKind.PictureInPicture2 : LucideIconKind.PanelLeft, docked ? "Float the inspector over the canvas" : "Dock the inspector beside the canvas", (_, _) => ToggleInspectorDocking(), 30, 30, 16);
        dock.Foreground = Brush("#9A9CA2"); dock.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(dock, 2);
        remember.Children.Add(dock);
        panel.Children.Add(remember);
        string? hint = tool switch
        {
            EditorTool.Arrow or EditorTool.Line => "Drag to snap to 45Â°",
            EditorTool.Rectangle or EditorTool.Redact or EditorTool.Spotlight => "Drag for a square",
            EditorTool.Ellipse => "Drag for a circle",
            _ => null
        };
        if (hint is not null) remember.ToolTip = $"Hold Shift: {hint}";
        return new Border { Child = panel, BorderBrush = Brush("#2C2D31"), BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    /// <summary>Draws a sample of the current tool style the way it will appear on the capture.</summary>
    private sealed class StrokePreview(EditorTool tool, Func<ToolStyle> style, AnnotationRenderContext context) : FrameworkElement
    {
        internal static bool Supports(EditorTool tool) => tool is EditorTool.Arrow or EditorTool.Line or EditorTool.Rectangle or EditorTool.Ellipse
            or EditorTool.Pen or EditorTool.Highlighter or EditorTool.Text or EditorTool.Callout or EditorTool.Step;

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth, height = ActualHeight;
            if (width <= 0 || height <= 0) return;
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
            ToolStyle current = style();
            ToolStyle sample = current with { Width = Math.Min(current.Width, 16), FontSize = Math.Min(current.FontSize, tool == EditorTool.Callout ? 18 : 30) };
            Annotation? annotation = tool switch
            {
                EditorTool.Arrow => new ArrowAnnotation(new Point(width * .2, height * .66), new Point(width * .8, height * .36), sample.Stroke, sample.Width).WithStyle(sample),
                EditorTool.Line => new LineAnnotation(new Point(width * .2, height * .66), new Point(width * .8, height * .36), sample.Stroke, sample.Width).WithStyle(sample),
                EditorTool.Rectangle => new RectangleAnnotation(new Rect(width * .22, height * .22, width * .56, height * .56), sample.Stroke, sample.Width).WithStyle(sample),
                EditorTool.Ellipse => new EllipseAnnotation(new Rect(width * .22, height * .2, width * .56, height * .6), sample.Stroke, sample.Width).WithStyle(sample),
                EditorTool.Pen => new PenAnnotation(Squiggle(width, height), sample.Stroke, sample.Width).WithStyle(sample),
                EditorTool.Highlighter => new HighlighterAnnotation([new Point(width * .2, height * .5), new Point(width * .8, height * .5)], sample.Stroke, sample.Width).WithStyle(sample),
                EditorTool.Text => new TextAnnotation(new Point(width * .14, height * .5 - sample.FontSize * .7), "Aa Text", sample.Stroke, sample.FontSize).WithStyle(sample),
                EditorTool.Callout => new TextAnnotation(new Point(width * .36, height * .5 - sample.FontSize * .7), "Callout", sample.Stroke, sample.FontSize).WithStyle(sample) is TextAnnotation callout
                    ? callout with { Tail = new Point(width * .12, height * .86) } : null,
                EditorTool.Step => new StepAnnotation(new Point(width / 2, height / 2), 1, sample.Stroke, Math.Min(StepAnnotation.RadiusFor(sample.Width), height * .4)).WithStyle(sample) is StepAnnotation step
                    ? step with { Radius = Math.Min(step.Radius, height * .4) } : null,
                _ => null
            };
            annotation?.Draw(dc, false, context);
            dc.Pop();
        }

        private static Point[] Squiggle(double width, double height) =>
            Enumerable.Range(0, 25).Select(index => new Point(width * (.18 + .64 * index / 24), height * (.5 + .22 * Math.Sin(index / 24d * Math.PI * 2.4)))).ToArray();
    }
}
