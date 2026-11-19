using BetterShot.Controls;
using System.Windows.Controls;
using System.Windows.Input;

namespace BetterShot.Editing;

public sealed partial class EditorSurface
{
    private static readonly Vector DuplicateOffset = new(16, 16);

    internal void DuplicateSelectedForTest() => DuplicateSelected();
    internal void BringSelectedToFrontForTest() => BringSelectedToFront();
    internal void SendSelectedToBackForTest() => SendSelectedToBack();
    internal ContextMenu? OpenSelectionMenuForTest(Point point) => OpenSelectionMenu(point);

    private void Image_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropping || _start is not null || _spaceHeld) return;
        CommitTextEdit();
        if (OpenSelectionMenu(PixelPoint(e)) is not null) e.Handled = true;
    }

    private ContextMenu? OpenSelectionMenu(Point point)
    {
        Annotation? hit = HitTest(point);
        if (hit is null) return null;
        if (_tool != EditorTool.Select) SelectTool(EditorTool.Select);
        _selected = hit;
        Refresh();
        ContextMenu menu = DarkMenu();
        menu.Items.Add(DarkMenuItem("Duplicate", LucideIconKind.Copy, "Ctrl+D", (_, _) => DuplicateSelected()));
        menu.Items.Add(DarkMenuItem("Bring to front", LucideIconKind.ArrowUpToLine, null, (_, _) => BringSelectedToFront()));
        menu.Items.Add(DarkMenuItem("Send to back", LucideIconKind.ArrowDownToLine, null, (_, _) => SendSelectedToBack()));
        menu.Items.Add(new Separator { Background = Brush("#4A4B4E") });
        menu.Items.Add(DarkMenuItem("Delete", LucideIconKind.Trash2, "Delete", (_, _) => DeleteSelected()));
        menu.PlacementTarget = _image;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        return menu;
    }

    private void DuplicateSelected()
    {
        if (_selected is null || !_document.Items.Contains(_selected)) return;
        Annotation copy = _selected.DeepCopy().Move(DuplicateOffset);
        if (copy is StepAnnotation step) copy = step with { Number = _document.Items.OfType<StepAnnotation>().Max(item => item.Number) + 1 };
        _selected = copy;
        _document.Add(copy);
        _status.Text = "Duplicated the selected annotation.";
    }

    private void BringSelectedToFront()
    {
        if (_selected is not null) _document.BringToFront(_selected);
    }

    private void SendSelectedToBack()
    {
        if (_selected is not null) _document.SendToBack(_selected);
    }

    private void DeleteSelected()
    {
        if (_selected is null) return;
        Annotation deleting = _selected;
        _selected = null;
        _document.Remove(deleting);
    }
}
