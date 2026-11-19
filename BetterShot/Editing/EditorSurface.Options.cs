using BetterShot.Controls;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterShot.Editing;

/// <summary>Style changes (to the tool default and the selected annotation), presets, and the Background popup.</summary>
public sealed partial class EditorSurface
{
    /// <summary>The palette row under the quick colors, in KhingShot's order.</summary>
    private static readonly (string Name, Color Value)[] PaletteColors =
    [
        ("Orange", Color.FromRgb(0xFF, 0x9F, 0x0A)), ("Blue", Color.FromRgb(0x0A, 0x84, 0xFF)), ("Purple", Color.FromRgb(0xBF, 0x5A, 0xF2)),
        ("Pink", Color.FromRgb(0xFF, 0x37, 0x5F)), ("Cyan", Color.FromRgb(0x64, 0xD2, 0xFF)), ("White", Colors.White), ("Black", Colors.Black)
    ];
    private static readonly (string Name, Color Value)[] NamedColors =
    [
        ("Red", ToolStyle.DefaultStroke), ("Yellow", Color.FromRgb(0xFF, 0xD6, 0x0A)), ("Yellow", Color.FromRgb(0xFF, 0xD4, 0x00)),
        ("Green", Color.FromRgb(0x34, 0xC7, 0x59)), ("Green", Color.FromRgb(32, 195, 108)), ("Orange", Color.FromRgb(255, 149, 0)),
        ("Blue", Color.FromRgb(44, 137, 255)), ("Purple", Color.FromRgb(175, 82, 222)), .. PaletteColors
    ];
    private static readonly double[] MagnifyZoomPresets = [1.5, 2, 3, 4];
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
    internal void OpenBackgroundOptionsForTest() => OpenBackgroundOptions();
    internal Popup? BackgroundPopupForTest => _backgroundPopup;

    private EditorTool StyleTool => _selected is not null ? ToolFor(_selected) : _tool;
    private ToolStyle CurrentStyle => _selected is not null ? _selected.CaptureStyle(_styles.Get(StyleTool)).Normalized() : _styles.Get(StyleTool);
    private static bool HasStyleOptions(EditorTool tool) => tool is not (EditorTool.Select or EditorTool.Crop);

    private static bool SameColor(Color first, Color second) => first.R == second.R && first.G == second.G && first.B == second.B;

    private static string ColorName(Color color)
    {
        foreach ((string name, Color value) in NamedColors) if (SameColor(color, value)) return name;
        return "Custom";
    }

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Changes the current tool's remembered style and, when something is selected, restyles it as one undo step.</summary>
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
                // Keep the inspector (and an active slider drag) attached while the document refreshes.
                _inspectorContext = (_inspectorContext.Tool, restyled);
                _document.Replace(old, restyled, coalesceKey is null ? null : "style:" + coalesceKey);
            }
        }
        _strokePreview?.InvalidateVisual();
        UpdateToolbarExtras();
        if (rebuild) UpdateInspector(force: true);
    }

    private void SaveCurrentStyleAsPreset()
    {
        EditorTool tool = StyleTool;
        if (!HasStyleOptions(tool)) return;
        _styles.AddPreset(tool, CurrentStyle);
        _status.Text = $"Saved a {Info(tool).Name.ToLowerInvariant()} style preset.";
        UpdateInspector(force: true);
    }

    private static string PresetSummary(EditorTool tool, ToolStyle preset)
    {
        string summary = tool switch
        {
            EditorTool.Text or EditorTool.Callout => $"{preset.FontSize:0} pt",
            EditorTool.Magnify => $"{preset.MagnifyZoom:0.#}×",
            EditorTool.Redact => preset.Redaction.ToString(),
            EditorTool.Spotlight => $"r{preset.CornerRadius:0}",
            _ => $"{preset.Width:0} pt"
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
        var button = new Button { Content = face, Width = 52, Height = 36, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(2), Background = selected ? Brush("#FFD400") : Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = name, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Template = RoundedButtonTemplate();
        System.Windows.Automation.AutomationProperties.SetName(button, $"Background {name}");
        string id = preset?.Id ?? BackgroundStyle.NoneId;
        button.Click += (_, _) => SetBackground(b => b with { Preset = id }, null, true);
        return button;
    }

    private FrameworkElement BackgroundSlider(double value, double minimum, double maximum, string key, Func<double, Func<BackgroundStyle, BackgroundStyle>> change) =>
        SliderControl(value, minimum, maximum, "0", " px", number => SetBackground(change(number), $"{key}:{_styleGesture}", false));

    private FrameworkElement SliderControl(double value, double minimum, double maximum, string format, string suffix, Action<double> changed)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = Math.Clamp(value, minimum, maximum), IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center, SmallChange = 1, LargeChange = 4 };
        if (TryFindResource("EditorSlider") is Style sliderStyle) slider.Style = sliderStyle;
        var label = new TextBlock { Text = value.ToString(format) + suffix, Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        slider.PreviewMouseLeftButtonDown += (_, _) => _styleGesture++;
        slider.PreviewKeyDown += (_, _) => _styleGesture++;
        slider.ValueChanged += (_, e) => { label.Text = e.NewValue.ToString(format) + suffix; changed(e.NewValue); };
        Grid.SetColumn(label, 1);
        grid.Children.Add(slider); grid.Children.Add(label);
        return grid;
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
        popup.Closed += (_, _) => { if (Window.GetWindow(this)?.IsActive == true) _image.Focus(); };
        return popup;
    }

    private static Border PopupChrome(UIElement content)
    {
        var chrome = new Border
        {
            Background = Brush("#232426"), BorderBrush = Brush("#45474A"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Child = content
        };
        TextElement.SetForeground(chrome, Brushes.White);
        TextElement.SetFontFamily(chrome, new FontFamily("Segoe UI"));
        TextElement.SetFontSize(chrome, 13);
        return chrome;
    }
}
