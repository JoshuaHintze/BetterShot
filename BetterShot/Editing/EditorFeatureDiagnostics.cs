using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterShot.Editing;

/// <summary>Editor checks for grouped tools, crop, background, styling, selection commands, callouts, the inspector, and Done.</summary>
internal static class EditorFeatureDiagnostics
{
    internal static void Verify(EditorSurface editor, CaptureDocument document)
    {
        string directory = Path.Combine(Path.GetTempPath(), "BetterShot-editor-test-" + Guid.NewGuid().ToString("N"));
        var styles = new EditorStyleStore(Path.Combine(directory, "editor-styles.json"));
        Annotation[] originalItems = document.Items.ToArray();
        editor.UseStylesForTest(styles);
        try
        {
            VerifyShortcuts(editor);
            VerifyCrop(editor, document);
            VerifyBackground(editor, document);
            VerifyStyling(editor, document, styles);
            VerifySelectionCommands(editor, document);
            VerifyCallout(editor, document);
            VerifyInspector(editor, styles);
            VerifyDone(styles);
        }
        finally
        {
            editor.SelectAnnotationForTest(null);
            document.RestoreState(originalItems, null, BackgroundStyle.None);
            editor.UseStylesForTest(EditorStyleStore.Shared);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void VerifyShortcuts(EditorSurface editor)
    {
        var expectations = new (Key Key, EditorTool Tool)[]
        {
            (Key.O, EditorTool.Ellipse), (Key.R, EditorTool.Rectangle), (Key.L, EditorTool.Line), (Key.A, EditorTool.Arrow),
            (Key.H, EditorTool.Highlighter), (Key.P, EditorTool.Pen), (Key.Q, EditorTool.Callout), (Key.T, EditorTool.Text),
            (Key.N, EditorTool.Step), (Key.B, EditorTool.Redact), (Key.S, EditorTool.Spotlight), (Key.M, EditorTool.Magnify), (Key.V, EditorTool.Select)
        };
        foreach ((Key key, EditorTool tool) in expectations)
        {
            Check(editor.HandleShortcutForTest(key) && editor.CurrentToolForTest == tool, $"Shortcut {key} did not select {tool}.");
            editor.VerifyToolIconCentersForTest($"{tool} selected");
        }
        editor.HandleShortcutForTest(Key.O);
        editor.HandleShortcutForTest(Key.V);
        Check(editor.GroupToolForTest(EditorTool.Rectangle) == EditorTool.Ellipse, "The Shapes group did not remember its last-used tool.");
        Check(!editor.HandleShortcutForTest(Key.R, textInputFocused: true) && editor.CurrentToolForTest == EditorTool.Select, "A tool shortcut fired while text input had focus.");
        Check(!editor.HandleShortcutForTest(Key.Enter, ModifierKeys.Control, textInputFocused: true) && editor.IsVisible, "Ctrl+Enter closed the editor while text input had focus.");
        Check(!editor.HandleShortcutForTest(Key.Enter), "Enter was handled outside an active crop.");
    }

    private static void VerifyCrop(EditorSurface editor, CaptureDocument document)
    {
        editor.SetZoomForTest(0.5);
        Check(editor.HandleShortcutForTest(Key.C) && editor.IsCroppingForTest && editor.CurrentToolForTest == EditorTool.Crop, "C did not start cropping.");
        Check(editor.CropOverlayForTest.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Count() == 2 &&
              editor.CropOverlayForTest.Children.OfType<Thumb>().Count() == 9, "Crop is missing its handles or Apply/Cancel buttons.");
        editor.SetPendingCropForTest(new Rect(100, 80, 400, 300));
        editor.DragCropHandleForTest(CropHandle.Right, new Vector(-25, 0));
        Check(editor.PendingCropForTest == new Rect(100, 80, 350, 300), "Crop edge handle did not move in native pixels.");
        editor.DragCropHandleForTest(CropHandle.TopLeft, new Vector(10, 5));
        Check(editor.PendingCropForTest == new Rect(120, 90, 330, 290), "Crop corner handle did not move both edges.");
        Check(editor.HandleShortcutForTest(Key.Escape) && !editor.IsCroppingForTest && document.Crop is null && editor.IsVisible, "Escape did not cancel only the crop.");

        editor.HandleShortcutForTest(Key.C);
        editor.SetPendingCropForTest(new Rect(100, 80, 400, 300));
        Check(editor.HandleShortcutForTest(Key.Enter) && !editor.IsCroppingForTest && editor.IsVisible, "Enter did not apply the crop in place.");
        Check(document.Crop == new Rect(100, 80, 400, 300) && document.Render().PixelWidth == 400 && editor.ImageSourceForTest?.PixelWidth == 400,
            "Applied crop did not change the preview and export dimensions.");
        Check(document.Items.Count > 0 && document.Original.PixelWidth == 920, "Crop discarded the original or annotations.");
        document.Undo();
        Check(document.Crop is null && editor.ImageSourceForTest?.PixelWidth == 920, "Crop undo did not restore the full preview.");
    }

    private static void VerifyBackground(EditorSurface editor, CaptureDocument document)
    {
        document.SetBackground(new BackgroundStyle("ocean", 48, 18, true));
        Check(editor.ImageSourceForTest?.PixelWidth == 920 + 96 && document.Render().PixelHeight == 560 + 96, "Background padding was not shown in the preview and export.");
        document.Undo();
        Check(editor.ImageSourceForTest?.PixelWidth == 920 && !document.Background.IsEnabled, "Background undo failed.");
    }

    private static void VerifyStyling(EditorSurface editor, CaptureDocument document, EditorStyleStore styles)
    {
        editor.HandleShortcutForTest(Key.R);
        editor.ApplyStyleForTest(style => style with { Stroke = Colors.Blue, Width = 14, Dashed = true });
        Check(styles.Get(EditorTool.Rectangle) is { Width: 14, Dashed: true } saved && saved.Stroke == Colors.Blue, "Rectangle defaults did not update.");
        Check(styles.Get(EditorTool.Ellipse) == ToolStyle.DefaultFor(EditorTool.Ellipse), "Changing rectangle style changed another tool.");

        var rectangle = new RectangleAnnotation(new Rect(50, 50, 80, 60), ToolStyle.DefaultStroke, 4);
        document.Add(rectangle);
        editor.SelectAnnotationForTest(rectangle);
        editor.ApplyStyleForTest(style => style with { Stroke = Colors.Green, Opacity = .5 });
        var restyled = document.Items.OfType<RectangleAnnotation>().Last();
        Check(restyled.Color == Colors.Green && restyled.Opacity == .5 && restyled.Width == 4 && ReferenceEquals(editor.SelectedForTest, restyled),
            "Style change did not restyle the selected annotation from its own style.");
        document.Undo();
        Check(document.Items.OfType<RectangleAnnotation>().Last() == rectangle, "Restyle was not undoable.");
        document.Redo();
        editor.SelectAnnotationForTest(document.Items.OfType<RectangleAnnotation>().Last());
        editor.ApplyStyleForTest(style => style with { Opacity = .6 }, "opacity:test");
        editor.ApplyStyleForTest(style => style with { Opacity = .7 }, "opacity:test");
        document.Undo();
        Check(document.Items.OfType<RectangleAnnotation>().Last().Opacity == .5, "A slider drag created more than one undo step.");

        editor.SelectAnnotationForTest(document.Items.OfType<RectangleAnnotation>().Last());
        editor.SavePresetForTest();
        Check(styles.Presets(EditorTool.Rectangle).Count == 1, "Style preset was not saved.");
        editor.ApplyStyleForTest(style => style with { Width = 2 });
        editor.ApplyPresetForTest(styles.Presets(EditorTool.Rectangle)[0]);
        Check(document.Items.OfType<RectangleAnnotation>().Last() is { Width: 4, Opacity: .5 }, "Applying a preset did not restyle the selection.");
    }

    private static void VerifySelectionCommands(EditorSurface editor, CaptureDocument document)
    {
        var target = document.Items.OfType<RectangleAnnotation>().Last();
        editor.SelectAnnotationForTest(target);
        int count = document.Items.Count;
        editor.DuplicateSelectedForTest();
        var duplicate = document.Items[^1] as RectangleAnnotation;
        Check(document.Items.Count == count + 1 && duplicate is not null && duplicate.Bounds.TopLeft == target.Bounds.TopLeft + new Vector(16, 16) &&
              ReferenceEquals(editor.SelectedForTest, duplicate), "Duplicate did not add an offset, selected copy.");
        editor.SendSelectedToBackForTest();
        Check(ReferenceEquals(document.Items[0], duplicate), "Send to back did not move the selection to the bottom.");
        editor.BringSelectedToFrontForTest();
        Check(ReferenceEquals(document.Items[^1], duplicate), "Bring to front did not move the selection to the top.");
        Check(editor.HandleShortcutForTest(Key.D, ModifierKeys.Control) && document.Items.Count == count + 2, "Ctrl+D did not duplicate.");
        Check(editor.HandleShortcutForTest(Key.Delete) && document.Items.Count == count + 1, "Delete did not remove the selection.");
        ContextMenu? menu = editor.OpenSelectionMenuForTest(new Point(duplicate!.Bounds.Left, duplicate.Bounds.Top + 10));
        Check(menu is not null && menu.Items.OfType<MenuItem>().Count() == 4, "The selection context menu is missing commands.");
        menu!.IsOpen = false;
    }

    private static void VerifyCallout(EditorSurface editor, CaptureDocument document)
    {
        int count = document.Items.Count;
        // Drag from (300, 420) to (520, 330): the drag is the bubble, the tail points out from the drag's start corner.
        editor.BeginCalloutForTest(new Point(300, 420), new Point(520, 330));
        Check(editor.TextEditorForTest is { TextWrapping: TextWrapping.Wrap, FontWeight: var weight } && weight == FontWeights.Bold, "The callout editor is not bold, wrapping, and inline.");
        editor.SetTextForTest("Look here, this wraps onto more than one line");
        editor.CommitTextForTest();
        var callout = document.Items.OfType<TextAnnotation>().Last();
        Check(document.Items.Count == count + 1 && callout.IsCallout && callout.Text.StartsWith("Look here") && callout.WrapWidth is double wrap && wrap > 100,
            "Callout was not created inline with a wrapping bubble width.");
        Rect bubble = callout.BubbleBounds;
        Check(Math.Abs(bubble.Left - 300) < 1 && Math.Abs(bubble.Top - 330) < 1 && Math.Abs(bubble.Width - 220) < 1, "The callout bubble does not match the dragged rectangle.");
        Check(bubble.Height > callout.FontSize * 2, "The callout bubble did not grow to fit wrapped text.");
        Check(callout.Tail is Point pointer && pointer.X < 300 && pointer.Y > 420, "The callout tail does not point out from the drag start.");
        Check(GeometryUtil.Contrast(callout.BubbleColor) == callout.TextColor, "Callout text is not contrast-aware.");
        Point oldTail = callout.Tail!.Value;
        editor.UpdateLayout();
        Thumb tail = editor.TextOverlayForTest.Children.OfType<Thumb>().Single(thumb => thumb.Tag is TextCorner.Tail);
        tail.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        tail.RaiseEvent(new DragDeltaEventArgs(10, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        tail.RaiseEvent(new DragCompletedEventArgs(10, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Check(document.Items.OfType<TextAnnotation>().Last().Tail == oldTail + new Vector(20, 0), "Callout tail handle did not move the pointer.");
        document.Undo();
        Check(document.Items.OfType<TextAnnotation>().Last().Tail == oldTail, "Callout tail move was not undoable.");
        editor.BeginCalloutForTest(new Point(500, 200), new Point(500, 200));
        editor.CancelTextForTest();
        Check(document.Items.Count == count + 1, "Cancelling a new callout changed the document.");
        document.Undo();
        Check(document.Items.Count == count, "Callout creation was not one undo step.");
    }

    private static void VerifyInspector(EditorSurface editor, EditorStyleStore styles)
    {
        Check(EditorSurface.HeaderHeight == 44 && EditorSurface.InspectorWidth == 240, "The compact editor chrome dimensions changed.");
        editor.HandleShortcutForTest(Key.R);
        editor.SetInspectorForTest(true, docked: true);
        editor.UpdateLayout();
        Check(editor.InspectorVisibleForTest && editor.InspectorDockedForTest, "The docked inspector is not showing.");
        editor.SetInspectorForTest(true, docked: false);
        editor.UpdateLayout();
        Check(editor.InspectorVisibleForTest && !editor.InspectorDockedForTest && !styles.InspectorDocked, "The floating inspector is not showing.");
        Check(editor.HandleShortcutForTest(Key.I) && !editor.InspectorVisibleForTest && !styles.ShowsInspector, "I did not hide the inspector.");
        Check(editor.HandleShortcutForTest(Key.I) && editor.InspectorVisibleForTest, "I did not show the inspector again.");
        editor.SetInspectorForTest(true, docked: true);
        editor.ApplyQuickColorForTest(2);
        Check(styles.Get(EditorTool.Rectangle).Stroke == styles.QuickColors[2], "A quick color did not apply.");
        editor.SetHexForTest("#3366CC");
        Check(styles.Get(EditorTool.Rectangle).Stroke == Color.FromRgb(0x33, 0x66, 0xCC), "The hex field did not apply.");
        Check(EditorSurface.TryParseHex("ff4245", out Color parsed) && parsed == ToolStyle.DefaultStroke && !EditorSurface.TryParseHex("#12", out _), "Hex parsing is wrong.");
        editor.OpenBackgroundOptionsForTest();
        Check(editor.BackgroundPopupForTest is { IsOpen: true, Child: not null }, "Background options did not open.");
        editor.BackgroundPopupForTest!.IsOpen = false;
    }

    private static void VerifyDone(EditorStyleStore styles)
    {
        var document = new CaptureDocument(EditorDiagnostics.MakeSource(200, 120, 96));
        var window = new EditorWindow(document, styles);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            window.Surface.HandleShortcutForTest(Key.C);
            window.Surface.SetPendingCropForTest(new Rect(10, 10, 100, 60));
            Check(window.Surface.HandleShortcutForTest(Key.Enter, ModifierKeys.Control) && closed, "Ctrl+Enter did not close the editor.");
            Check(document.Crop == new Rect(10, 10, 100, 60), "Done did not commit the pending crop.");
        }
        finally
        {
            if (!closed) window.Close();
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
