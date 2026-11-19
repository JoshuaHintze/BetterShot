using BetterShot.Controls;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterShot.Editing;

public sealed partial class EditorWindow
{
    private static readonly (string Name, Color Value)[] QuickColors =
    [
        ("Red", ToolStyle.DefaultStroke), ("Orange", Color.FromRgb(255, 149, 0)), ("Yellow", Color.FromRgb(255, 212, 0)),
        ("Green", Color.FromRgb(32, 195, 108)), ("Blue", Color.FromRgb(44, 137, 255)), ("Purple", Color.FromRgb(175, 82, 222)),
        ("White", Colors.White), ("Black", Colors.Black)
    ];
    private static readonly double[] MagnifyZoomPresets = [1.5, 2, 3, 4];
    private Popup? _optionsPopup;
    private Popup? _backgroundPopup;
    private int _styleGesture;

    internal Annotation? SelectedForTest => _selected;
    internal EditorStyleStore StylesForTest => _styles;
    internal void SelectAnnotationForTest(Annotation? annotation)
    {
        if (annotation is not null && _tool != EditorTool.Select) SelectTool(EditorTool.Select);
        _selected = annotation;
        Refresh();
    }
    internal void ApplyStyleForTest(Func<ToolStyle, ToolStyle> change, string? coalesceKey = null) => ApplyStyleChange(change, coalesceKey, rebuild: false);
    internal void SavePresetForTest() => SaveCurrentStyleAsPreset();
    internal void ApplyPresetForTest(ToolStyle preset) => ApplyStyleChange(_ => preset, null, rebuild: false);
    internal void OpenOptionsForTest() => OpenOptions();
    internal void OpenBackgroundOptionsForTest() => OpenBackgroundOptions();
    internal Popup? OptionsPopupForTest => _optionsPopup;
    internal Popup? BackgroundPopupForTest => _backgroundPopup;

    private EditorTool StyleTool => _selected is not null ? ToolFor(_selected) : _tool;
    private ToolStyle CurrentStyle => _selected is not null ? _selected.CaptureStyle(_styles.Get(StyleTool)).Normalized() : _styles.Get(StyleTool);
    private static bool HasStyleOptions(EditorTool tool) => tool is not (EditorTool.Select or EditorTool.Crop);

    private void UpdateStyleButton()
    {
        EditorTool tool = StyleTool;
        bool enabled = HasStyleOptions(tool);
        ToolStyle style = enabled ? CurrentStyle : _styles.Get(EditorTool.Rectangle);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        Color swatch = tool == EditorTool.Spotlight ? Color.FromArgb(140, 0, 0, 0) : style.Stroke;
        row.Children.Add(new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(swatch), BorderBrush = Brush("#D9DADC"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0) });
        var label = new TextBlock { Text = StyleSummary(tool, style), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = _styleButton });
        row.Children.Add(label);
        _styleButton.Content = row;
        _styleButton.IsEnabled = enabled;
        _styleButton.ToolTip = enabled ? $"{Info(tool).Name} options" : "Choose a drawing tool or select an annotation to change its style";
    }

    private static string StyleSummary(EditorTool tool, ToolStyle style) => tool switch
    {
        EditorTool.Text or EditorTool.Callout => $"{style.FontSize:0}",
        EditorTool.Magnify => $"{style.MagnifyZoom:0.#}×",
        EditorTool.Spotlight => "",
        _ => $"{style.Width:0}"
    };

    private void OpenOptions()
    {
        CommitTextEdit();
        if (!HasStyleOptions(StyleTool)) return;
        _optionsPopup ??= CreatePopup(_styleButton);
        RebuildOptions();
        _optionsPopup.IsOpen = true;
    }

    private void RebuildOptions()
    {
        if (_optionsPopup is null) return;
        EditorTool tool = StyleTool;
        if (!HasStyleOptions(tool)) { _optionsPopup.IsOpen = false; return; }
        _optionsPopup.Child = PopupChrome(BuildOptionsPanel(tool, CurrentStyle));
    }

    private void ApplyStyleChange(Func<ToolStyle, ToolStyle> change, string? coalesceKey, bool rebuild)
    {
        EditorTool tool = StyleTool;
        if (!HasStyleOptions(tool)) return;
        ToolStyle updated = change(CurrentStyle).Normalized();
        _styles.Set(tool, updated);
        if (_selected is not null)
        {
            Annotation restyled = _selected.WithStyle(updated);
            if (restyled != _selected)
            {
                Annotation old = _selected;
                _selected = restyled;
                _document.Replace(old, restyled, coalesceKey is null ? null : "style:" + coalesceKey);
            }
        }
        UpdateStyleButton();
        if (rebuild) RebuildOptions();
    }

    private void SaveCurrentStyleAsPreset()
    {
        EditorTool tool = StyleTool;
        if (!HasStyleOptions(tool)) return;
        _styles.AddPreset(tool, CurrentStyle);
        _status.Text = $"Saved a {Info(tool).Name.ToLowerInvariant()} style preset.";
        RebuildOptions();
    }

    private StackPanel BuildOptionsPanel(EditorTool tool, ToolStyle style)
    {
        var panel = new StackPanel { Width = 264 };
        panel.Children.Add(new TextBlock { Text = $"{Info(tool).Name} Options", FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Brushes.White });
        bool solidRedaction = tool == EditorTool.Redact && style.Redaction == RedactionStyle.Solid;

        if (tool == EditorTool.Redact)
            AddSection(panel, "Style", ChoiceRow(Enum.GetValues<RedactionStyle>().Select(mode =>
                (mode.ToString(), style.Redaction == mode, (Action)(() => ApplyStyleChange(s => s with { Redaction = mode }, null, true)))), 76));
        if (tool != EditorTool.Spotlight && (tool != EditorTool.Redact || solidRedaction))
        {
            string label = tool switch { EditorTool.Text => "Text color", EditorTool.Callout => "Bubble color", EditorTool.Redact => "Fill color", _ => "Stroke color" };
            AddSection(panel, label, ColorRow(style.Stroke, allowNone: false, noneLabel: "", color => ApplyStyleChange(s => s with { Stroke = color!.Value }, null, true)));
        }
        if (tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Arrow or EditorTool.Line or EditorTool.Pen or EditorTool.Highlighter or EditorTool.Step or EditorTool.Magnify
            || (tool == EditorTool.Redact && !solidRedaction))
        {
            string label = tool switch { EditorTool.Step => "Size", EditorTool.Redact => "Strength", EditorTool.Magnify => "Border", _ => "Stroke width" };
            AddSection(panel, $"{label} · {style.Width:0} px", ChoiceRow(ToolStyle.WidthPresets.Select(width =>
                ($"{width:0}", Math.Abs(style.Width - width) < .01, (Action)(() => ApplyStyleChange(s => s with { Width = width }, null, true)))), 46));
        }
        if (tool is EditorTool.Rectangle or EditorTool.Ellipse)
            AddSection(panel, "Fill", ColorRow(style.Fill, allowNone: true, noneLabel: "None", color => ApplyStyleChange(s => s with { Fill = color }, null, true)));
        if (tool == EditorTool.Text && style.TextBackground)
            AddSection(panel, "Background color", ColorRow(style.Fill, allowNone: true, noneLabel: "Automatic", color => ApplyStyleChange(s => s with { Fill = color }, null, true)));
        if (tool is EditorTool.Text or EditorTool.Callout)
            AddSection(panel, "Font size", SliderRow(style.FontSize, 10, 120, "0", " px", "font", value => s => s with { FontSize = value }));
        if (tool is EditorTool.Rectangle or EditorTool.Spotlight)
            AddSection(panel, "Corner radius", SliderRow(style.CornerRadius, 0, 40, "0", " px", "radius", value => s => s with { CornerRadius = value }));
        if (tool == EditorTool.Magnify)
        {
            AddSection(panel, "Zoom", ChoiceRow(MagnifyZoomPresets.Select(zoom =>
                ($"{zoom:0.#}×", Math.Abs(style.MagnifyZoom - zoom) < .01, (Action)(() => ApplyStyleChange(s => s with { MagnifyZoom = zoom }, null, true)))), 46));
            AddSection(panel, "Diameter", SliderRow(style.MagnifyDiameter, 40, 600, "0", " px", "diameter", value => s => s with { MagnifyDiameter = value }));
        }
        if (tool is not (EditorTool.Redact or EditorTool.Spotlight))
            AddSection(panel, "Opacity", SliderRow(style.Opacity * 100, 10, 100, "0", "%", "opacity", value => s => s with { Opacity = value / 100 }));

        var toggles = new WrapPanel();
        if (tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Arrow or EditorTool.Line or EditorTool.Pen or EditorTool.Magnify)
            toggles.Children.Add(Toggle("Dashed", style.Dashed, value => s => s with { Dashed = value }));
        if (tool is not (EditorTool.Redact or EditorTool.Spotlight or EditorTool.Highlighter))
            toggles.Children.Add(Toggle("Shadow", style.Shadow, value => s => s with { Shadow = value }));
        if (tool == EditorTool.Text)
            toggles.Children.Add(Toggle("Text background", style.TextBackground, value => s => s with { TextBackground = value }));
        if (toggles.Children.Count > 0) { toggles.Margin = new Thickness(0, 10, 0, 0); panel.Children.Add(toggles); }

        AddSection(panel, "Presets", PresetRow(tool));
        string scope = _selected is not null ? "Changes apply to the selected annotation and are" : "Changes are";
        panel.Children.Add(new TextBlock { Text = $"{scope} saved as the default for new {Info(tool).Name.ToLowerInvariant()} annotations.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#8E939B"), Margin = new Thickness(0, 10, 0, 0) });
        return panel;
    }

    private FrameworkElement PresetRow(EditorTool tool)
    {
        var wrap = new WrapPanel();
        foreach (ToolStyle preset in _styles.Presets(tool))
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(preset.Stroke), BorderBrush = Brush("#8A8C90"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 5, 0) });
            content.Children.Add(new TextBlock { Text = PresetSummary(tool, preset), FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            var chip = new Button { Content = content, Height = 28, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 3, 8, 3), Background = Brush("#303133"), Cursor = Cursors.Hand, ToolTip = "Apply preset · right-click to remove" };
            chip.Template = RoundedButtonTemplate();
            System.Windows.Automation.AutomationProperties.SetName(chip, $"Apply preset {PresetSummary(tool, preset)}");
            chip.Click += (_, _) => ApplyStyleChange(_ => preset, null, true);
            var menu = DarkMenu();
            menu.Items.Add(DarkMenuItem("Remove preset", LucideIconKind.Trash2, null, (_, _) => { _styles.RemovePreset(tool, preset); RebuildOptions(); }));
            chip.ContextMenu = menu;
            wrap.Children.Add(chip);
        }
        var save = IconTextButton(LucideIconKind.Plus, "Save style as preset", "Save the current style as a preset", (_, _) => SaveCurrentStyleAsPreset(), 190);
        save.Height = 30; save.Margin = new Thickness(0, 0, 4, 4); save.FontSize = 12;
        wrap.Children.Add(save);
        return wrap;
    }

    private static string PresetSummary(EditorTool tool, ToolStyle preset)
    {
        string summary = tool switch
        {
            EditorTool.Text or EditorTool.Callout => $"{preset.FontSize:0} px",
            EditorTool.Magnify => $"{preset.MagnifyZoom:0.#}×",
            EditorTool.Redact => preset.Redaction.ToString(),
            EditorTool.Spotlight => $"r{preset.CornerRadius:0}",
            _ => $"{preset.Width:0} px"
        };
        if (preset.Fill is not null && tool is EditorTool.Rectangle or EditorTool.Ellipse) summary += " · fill";
        if (preset.Dashed) summary += " · dash";
        if (preset.Opacity < 1) summary += $" · {preset.Opacity:P0}";
        return summary;
    }

    private void OpenBackgroundOptions()
    {
        CommitTextEdit();
        if (_backgroundButton is null) return;
        _backgroundPopup ??= CreatePopup(_backgroundButton);
        RebuildBackgroundOptions();
        _backgroundPopup.IsOpen = true;
    }

    private void RebuildBackgroundOptions()
    {
        if (_backgroundPopup is null) return;
        _backgroundPopup.Child = PopupChrome(BuildBackgroundPanel(_document.Background));
    }

    private void SetBackground(Func<BackgroundStyle, BackgroundStyle> change, string? coalesceKey, bool rebuild)
    {
        _document.SetBackground(change(_document.Background), coalesceKey is null ? null : "background:" + coalesceKey);
        if (rebuild) RebuildBackgroundOptions();
    }

    private StackPanel BuildBackgroundPanel(BackgroundStyle background)
    {
        var panel = new StackPanel { Width = 264 };
        panel.Children.Add(new TextBlock { Text = "Background", FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Brushes.White });
        panel.Children.Add(BuildBackgroundRemovalSection());
        var presets = new WrapPanel();
        presets.Children.Add(BackgroundSwatch(null, !background.IsEnabled));
        foreach (BackgroundPreset preset in BackgroundPresets.All) presets.Children.Add(BackgroundSwatch(preset, background.IsEnabled && background.Preset == preset.Id));
        AddSection(panel, "Preset", presets);
        FrameworkElement padding = BackgroundSlider(background.Padding, 0, 160, "padding", value => b => b with { Padding = value });
        FrameworkElement radius = BackgroundSlider(background.CornerRadius, 0, 64, "radius", value => b => b with { CornerRadius = value });
        var shadow = new CheckBox { Content = "Shadow", IsChecked = background.Shadow, Foreground = Brushes.White, Margin = new Thickness(0, 10, 12, 0), Cursor = Cursors.Hand };
        shadow.Checked += (_, _) => SetBackground(b => b with { Shadow = true }, null, false);
        shadow.Unchecked += (_, _) => SetBackground(b => b with { Shadow = false }, null, false);
        padding.IsEnabled = radius.IsEnabled = shadow.IsEnabled = background.IsEnabled;
        AddSection(panel, "Padding", padding);
        AddSection(panel, "Corner radius", radius);
        panel.Children.Add(shadow);
        panel.Children.Add(new TextBlock { Text = "Applies to Copy, Save, drag, and thumbnails. The capture and annotations stay editable.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#8E939B"), Margin = new Thickness(0, 10, 0, 0) });
        return panel;
    }

    private Button BackgroundSwatch(BackgroundPreset? preset, bool selected)
    {
        FrameworkElement face = preset is null
            ? new Border { Width = 44, Height = 28, CornerRadius = new CornerRadius(6), Background = Brush("#18191A"), BorderBrush = Brush("#55575B"), BorderThickness = new Thickness(1), Child = new LucideIcon { Icon = LucideIconKind.Ban, Width = 14, Height = 14, Foreground = Brushes.White } }
            : new Border { Width = 44, Height = 28, CornerRadius = new CornerRadius(6), Background = preset.CreateBrush(), BorderBrush = Brush("#55575B"), BorderThickness = new Thickness(1) };
        string name = preset?.Name ?? "None";
        var button = new Button { Content = face, Width = 52, Height = 36, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(2), Background = selected ? Brush("#FFD400") : Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = name };
        button.Template = RoundedButtonTemplate();
        System.Windows.Automation.AutomationProperties.SetName(button, $"Background {name}");
        string id = preset?.Id ?? BackgroundStyle.NoneId;
        button.Click += (_, _) => SetBackground(b => b with { Preset = id }, null, true);
        return button;
    }

    private FrameworkElement BackgroundSlider(double value, double minimum, double maximum, string key, Func<double, Func<BackgroundStyle, BackgroundStyle>> change) =>
        SliderControl(value, minimum, maximum, "0", " px", number => SetBackground(change(number), $"{key}:{_styleGesture}", false));

    private FrameworkElement SliderRow(double value, double minimum, double maximum, string format, string suffix, string key, Func<double, Func<ToolStyle, ToolStyle>> change) =>
        SliderControl(value, minimum, maximum, format, suffix, number => ApplyStyleChange(change(number), $"{key}:{_styleGesture}", false));

    private FrameworkElement SliderControl(double value, double minimum, double maximum, string format, string suffix, Action<double> changed)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = Math.Clamp(value, minimum, maximum), IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center, SmallChange = 1, LargeChange = 4 };
        var label = new TextBlock { Text = value.ToString(format) + suffix, Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        slider.PreviewMouseLeftButtonDown += (_, _) => _styleGesture++;
        slider.PreviewKeyDown += (_, _) => _styleGesture++;
        slider.ValueChanged += (_, e) => { label.Text = e.NewValue.ToString(format) + suffix; changed(e.NewValue); };
        Grid.SetColumn(label, 1);
        grid.Children.Add(slider); grid.Children.Add(label);
        return grid;
    }

    private CheckBox Toggle(string text, bool value, Func<bool, Func<ToolStyle, ToolStyle>> change)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Foreground = Brushes.White, Margin = new Thickness(0, 0, 14, 4), Cursor = Cursors.Hand };
        box.Checked += (_, _) => ApplyStyleChange(change(true), null, true);
        box.Unchecked += (_, _) => ApplyStyleChange(change(false), null, true);
        return box;
    }

    private FrameworkElement ColorRow(Color? current, bool allowNone, string noneLabel, Action<Color?> choose)
    {
        var wrap = new WrapPanel();
        if (allowNone) wrap.Children.Add(Swatch(null, current is null, noneLabel, () => choose(null)));
        foreach ((string name, Color value) in QuickColors)
            wrap.Children.Add(Swatch(value, current is Color color && color.R == value.R && color.G == value.G && color.B == value.B, name, () => choose(value)));
        return wrap;
    }

    private Button Swatch(Color? color, bool selected, string name, Action click)
    {
        FrameworkElement face = color is Color value
            ? new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(value), BorderBrush = Brush("#8A8C90"), BorderThickness = new Thickness(1) }
            : new LucideIcon { Icon = LucideIconKind.Ban, Width = 16, Height = 16, Foreground = Brushes.White };
        var button = new Button { Content = face, Width = 28, Height = 28, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(3), Background = selected ? Brush("#FFD400") : Brush("#303133"), Cursor = Cursors.Hand, ToolTip = name };
        button.Template = RoundedButtonTemplate();
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private FrameworkElement ChoiceRow(IEnumerable<(string Label, bool Selected, Action Click)> choices, double width)
    {
        var wrap = new WrapPanel();
        foreach ((string label, bool selected, Action click) in choices)
        {
            Button button = ActionButton(label, label, (_, _) => click(), width);
            button.Height = 28; button.FontSize = 12; button.Margin = new Thickness(0, 0, 4, 4);
            if (selected) { button.Background = Brush("#FFD400"); button.Foreground = Brushes.Black; }
            wrap.Children.Add(button);
        }
        return wrap;
    }

    private static void AddSection(Panel panel, string label, FrameworkElement content)
    {
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Brush("#9BA0A8"), Margin = new Thickness(0, 10, 0, 4) });
        panel.Children.Add(content);
    }

    private Popup CreatePopup(UIElement target)
    {
        var popup = new Popup
        {
            PlacementTarget = target, Placement = PlacementMode.Bottom, VerticalOffset = 6, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade
        };
        // Return keyboard focus to the canvas so tool shortcuts keep working after the popup closes.
        popup.Closed += (_, _) => { if (IsActive) _image.Focus(); };
        return popup;
    }

    private static Border PopupChrome(UIElement content)
    {
        var chrome = new Border
        {
            Background = Brush("#232426"), BorderBrush = Brush("#45474A"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Child = content
        };
        TextElement.SetForeground(chrome, Brushes.White);
        TextElement.SetFontFamily(chrome, new FontFamily("Segoe UI"));
        TextElement.SetFontSize(chrome, 13);
        return chrome;
    }
}
