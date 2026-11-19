using BetterShot.Controls;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterShot.Editing;

public sealed partial class EditorWindow
{
    private sealed record ToolInfo(EditorTool Tool, string Name, LucideIconKind Icon, Key Key, string Hint)
    {
        internal string Shortcut => Key.ToString();
        internal string Tip => $"{Name} ({Shortcut})";
    }

    private sealed class ToolGroup(string name, EditorTool[] tools)
    {
        internal string Name { get; } = name;
        internal EditorTool[] Tools { get; } = tools;
        internal EditorTool Current { get; set; } = tools[0];
        internal Border Chrome { get; set; } = null!;
        internal Button Main { get; set; } = null!;
        internal Button? Chevron { get; set; }
    }

    private static readonly ToolInfo[] ToolCatalog =
    [
        new(EditorTool.Select, "Select", LucideIconKind.MousePointer2, Key.V, "Click an annotation to select it · drag to move · right-click for more · Delete removes"),
        new(EditorTool.Rectangle, "Rectangle", LucideIconKind.Square, Key.R, "Drag to draw a rectangle · Shift draws a square"),
        new(EditorTool.Ellipse, "Ellipse", LucideIconKind.Circle, Key.O, "Drag to draw an ellipse · Shift draws a circle"),
        new(EditorTool.Arrow, "Arrow", LucideIconKind.ArrowUpRight, Key.A, "Drag to draw an arrow · Shift snaps to 45°"),
        new(EditorTool.Line, "Line", LucideIconKind.Slash, Key.L, "Drag to draw a line · Shift snaps to 45°"),
        new(EditorTool.Pen, "Pen", LucideIconKind.PenLine, Key.P, "Drag to draw freehand"),
        new(EditorTool.Highlighter, "Highlighter", LucideIconKind.Highlighter, Key.H, "Drag to highlight"),
        new(EditorTool.Text, "Text", LucideIconKind.Type, Key.T, "Click to type on the image · Ctrl+Enter finishes"),
        new(EditorTool.Callout, "Callout", LucideIconKind.MessageSquare, Key.Q, "Drag from the target to where the bubble goes, or click to place it"),
        new(EditorTool.Step, "Step Number", LucideIconKind.ListOrdered, Key.N, "Click to place the next step number"),
        new(EditorTool.Redact, "Redact", LucideIconKind.RectangleEllipsis, Key.B, "Drag over private content · redactions are always burned into exports"),
        new(EditorTool.Spotlight, "Spotlight", LucideIconKind.Flashlight, Key.S, "Drag to spotlight an area · everything else is dimmed"),
        new(EditorTool.Magnify, "Magnify", LucideIconKind.ZoomIn, Key.M, "Click to place a magnifier, or drag to set its size"),
        new(EditorTool.Crop, "Crop", LucideIconKind.Crop, Key.C, "Drag the handles or draw a new area · Enter applies · Esc cancels")
    ];

    private readonly List<ToolGroup> _groups = [];
    private Button? _backgroundButton;
    private EditorTool _toolBeforeCrop = EditorTool.Select;

    internal EditorTool CurrentToolForTest => _tool;
    internal bool HandleShortcutForTest(Key key, ModifierKeys modifiers = ModifierKeys.None, bool textInputFocused = false) =>
        HandleShortcut(key, modifiers, textInputFocused);
    internal EditorTool GroupToolForTest(EditorTool member) => _groups.First(group => group.Tools.Contains(member)).Current;

    private static ToolInfo Info(EditorTool tool) => ToolCatalog.First(info => info.Tool == tool);

    private static EditorTool? ToolForKey(Key key) => ToolCatalog.FirstOrDefault(info => info.Key == key)?.Tool;

    private void BuildToolButtons(Panel panel)
    {
        AddGroup(panel, new ToolGroup("Select", [EditorTool.Select]));
        AddGroup(panel, new ToolGroup("Shapes", [EditorTool.Rectangle, EditorTool.Ellipse]));
        AddGroup(panel, new ToolGroup("Lines", [EditorTool.Arrow, EditorTool.Line]) { Current = EditorTool.Arrow });
        AddGroup(panel, new ToolGroup("Drawing", [EditorTool.Pen, EditorTool.Highlighter]));
        AddGroup(panel, new ToolGroup("Text", [EditorTool.Text, EditorTool.Callout]));
        AddGroup(panel, new ToolGroup("Steps", [EditorTool.Step]));
        AddGroup(panel, new ToolGroup("Effects", [EditorTool.Redact, EditorTool.Spotlight, EditorTool.Magnify]));
        panel.Children.Add(Separator());
        AddGroup(panel, new ToolGroup("Crop", [EditorTool.Crop]));
        _backgroundButton = IconButton(LucideIconKind.Image, "Background", (_, _) => OpenBackgroundOptions(), 34);
        _backgroundButton.Margin = new Thickness(1, 0, 1, 0);
        panel.Children.Add(_backgroundButton);
    }

    private void AddGroup(Panel panel, ToolGroup group)
    {
        ToolInfo info = Info(group.Current);
        if (group.Tools.Length == 1)
        {
            group.Main = IconButton(info.Icon, info.Tip, (_, _) => SelectTool(group.Current), 34);
            group.Main.Margin = new Thickness(1, 0, 1, 0);
            group.Chrome = new Border { Child = group.Main };
        }
        else
        {
            group.Main = IconButton(info.Icon, info.Tip, (_, _) => SelectTool(group.Current), 30);
            group.Chevron = IconButton(LucideIconKind.ChevronDown, $"{group.Name} tools", (_, _) => OpenGroupMenu(group), 16, 36, 11);
            group.Main.Margin = new Thickness(0); group.Chevron.Margin = new Thickness(0);
            group.Main.Background = Brushes.Transparent; group.Chevron.Background = Brushes.Transparent;
            group.Chevron.Padding = new Thickness(0, 5, 3, 5);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(group.Main); row.Children.Add(group.Chevron);
            group.Chrome = new Border { Child = row, CornerRadius = new CornerRadius(9), Margin = new Thickness(1, 0, 1, 0), Background = Brush("#303133") };
        }
        _groups.Add(group);
        panel.Children.Add(group.Chrome);
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
        menu.PlacementTarget = group.Chrome;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SelectTool(EditorTool tool)
    {
        CommitTextEdit();
        if (_start is not null) { _image.ReleaseMouseCapture(); CancelDrag(); }
        if (tool == EditorTool.Crop) { BeginCrop(); return; }
        if (_cropping) ApplyCrop();
        _tool = tool;
        if (tool != EditorTool.Select && _selected is not null) { _selected = null; Refresh(); }
        UpdateToolVisuals();
        _status.Text = Info(tool).Hint;
        UpdateStyleButton();
    }

    private void UpdateToolVisuals()
    {
        foreach (ToolGroup group in _groups)
        {
            if (group.Tools.Contains(_tool)) group.Current = _tool;
            ToolInfo info = Info(group.Current);
            if (group.Main.Content is LucideIcon icon) icon.Icon = info.Icon;
            group.Main.ToolTip = info.Tip;
            System.Windows.Automation.AutomationProperties.SetName(group.Main, info.Name);
            bool active = group.Tools.Contains(_tool);
            Brush background = active ? Brush("#FFD400") : Brush("#303133");
            Brush foreground = active ? Brushes.Black : Brushes.White;
            if (group.Chevron is null) group.Main.Background = background; else group.Chrome.Background = background;
            group.Main.Foreground = foreground;
            if (group.Chevron is not null) group.Chevron.Foreground = foreground;
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
}
