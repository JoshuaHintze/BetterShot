using BetterShot.Controls;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BetterShot.Editing;

/// <summary>The rounded tool pill: grouped tools (a corner mark shows a group), quick colors, width, undo and redo.</summary>
public sealed partial class EditorSurface
{
    private sealed record ToolInfo(EditorTool Tool, string Name, LucideIconKind Icon, Key Key, string Hint, string NextLabel, string Noun)
    {
        internal string Shortcut => Key.ToString();
        internal string Tip => $"{Name} ({Shortcut})";
    }

    private sealed class ToolGroup(string name, EditorTool[] tools)
    {
        internal string Name { get; } = name;
        internal EditorTool[] Tools { get; } = tools;
        internal EditorTool Current { get; set; } = tools[0];
        internal Button Main { get; set; } = null!;
    }

    private static readonly ToolInfo[] ToolCatalog =
    [
        new(EditorTool.Select, "Select", LucideIconKind.MousePointer2, Key.V, "Click an annotation to select it · drag to move · right-click for more · Delete removes", "Click markup to change it", "markup"),
        new(EditorTool.Rectangle, "Rectangle", LucideIconKind.Square, Key.R, "Drag to draw a rectangle · Shift draws a square", "Next rectangle you draw", "rectangle"),
        new(EditorTool.Ellipse, "Ellipse", LucideIconKind.Circle, Key.O, "Drag to draw an ellipse · Shift draws a circle", "Next ellipse you draw", "ellipse"),
        new(EditorTool.Arrow, "Arrow", LucideIconKind.ArrowUpRight, Key.A, "Drag to draw an arrow · Shift snaps to 45°", "Next arrow you draw", "arrow"),
        new(EditorTool.Line, "Line", LucideIconKind.Slash, Key.L, "Drag to draw a line · Shift snaps to 45°", "Next line you draw", "line"),
        new(EditorTool.Pen, "Pen", LucideIconKind.PenLine, Key.P, "Drag to draw freehand", "Next stroke you draw", "stroke"),
        new(EditorTool.Highlighter, "Highlighter", LucideIconKind.Highlighter, Key.H, "Drag to highlight", "Next highlight you draw", "highlight"),
        new(EditorTool.Text, "Text", LucideIconKind.CaseSensitive, Key.T, "Click to type on the image · Ctrl+Enter finishes", "Next text you add", "text"),
        new(EditorTool.Callout, "Callout", LucideIconKind.MessageSquare, Key.Q, "Drag out a bubble, then type · drag the tail's handle to point at something", "Next callout you add", "callout"),
        new(EditorTool.Step, "Step", LucideIconKind.ListOrdered, Key.N, "Click to place the next step number", "Next number you place", "number"),
        new(EditorTool.Redact, "Redact", LucideIconKind.Grid3x3, Key.B, "Drag over private content · redactions are always burned into exports", "Next area you hide", "redaction"),
        new(EditorTool.Spotlight, "Spotlight", LucideIconKind.Flashlight, Key.S, "Drag to spotlight an area · everything else is dimmed", "Next spot you light", "spotlight"),
        new(EditorTool.Magnify, "Magnify", LucideIconKind.ZoomIn, Key.M, "Click to place a magnifier, or drag to set its size", "Next loupe you place", "loupe"),
        new(EditorTool.Crop, "Crop", LucideIconKind.Crop, Key.C, "Drag the handles or draw a new area · Enter applies · Esc cancels", "Drag the handles to crop", "crop")
    ];

    private static readonly double[] WidthChoices = [1, 2, 3, 4, 6, 8, 12, 16, 24];
    private static readonly double[] TextSizeChoices = [12, 16, 20, 24, 32, 40, 56, 72, 96];
    private readonly List<ToolGroup> _groups = [];
    private readonly Border _toolbarPill = new()
    {
        CornerRadius = new CornerRadius(24), Background = Brush("#EE1C1D20"), BorderBrush = Brush("#34363A"), BorderThickness = new Thickness(1), Padding = new Thickness(6),
        Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 20, Opacity = .4, ShadowDepth = 3, Direction = 270 }
    };
    private readonly StackPanel _toolRow = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _toolbarExtras = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _quickSwatches = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _widthLabel = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
    private readonly Border _widthGlyph = new() { Width = 16, Height = 3, CornerRadius = new CornerRadius(1.5), Background = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private Button? _widthButton;
    private Button? _backgroundButton;
    private Button? _undoButton;
    private Button? _redoButton;
    private EditorTool _toolBeforeCrop = EditorTool.Select;

    internal EditorTool CurrentToolForTest => _tool;
    internal bool HandleShortcutForTest(Key key, ModifierKeys modifiers = ModifierKeys.None, bool textInputFocused = false) =>
        HandleShortcut(key, modifiers, textInputFocused);
    internal EditorTool GroupToolForTest(EditorTool member) => _groups.First(group => group.Tools.Contains(member)).Current;

    private static ToolInfo Info(EditorTool tool) => ToolCatalog.First(info => info.Tool == tool);

    private static EditorTool? ToolForKey(Key key) => ToolCatalog.FirstOrDefault(info => info.Key == key)?.Tool;

    private ToolGroup? GroupOf(EditorTool tool) => _groups.FirstOrDefault(group => group.Tools.Contains(tool));

    private void BuildToolbarPill()
    {
        AddGroup(new ToolGroup("Select", [EditorTool.Select]));
        AddGroup(new ToolGroup("Shapes", [EditorTool.Rectangle, EditorTool.Ellipse]));
        AddGroup(new ToolGroup("Lines", [EditorTool.Arrow, EditorTool.Line]) { Current = EditorTool.Arrow });
        AddGroup(new ToolGroup("Drawing", [EditorTool.Pen, EditorTool.Highlighter]));
        AddGroup(new ToolGroup("Text", [EditorTool.Text, EditorTool.Callout]));
        AddGroup(new ToolGroup("Steps", [EditorTool.Step]));
        AddGroup(new ToolGroup("Effects", [EditorTool.Redact, EditorTool.Spotlight, EditorTool.Magnify]));
        AddGroup(new ToolGroup("Crop", [EditorTool.Crop]));
        _backgroundButton = IconButton(LucideIconKind.Image, "Background", (_, _) => OpenBackgroundOptions(), 44, 44, 20);
        _backgroundButton.Margin = new Thickness(1, 0, 1, 0);
        _toolRow.Children.Add(_backgroundButton);

        _toolbarExtras.Children.Add(Separator());
        _toolbarExtras.Children.Add(_quickSwatches);
        _toolbarExtras.Children.Add(ColorWheelButton());
        _widthButton = WidthDropdownButton();
        _toolbarExtras.Children.Add(_widthButton);
        _toolRow.Children.Add(_toolbarExtras);

        _toolRow.Children.Add(Separator());
        _undoButton = IconButton(LucideIconKind.Undo2, "Undo (Ctrl+Z)", (_, _) => UndoEdit(), 44, 44, 20);
        _redoButton = IconButton(LucideIconKind.Redo2, "Redo (Ctrl+Y)", (_, _) => RedoEdit(), 44, 44, 20);
        _toolRow.Children.Add(_undoButton);
        _toolRow.Children.Add(_redoButton);
        _toolbarPill.Child = _toolRow;
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(_toolbarPill, true);
    }

    private void AddGroup(ToolGroup group)
    {
        ToolInfo info = Info(group.Current);
        var icon = new LucideIcon { Icon = info.Icon, Width = 21, Height = 21 };
        var face = new Grid { Width = 44, Height = 44 };
        face.Children.Add(icon);
        icon.HorizontalAlignment = HorizontalAlignment.Center; icon.VerticalAlignment = VerticalAlignment.Center;
        if (group.Tools.Length > 1)
        {
            // The tiny corner triangle marks a group; its other tools are in the inspector, a right-click, or a second click.
            var corner = new Path { Data = Geometry.Parse("M 6 0 L 6 6 L 0 6 Z"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 6, 6) };
            face.Children.Add(corner);
        }
        var button = new Button
        {
            Content = face, Width = 44, Height = 44, Margin = new Thickness(1, 0, 1, 0), Padding = new Thickness(0), Background = Brushes.Transparent,
            Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = info.Tip, HorizontalContentAlignment = HorizontalAlignment.Center
        };
        icon.SetBinding(Control.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
        if (face.Children.Count > 1) ((Path)face.Children[1]).SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
        button.Template = ButtonTemplate(13);
        System.Windows.Automation.AutomationProperties.SetName(button, info.Name);
        button.Click += (_, _) =>
        {
            if (group.Tools.Length > 1 && group.Tools.Contains(_tool)) OpenGroupMenu(group);
            else SelectTool(group.Current);
        };
        button.MouseRightButtonUp += (_, e) => { if (group.Tools.Length > 1) { OpenGroupMenu(group); e.Handled = true; } };
        group.Main = button;
        _groups.Add(group);
        _toolRow.Children.Add(button);
    }

    private void OpenGroupMenu(ToolGroup group)
    {
        ContextMenu menu = DarkMenu();
        foreach (EditorTool tool in group.Tools)
        {
            ToolInfo info = Info(tool);
            MenuItem item = DarkMenuItem(info.Name, info.Icon, info.Shortcut, (_, _) => SelectTool(tool));
            if (tool == _tool) item.FontWeight = FontWeights.SemiBold;
            menu.Items.Add(item);
        }
        if (group.Name == "Effects") AddEffectsMenuActions(menu);
        menu.PlacementTarget = group.Main;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SelectTool(EditorTool tool)
    {
        CancelTextSelection(null);
        CommitTextEdit();
        if (_start is not null) { _image.ReleaseMouseCapture(); CancelDrag(); }
        if (tool == EditorTool.Crop) { BeginCrop(); return; }
        if (_cropping) ApplyCrop();
        _tool = tool;
        if (tool != EditorTool.Select && _selected is not null) { _selected = null; Refresh(); }
        UpdateToolVisuals();
        _status.Text = Info(tool).Hint;
        UpdateInspector();
        UpdateToolbarExtras();
    }

    private void UpdateToolVisuals()
    {
        foreach (ToolGroup group in _groups)
        {
            if (group.Tools.Contains(_tool)) group.Current = _tool;
            ToolInfo info = Info(group.Current);
            if (group.Main.Content is Grid { Children: [LucideIcon icon, ..] }) icon.Icon = info.Icon;
            group.Main.ToolTip = group.Tools.Length > 1 ? $"{info.Tip} · click again for {group.Name.ToLowerInvariant()}" : info.Tip;
            System.Windows.Automation.AutomationProperties.SetName(group.Main, info.Name);
            bool active = group.Tools.Contains(_tool);
            group.Main.Background = active ? Brush("#FFD400") : Brushes.Transparent;
            group.Main.Foreground = active ? Brushes.Black : Brushes.White;
        }
        if (_backgroundButton is not null) _backgroundButton.Foreground = _document.Background.IsEnabled ? Brush("#FFD400") : Brushes.White;
    }

    private static EditorTool ToolFor(Annotation annotation) => annotation switch
    {
        ArrowAnnotation => EditorTool.Arrow,
        LineAnnotation => EditorTool.Line,
        RectangleAnnotation rectangle => rectangle.Redaction ? EditorTool.Redact : EditorTool.Rectangle,
        EllipseAnnotation => EditorTool.Ellipse,
        PenAnnotation => EditorTool.Pen,
        HighlighterAnnotation => EditorTool.Highlighter,
        TextAnnotation text => text.IsCallout ? EditorTool.Callout : EditorTool.Text,
        StepAnnotation => EditorTool.Step,
        SpotlightAnnotation => EditorTool.Spotlight,
        MagnifyAnnotation => EditorTool.Magnify,
        _ => EditorTool.Select
    };

    private static bool HasColor(EditorTool tool, ToolStyle style) =>
        HasStyleOptions(tool) && tool != EditorTool.Spotlight && (tool != EditorTool.Redact || style.Redaction == RedactionStyle.Solid);

    private static bool UsesTextSize(EditorTool tool) => tool is EditorTool.Text or EditorTool.Callout;

    private static bool HasWidth(EditorTool tool, ToolStyle style) =>
        tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Arrow or EditorTool.Line or EditorTool.Pen or EditorTool.Highlighter or EditorTool.Step or EditorTool.Magnify
        || (tool == EditorTool.Redact && style.Redaction != RedactionStyle.Solid);

    private Button ColorWheelButton()
    {
        var wheel = new Ellipse
        {
            Width = 26, Height = 26,
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0xFF, 0x3B, 0x30), 0), new(Color.FromRgb(0xFF, 0xCC, 0x00), .2), new(Color.FromRgb(0x34, 0xC7, 0x59), .4),
                new(Color.FromRgb(0x5A, 0xC8, 0xFA), .6), new(Color.FromRgb(0x5E, 0x5C, 0xE6), .8), new(Color.FromRgb(0xFF, 0x2D, 0x92), 1)
            }, new Point(0, 0), new Point(1, 1)),
            Stroke = Brush("#55FFFFFF"), StrokeThickness = 1
        };
        var button = new Button { Content = wheel, Width = 38, Height = 44, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "More colors", HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Template = ButtonTemplate(13);
        System.Windows.Automation.AutomationProperties.SetName(button, "More colors");
        button.Click += (_, _) => ShowInspectorColors();
        return button;
    }

    private Button WidthDropdownButton()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_widthGlyph);
        row.Children.Add(_widthLabel);
        var chevron = new LucideIcon { Icon = LucideIconKind.ChevronDown, Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(chevron);
        var button = new Button { Content = row, Height = 36, Margin = new Thickness(4, 0, 2, 0), Padding = new Thickness(12, 0, 10, 0), Background = Brush("#2A2B2E"), Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "Line width" };
        _widthLabel.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
        chevron.SetBinding(Control.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
        button.Template = ButtonTemplate(12);
        System.Windows.Automation.AutomationProperties.SetName(button, "Line width");
        button.Click += (_, _) => OpenWidthMenu(button);
        return button;
    }

    private void OpenWidthMenu(Button anchor)
    {
        EditorTool tool = StyleTool;
        ToolStyle style = CurrentStyle;
        bool text = UsesTextSize(tool);
        ContextMenu menu = DarkMenu();
        foreach (double value in text ? TextSizeChoices : WidthChoices)
        {
            double chosen = value;
            bool current = Math.Abs((text ? style.FontSize : style.Width) - chosen) < .01;
            MenuItem item = DarkMenuItem($"{chosen:0} pt", null, null, (_, _) => ApplyStyleChange(s => text ? s with { FontSize = chosen } : s with { Width = chosen }, null, true));
            if (current) item.FontWeight = FontWeights.SemiBold;
            menu.Items.Add(item);
        }
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Refreshes the toolbar's quick colors (with the current one ringed) and the width or text-size dropdown.</summary>
    private void UpdateToolbarExtras()
    {
        EditorTool tool = StyleTool;
        ToolStyle style = HasStyleOptions(tool) ? CurrentStyle : _styles.Get(EditorTool.Arrow);
        bool colors = HasColor(tool, style);
        _quickSwatches.Children.Clear();
        foreach (Color color in _styles.QuickColors)
        {
            Color swatch = color;
            bool current = colors && SameColor(style.Stroke, swatch);
            var dot = new Ellipse { Width = 26, Height = 26, Fill = new SolidColorBrush(swatch), Stroke = current ? Brushes.White : Brush("#40FFFFFF"), StrokeThickness = current ? 2.5 : 1 };
            var button = new Button { Content = dot, Width = 38, Height = 44, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = ColorName(swatch), IsEnabled = colors, HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Template = ButtonTemplate(13);
            System.Windows.Automation.AutomationProperties.SetName(button, $"Quick color {ColorName(swatch)}");
            button.Click += (_, _) => ApplyStyleChange(s => s with { Stroke = swatch }, null, true);
            _quickSwatches.Children.Add(button);
        }
        if (_widthButton is null) return;
        bool text = UsesTextSize(tool);
        bool width = HasWidth(tool, style);
        _widthButton.Visibility = text || width ? Visibility.Visible : Visibility.Collapsed;
        _widthGlyph.Visibility = text ? Visibility.Collapsed : Visibility.Visible;
        _widthGlyph.Height = Math.Clamp(style.Width / 2, 1.5, 7);
        _widthLabel.Text = text ? $"{style.FontSize:0} pt" : $"{style.Width:0} pt";
        _widthButton.ToolTip = text ? "Text size" : "Line width";
    }
}
