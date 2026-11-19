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

    private static ControlTemplate RoundedTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "chrome");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;
        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(UIElement.OpacityProperty, .84));
        template.Triggers.Add(over);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .62));
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
