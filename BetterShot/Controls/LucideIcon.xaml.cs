using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BetterShot.Controls;

public enum LucideIconKind
{
    Trash2,
    Settings,
    Monitor,
    AppWindow,
    PanelBottom,
    MousePointer2,
    ArrowUpRight,
    Square,
    PenLine,
    Type,
    ListOrdered,
    RectangleEllipsis,
    Undo2,
    Redo2,
    Copy,
    Save,
    Check,
    Maximize,
    Minus,
    Plus,
    Pencil,
    Pin,
    X
}

public partial class LucideIcon : UserControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(LucideIconKind), typeof(LucideIcon),
        new FrameworkPropertyMetadata(LucideIconKind.MousePointer2, OnIconChanged));

    public LucideIcon()
    {
        InitializeComponent();
        DrawIcon();
    }

    public LucideIconKind Icon
    {
        get => (LucideIconKind)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    private static void OnIconChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => ((LucideIcon)sender).DrawIcon();

    private void DrawIcon()
    {
        if (IconCanvas is null) return;
        IconCanvas.Children.Clear();

        switch (Icon)
        {
            // Official pinned Lucide trash.svg (formerly trash-2).
            case LucideIconKind.Trash2: Path("M10 11v6"); Path("M14 11v6"); Path("M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"); Path("M3 6h18"); Path("M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"); break;
            case LucideIconKind.Settings: Path("M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915"); Path("M15 12a3 3 0 1 1-6 0a3 3 0 1 1 6 0"); break;
            case LucideIconKind.Monitor: Rectangle(2, 3, 20, 14, 2); Path("M8 21h8"); Path("M12 17v4"); break;
            case LucideIconKind.AppWindow: Rectangle(2, 4, 20, 16, 2); Path("M10 4v4"); Path("M2 8h20"); Path("M6 4v4"); break;
            case LucideIconKind.PanelBottom: Rectangle(3, 3, 18, 18, 2); Path("M3 15h18"); break;
            case LucideIconKind.MousePointer2: Path("M4.037 4.688a.495.495 0 0 1 .651-.651l16 6.5a.5.5 0 0 1-.063.947l-6.124 1.58a2 2 0 0 0-1.438 1.435l-1.579 6.126a.5.5 0 0 1-.947.063z"); break;
            case LucideIconKind.ArrowUpRight: Path("M7 7h10v10"); Path("M7 17 17 7"); break;
            case LucideIconKind.Square: Rectangle(3, 3, 18, 18, 2); break;
            case LucideIconKind.PenLine: Path("M13 21h8"); Path("M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z"); break;
            case LucideIconKind.Type: Path("M12 4v16"); Path("M4 7V5a1 1 0 0 1 1-1h14a1 1 0 0 1 1 1v2"); Path("M9 20h6"); break;
            case LucideIconKind.ListOrdered: Path("M11 5h10"); Path("M11 12h10"); Path("M11 19h10"); Path("M4 4h1v5"); Path("M4 9h2"); Path("M6.5 20H3.4c0-1 2.6-1.925 2.6-3.5a1.5 1.5 0 0 0-2.6-1.02"); break;
            case LucideIconKind.RectangleEllipsis: Rectangle(2, 6, 20, 12, 2); Path("M12 12h.01"); Path("M17 12h.01"); Path("M7 12h.01"); break;
            case LucideIconKind.Undo2: Path("M9 14 4 9l5-5"); Path("M4 9h10.5a5.5 5.5 0 0 1 5.5 5.5a5.5 5.5 0 0 1-5.5 5.5H11"); break;
            case LucideIconKind.Redo2: Path("m15 14 5-5-5-5"); Path("M20 9H9.5A5.5 5.5 0 0 0 4 14.5A5.5 5.5 0 0 0 9.5 20H13"); break;
            case LucideIconKind.Copy: Rectangle(8, 8, 14, 14, 2); Path("M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"); break;
            case LucideIconKind.Save: Path("M15.2 3a2 2 0 0 1 1.4.6l3.8 3.8a2 2 0 0 1 .6 1.4V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z"); Path("M17 21v-7a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1v7"); Path("M7 3v4a1 1 0 0 0 1 1h7"); break;
            case LucideIconKind.Check: Path("M20 6 9 17l-5-5"); break;
            case LucideIconKind.Maximize: Path("M8 3H5a2 2 0 0 0-2 2v3"); Path("M21 8V5a2 2 0 0 0-2-2h-3"); Path("M3 16v3a2 2 0 0 0 2 2h3"); Path("M16 21h3a2 2 0 0 0 2-2v-3"); break;
            case LucideIconKind.Minus: Path("M5 12h14"); break;
            case LucideIconKind.Plus: Path("M5 12h14"); Path("M12 5v14"); break;
            case LucideIconKind.Pencil: Path("M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z"); Path("m15 5 4 4"); break;
            case LucideIconKind.Pin: Path("M12 17v5"); Path("M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z"); break;
            case LucideIconKind.X: Path("M18 6 6 18"); Path("m6 6 12 12"); break;
        }
    }

    private void Path(string data)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Fill = Brushes.Transparent,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        BindStroke(path);
        IconCanvas.Children.Add(path);
    }

    private void Rectangle(double x, double y, double width, double height, double radius)
    {
        var rectangle = new System.Windows.Shapes.Path
        {
            Data = new RectangleGeometry(new Rect(x, y, width, height), radius, radius),
            Fill = Brushes.Transparent,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        };
        BindStroke(rectangle);
        IconCanvas.Children.Add(rectangle);
    }

    private void BindStroke(Shape shape) => shape.SetBinding(
        Shape.StrokeProperty,
        new Binding(nameof(Foreground)) { Source = this });
}
