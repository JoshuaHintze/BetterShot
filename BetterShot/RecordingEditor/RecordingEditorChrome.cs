using BetterShot.Controls;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterShot.RecordingEditor;

/// <summary>Buttons, menus, and formatting shared by the recording editor, matching the screenshot editor's dark chrome.</summary>
internal static class RecordingEditorChrome
{
    internal static readonly Brush ToolbarBrush = Freeze(Color.FromRgb(0x20, 0x21, 0x22));
    internal static readonly Brush ButtonBrush = Freeze(Color.FromRgb(0x30, 0x31, 0x33));
    internal static readonly Brush ActiveBrush = Freeze(Color.FromRgb(0xFF, 0xD4, 0x00));
    internal static readonly Brush MutedText = Freeze(Color.FromRgb(155, 160, 168));

    internal static Brush Brush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    internal static Button IconButton(LucideIconKind kind, string tip, RoutedEventHandler click, double width = 36, double height = 36, double iconSize = 18)
    {
        var icon = new LucideIcon { Icon = kind, Width = iconSize, Height = iconSize };
        var button = new Button
        {
            Content = icon, ToolTip = tip, Width = width, Height = height, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(5),
            Foreground = Brushes.White, Background = ButtonBrush, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Template = RoundedTemplate()
        };
        icon.SetBinding(Control.ForegroundProperty, new Binding(nameof(Button.Foreground)) { Source = button });
        System.Windows.Automation.AutomationProperties.SetName(button, tip.Split('(')[0].Trim());
        button.Click += click;
        return button;
    }

    internal static Button TextButton(string text, string tip, RoutedEventHandler click, LucideIconKind? kind = null, double height = 36)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var button = new Button
        {
            Content = row, ToolTip = tip, Height = height, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(10, 4, 10, 4),
            Foreground = Brushes.White, Background = ButtonBrush, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Template = RoundedTemplate()
        };
        if (kind is LucideIconKind icon)
        {
            var glyph = new LucideIcon { Icon = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 7, 0) };
            glyph.SetBinding(Control.ForegroundProperty, new Binding(nameof(Button.Foreground)) { Source = button });
            row.Children.Add(glyph);
        }
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        label.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Button.Foreground)) { Source = button });
        row.Children.Add(label);
        System.Windows.Automation.AutomationProperties.SetName(button, tip.Split('(')[0].Trim());
        button.Click += click;
        return button;
    }

    /// <summary>Highlights a toggle-style button (yellow when on), like the screenshot editor's active tool.</summary>
    internal static void SetActive(Button button, bool active)
    {
        button.Background = active ? ActiveBrush : ButtonBrush;
        button.Foreground = active ? Brushes.Black : Brushes.White;
    }

    internal static Border Separator() => new() { Width = 1, Height = 26, Margin = new Thickness(7, 0, 7, 0), Background = Brush("#3A3B3E"), VerticalAlignment = VerticalAlignment.Center };

    internal static ContextMenu Menu() => new()
    {
        Background = Brush("#292A2C"), Foreground = Brushes.White, BorderBrush = Brush("#4A4B4E"), BorderThickness = new Thickness(1), Padding = new Thickness(3)
    };

    internal static MenuItem MenuItem(string header, RoutedEventHandler click, LucideIconKind? icon = null, string? gesture = null, bool isChecked = false, string? tip = null)
    {
        var item = new MenuItem
        {
            Header = header, Padding = new Thickness(10, 7, 22, 7), Background = Brush("#292A2C"), Foreground = Brushes.White,
            InputGestureText = gesture ?? string.Empty, IsChecked = isChecked, ToolTip = tip
        };
        if (icon is LucideIconKind kind) item.Icon = new LucideIcon { Icon = kind, Width = 16, Height = 16, Foreground = Brushes.White };
        item.Click += click;
        return item;
    }

    internal static MenuItem MenuHeader(string text) => new()
    {
        Header = text, IsEnabled = false, Padding = new Thickness(10, 6, 22, 2), Background = Brush("#292A2C"), Foreground = MutedText, FontSize = 12
    };

    internal static void OpenBelow(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>m:ss.ff (or h:mm:ss.ff), the precision used across the editor.</summary>
    internal static string FormatTime(double seconds)
    {
        double safe = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
        var time = TimeSpan.FromSeconds(Math.Round(safe, 2));
        string fraction = (time.Milliseconds / 10).ToString("00", CultureInfo.InvariantCulture);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{fraction}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}.{fraction}";
    }

    // ---------- KhingShot-style surfaces (dark charcoal, rounded panels, yellow accent) ----------

    internal static readonly Brush WindowBrush = Freeze(Color.FromRgb(0x17, 0x17, 0x19));
    internal static readonly Brush PanelBrush = Freeze(Color.FromRgb(0x1F, 0x20, 0x22));
    internal static readonly Brush PanelBorderBrush = Freeze(Color.FromRgb(0x2E, 0x2F, 0x33));
    internal static readonly Brush SurfaceBrush = Freeze(Color.FromRgb(0x2A, 0x2B, 0x2E));
    internal static readonly Brush SurfaceBorderBrush = Freeze(Color.FromRgb(0x3A, 0x3B, 0x40));
    internal static readonly Brush DimText = Freeze(Color.FromRgb(0x6C, 0x6F, 0x76));
    internal static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");

    /// <summary>A rounded panel like the player, trim/export, and timeline panels in KhingShot.</summary>
    internal static Border Panel(UIElement child, Thickness padding) => new()
    {
        Background = PanelBrush, BorderBrush = PanelBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
        Padding = padding, Child = child, SnapsToDevicePixels = true
    };

    /// <summary>A borderless icon button that only shows a background on hover (or yellow when made active).</summary>
    internal static Button FlatIconButton(LucideIconKind kind, string tip, RoutedEventHandler click, double size = 36, double iconSize = 18)
    {
        Button button = IconButton(kind, tip, click, size, size, iconSize);
        button.Background = Brushes.Transparent;
        button.Margin = new Thickness(1, 0, 1, 0);
        return button;
    }

    /// <summary>Highlights a flat toggle: yellow icon on a soft tile when on (KhingShot's snapping and loop buttons).</summary>
    internal static void SetLit(Button button, bool lit)
    {
        button.Background = lit ? SurfaceBrush : Brushes.Transparent;
        button.Foreground = lit ? ActiveBrush : Brushes.White;
    }

    internal static Border VerticalRule(double height = 22) => new() { Width = 1, Height = height, Margin = new Thickness(8, 0, 8, 0), Background = SurfaceBorderBrush, VerticalAlignment = VerticalAlignment.Center };

    internal static TextBlock Label(string text, double size, Brush brush, FontWeight weight) =>
        new() { Text = text, FontSize = size, Foreground = brush, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A Windows 11 title-bar button (minimize, maximize/restore, close) drawn with the system icon font.</summary>
    internal static Button CaptionButton(string glyph, string name, RoutedEventHandler click, bool close = false)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = Brushes.White },
            Width = 46, Height = 34, Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = name,
            Template = CaptionTemplate(close ? Color.FromRgb(0xC4, 0x2B, 0x1C) : Color.FromArgb(40, 255, 255, 255)), Focusable = false
        };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += click;
        return button;
    }

    private static ControlTemplate CaptionTemplate(Color hover)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "chrome");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;
        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, Freeze(hover), "chrome"));
        template.Triggers.Add(over);
        return template;
    }

    private static readonly Brush HoverOverlay = Freeze(Color.FromArgb(28, 255, 255, 255));
    private static readonly Brush PressedOverlay = Freeze(Color.FromArgb(40, 0, 0, 0));

    /// <summary>Rounded button: a light overlay on hover and a dark one when pressed, so any background (clear, gray, yellow) reacts.</summary>
    internal static ControlTemplate RoundedTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "chrome");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        var layers = new FrameworkElementFactory(typeof(Grid));
        var overlay = new FrameworkElementFactory(typeof(Border), "overlay");
        overlay.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        overlay.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        layers.AppendChild(overlay);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        layers.AppendChild(content);
        border.AppendChild(layers);
        template.VisualTree = border;
        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, HoverOverlay, "overlay"));
        template.Triggers.Add(over);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, PressedOverlay, "overlay"));
        template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .38));
        template.Triggers.Add(disabled);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("#FFF07A"), "chrome"));
        template.Triggers.Add(focus);
        return template;
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
