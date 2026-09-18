using System.Windows.Media;
using System.Windows.Controls.Primitives;

namespace BetterShot.Editing;

internal static class InlineTextDiagnostics
{
    internal static void Verify(EditorWindow editor, CaptureDocument document)
    {
        int originalCount = document.Items.Count;
        var originalText = document.Items.OfType<TextAnnotation>().First();
        editor.BeginTextForTest(originalText.Position, originalText);
        editor.SetTextForTest("Cancelled text");
        editor.CancelTextForTest();
        if (document.Items.Count != originalCount || !ReferenceEquals(document.Items.OfType<TextAnnotation>().First(), originalText))
            throw new InvalidOperationException("Cancelling an inline edit changed the document.");

        editor.BeginTextForTest(new Point(90, 260));
        editor.SetTextForTest("Hello gyp\nSecond line");
        editor.UpdateLayout();
        var box = editor.TextEditorForTest ?? throw new InvalidOperationException("Inline text editor is missing.");
        var lastCharacter = box.GetRectFromCharacterIndex(box.Text.Length - 1);
        if (lastCharacter.IsEmpty || lastCharacter.Bottom > box.ActualHeight + 0.5)
            throw new InvalidOperationException("Inline multiline text is vertically clipped.");
        editor.CommitTextForTest();
        var added = document.Items.OfType<TextAnnotation>().Last();
        if (document.Items.Count != originalCount + 1 || added.Text.Replace("\r\n", "\n") != "Hello gyp\nSecond line")
            throw new InvalidOperationException("Inline text was not committed as one annotation.");
        document.Undo();
        if (document.Items.Count != originalCount) throw new InvalidOperationException("Text creation was not one undo step.");
        document.Redo();
        var restored = document.Items.OfType<TextAnnotation>().Last();
        editor.BeginTextForTest(restored.Position, restored);
        editor.SetTextForTest("Edited inline");
        editor.CommitTextForTest();
        if (document.Items.Count != originalCount + 1 || document.Items.OfType<TextAnnotation>().Last().Text != "Edited inline")
            throw new InvalidOperationException("Inline replacement duplicated text.");
        document.Undo();
        if (document.Items.OfType<TextAnnotation>().Last().Text.Replace("\r\n", "\n") != "Hello gyp\nSecond line")
            throw new InvalidOperationException("Undo did not restore the previous text.");
        editor.BeginTextForTest(new Point(20, 20));
        editor.SetTextForTest("   ");
        editor.CommitTextForTest();
        if (document.Items.Count != originalCount + 1) throw new InvalidOperationException("Empty text created an annotation.");
        VerifyHandles(editor, document);
    }

    private static void VerifyHandles(EditorWindow editor, CaptureDocument document)
    {
        var text = document.Items.OfType<TextAnnotation>().Last();
        editor.SetZoomForTest(0.5);
        editor.BeginTextForTest(text.Position, text);
        editor.CommitTextForTest();
        editor.UpdateLayout();
        var move = editor.TextOverlayForTest.Children.OfType<Thumb>().First();
        move.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        move.RaiseEvent(new DragDeltaEventArgs(20, 10) { RoutedEvent = Thumb.DragDeltaEvent });
        if (!editor.TextOverlayForTest.Children.Contains(move))
            throw new InvalidOperationException("Text handles were replaced during an active gesture.");
        move.RaiseEvent(new DragCompletedEventArgs(20, 10, false) { RoutedEvent = Thumb.DragCompletedEvent });
        var moved = document.Items.OfType<TextAnnotation>().Last();
        if ((moved.Position - text.Position - new Vector(40, 20)).Length > 0.01)
            throw new InvalidOperationException("Text move used display coordinates instead of native pixels.");
        document.Undo();
        text = document.Items.OfType<TextAnnotation>().Last();
        if (text.Position != moved.Position - new Vector(40, 20))
            throw new InvalidOperationException("Text move did not undo as one gesture.");
        editor.BeginTextForTest(text.Position, text); editor.CommitTextForTest();
        var corner = editor.TextOverlayForTest.Children.OfType<Thumb>().Last();
        corner.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        corner.RaiseEvent(new DragDeltaEventArgs(25, 15) { RoutedEvent = Thumb.DragDeltaEvent });
        corner.RaiseEvent(new DragCompletedEventArgs(25, 15, true) { RoutedEvent = Thumb.DragCompletedEvent });
        if (document.Items.OfType<TextAnnotation>().Last() != text)
            throw new InvalidOperationException("Cancelled resize changed text.");
        corner = editor.TextOverlayForTest.Children.OfType<Thumb>().Last();
        corner.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        corner.RaiseEvent(new DragDeltaEventArgs(25, 15) { RoutedEvent = Thumb.DragDeltaEvent });
        corner.RaiseEvent(new DragCompletedEventArgs(25, 15, false) { RoutedEvent = Thumb.DragCompletedEvent });
        if (document.Items.OfType<TextAnnotation>().Last().FontSize <= text.FontSize)
            throw new InvalidOperationException("Corner resize did not enlarge text.");
        document.Undo();
        if (document.Items.OfType<TextAnnotation>().Last() != text)
            throw new InvalidOperationException("Text resize did not undo as one gesture.");
    }
}
