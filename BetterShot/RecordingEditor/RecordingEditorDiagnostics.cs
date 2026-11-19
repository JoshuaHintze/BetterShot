using BetterShot.Editing;
using BetterShot.Persistence;
using BetterShot.Recording;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterShot.RecordingEditor;

/// <summary>
/// Phase 7 recording editor checks: cut/split/put-back/in-out math, keyboard mapping, mark timing and rows, undo,
/// tracking on synthetic moving frames (follows, then loses when covered), lost-field blur policy, project sidecar
/// round-trip and version tolerance, trim remapping, sharing decisions, settings, GIF delay/loop metadata, timelapse
/// speed, NV12 conversion, burned-in redactions, and (when Windows media editing is available) real encodes of a
/// synthetic clip. Uses isolated fixture folders only; no private desktop content is recorded.
/// </summary>
internal static class RecordingEditorDiagnostics
{
    internal static async Task RunAsync(string? output)
    {
        string directory = string.IsNullOrWhiteSpace(output) ? AppContext.BaseDirectory : Path.HasExtension(output) ? Path.GetDirectoryName(Path.GetFullPath(output))! : output;
        Directory.CreateDirectory(directory);
        string runRoot = Path.Combine(directory, "recording-editor-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        VerifyTimeline();
        VerifyKeys();
        VerifyMarks();
        VerifyHistory();
        VerifyTracking();
        VerifyTrackPath();
        VerifyProject(Path.Combine(runRoot, "project"));
        VerifyTrimRemap();
        VerifySharing(Path.Combine(runRoot, "sharing"));
        VerifySettings(Path.Combine(runRoot, "settings"));
        VerifyExportPlanning();
        VerifyNv12();
        VerifyGif(Path.Combine(runRoot, "gif"));
        VerifyCompositor();
        string live = await VerifyLiveExportsAsync(Path.Combine(runRoot, "live"), directory);
        App.WriteTestResult(Path.Combine(directory, "recording-editor-test.txt"),
            "PASS: cut/split/put-back/in-out math and repair, keyboard mapping, mark timing/rows/interpolation, undo/redo/coalescing, tracking follows a moving pattern and loses it when covered, keyframe simplification, lost-field blur policy, project sidecar round-trip/hidden/version tolerance/damage, trim remapping of marks and events, marked-copy sharing decisions, settings round-trip/clamping, timelapse speed, GIF timing/size/too-short, NV12 levels, GIF delay and loop metadata, burned-in solid/pixelate/whole-picture redactions; " + live);
    }

    private static void VerifyTimeline()
    {
        EditTimeline timeline = EditTimeline.Create(10).SplitAt(3).SplitAt(7);
        Check(timeline.Segments.Count == 3 && timeline.IsUnedited, "Splitting should add segments without cutting.");
        Check(timeline.SplitAt(3.001).Segments.Count == 3 && timeline.SplitAt(0).Segments.Count == 3, "Splits at an edge were not ignored.");
        EditTimeline cut = timeline.SetKept(1, false);
        Check(cut.HasCuts && Near(cut.OutputDuration, 6) && cut.KeptRanges.Count == 2, "Cutting the middle clip did not leave 6 s.");
        Check(Near(cut.SourceToOutput(8) ?? -1, 4) && cut.SourceToOutput(5) is null && Near(cut.OutputToSource(4), 8) && Near(cut.OutputToSource(1), 1), "Source/output mapping is wrong.");
        Check(Near(cut.NextPlayable(5) ?? -1, 7) && Near(cut.NextPlayable(1) ?? -1, 1) && cut.NextPlayable(10) is null, "Next playable time is wrong.");
        Check(cut.PreviousPlayable(5) is double previous && previous < 3 && previous > 2.99, "Previous playable time is wrong.");
        EditTimeline back = cut.SetKept(1, true);
        Check(back.IsUnedited && Near(back.OutputDuration, 10), "Putting the cut back did not restore the recording.");
        EditTimeline q = EditTimeline.Create(10).DeleteFromClipStart(2);
        Check(q.Segments.Count == 2 && !q.Segments[0].Kept && Near(q.Segments[0].End, 2) && Near(q.OutputDuration, 8), "Q did not delete from the clip start to the playhead.");
        EditTimeline w = EditTimeline.Create(10).SplitAt(6).DeleteToClipEnd(4);
        Check(Near(w.OutputDuration, 8) && !w.SegmentAt(5).Kept && w.SegmentAt(7).Kept, "W did not delete from the playhead to the clip end only.");
        Check(ReferenceEquals(q, q.DeleteFromClipStart(1)), "Q inside a cut changed the timeline.");
        EditTimeline inOut = cut.WithInPoint(1).WithOutPoint(9);
        Check(Near(inOut.OutputDuration, 4) && Near(inOut.FirstPlayable, 1) && Near(inOut.LastPlayable, 9), "In/out points do not limit the kept stretch.");
        Check(inOut.WithInPoint(9.5).OutPoint is null && inOut.WithOutPoint(0.5).InPoint is null, "Crossed in/out points were not cleared.");
        Check(inOut.Reset().IsUnedited && inOut.Reset().Segments.Count == 1, "Reset did not put the recording back.");
        EditTimeline repaired = EditTimeline.Restore(10, [new TimelineSegment(0, 4, true), new TimelineSegment(3, 6, false), new TimelineSegment(double.NaN, 2, false), new TimelineSegment(8, 50, true)], -1, 99);
        Check(Near(repaired.Duration, 10) && Near(repaired.OutputDuration, 7) && repaired.InPoint is null && repaired.OutPoint is null, "Damaged segments were not repaired.");
        Check(TimelineSnapping.Snap(4.96, [5, 2], 0.05) == 5 && TimelineSnapping.Snap(4.9, [5], 0.05) == 4.9, "Snapping tolerance is wrong.");
        Check(EditTimeline.Create(10).CutRange(0, 10).KeptRanges.Count == 0 && EditTimeline.Create(10).CutRange(0, 10).NextPlayable(0) is null, "Cutting everything left something playable.");
    }

    private static void VerifyKeys()
    {
        (Key Key, ModifierKeys Modifiers, RecordingEditorCommand Command)[] cases =
        [
            (Key.Space, ModifierKeys.None, RecordingEditorCommand.PlayPause),
            (Key.Left, ModifierKeys.None, RecordingEditorCommand.PreviousFrame), (Key.OemComma, ModifierKeys.None, RecordingEditorCommand.PreviousFrame),
            (Key.Right, ModifierKeys.None, RecordingEditorCommand.NextFrame), (Key.OemPeriod, ModifierKeys.None, RecordingEditorCommand.NextFrame),
            (Key.Home, ModifierKeys.None, RecordingEditorCommand.FirstFrame), (Key.End, ModifierKeys.None, RecordingEditorCommand.LastFrame),
            (Key.B, ModifierKeys.None, RecordingEditorCommand.ToggleSplitMode), (Key.B, ModifierKeys.Control, RecordingEditorCommand.SplitAtPlayhead),
            (Key.I, ModifierKeys.None, RecordingEditorCommand.SetIn), (Key.O, ModifierKeys.None, RecordingEditorCommand.SetOut),
            (Key.I, ModifierKeys.Shift, RecordingEditorCommand.JumpToIn), (Key.O, ModifierKeys.Shift, RecordingEditorCommand.JumpToOut),
            (Key.Q, ModifierKeys.None, RecordingEditorCommand.DeleteToClipStart), (Key.W, ModifierKeys.None, RecordingEditorCommand.DeleteToClipEnd),
            (Key.Delete, ModifierKeys.None, RecordingEditorCommand.DeleteSelection), (Key.N, ModifierKeys.None, RecordingEditorCommand.ToggleSnapping),
            (Key.Z, ModifierKeys.Control, RecordingEditorCommand.Undo), (Key.Y, ModifierKeys.Control, RecordingEditorCommand.Redo),
            (Key.Z, ModifierKeys.Control | ModifierKeys.Shift, RecordingEditorCommand.Redo), (Key.S, ModifierKeys.Control, RecordingEditorCommand.Save),
            (Key.Q, ModifierKeys.Alt, RecordingEditorCommand.None), (Key.A, ModifierKeys.None, RecordingEditorCommand.None), (Key.W, ModifierKeys.Control, RecordingEditorCommand.None)
        ];
        foreach ((Key key, ModifierKeys modifiers, RecordingEditorCommand expected) in cases)
            Check(RecordingEditorKeys.Map(key, modifiers) == expected, $"{modifiers}+{key} should map to {expected}.");
    }

    private static void VerifyMarks()
    {
        var shape = new RectangleAnnotation(new Rect(10, 10, 40, 20), Colors.Black, 4, Redaction: true);
        VideoMark late = VideoMark.CreateAt(shape, 9.5, 10);
        Check(Near(late.Start, 9.5) && Near(late.Duration, 0.5), "A mark near the end did not shorten to fit.");
        VideoMark mark = VideoMark.CreateAt(shape, 2, 10);
        Check(Near(mark.Duration, VideoMark.DefaultDuration) && mark.IsActiveAt(2) && mark.IsActiveAt(4.99) && !mark.IsActiveAt(5) && !mark.IsActiveAt(1.9), "Default 3 s mark timing is wrong.");
        Check(Near(mark.WithTiming(-3, 0.01, 10).Start, 0) && Near(mark.WithTiming(-3, 0.01, 10).Duration, VideoMark.MinimumDuration), "Mark timing was not clamped.");
        var second = VideoMark.CreateAt(new ArrowAnnotation(new Point(0, 0), new Point(5, 5), Colors.Red, 4), 1, 10);
        IReadOnlyDictionary<Guid, int> own = MarkRows.Assign([mark, second], ownRows: true), folded = MarkRows.Assign([mark, second], ownRows: false);
        Check(own[second.Id] == 0 && own[mark.Id] == 1 && folded.Values.All(row => row == 0), "Mark rows are wrong.");
        Check(MarkRows.RowCount([mark, second], true) == 2 && MarkRows.RowCount([mark, second], false) == 1 && MarkRows.RowCount([], true) == 0, "Row counts are wrong.");
        VideoMark tracked = mark with { Tracked = true, Keyframes = [new TrackKeyframe(2, new Rect(10, 10, 40, 20)), new TrackKeyframe(4, new Rect(30, 50, 40, 20))] };
        Check(tracked.BoundsAt(3) == new Rect(20, 30, 40, 20) && tracked.BoundsAt(1) == new Rect(10, 10, 40, 20) && tracked.BoundsAt(4.5) == new Rect(30, 50, 40, 20), "Keyframe interpolation is wrong.");
        Check(tracked.ShapeAt(3) is RectangleAnnotation { Bounds.X: 20 } && second.ShapeAt(3) == second.Shape, "ShapeAt did not follow the path.");
        VideoMark lost = tracked with { LostAt = 3.5 };
        Check(!lost.BlursWholePictureAt(3.6), "Stop following must not blur the picture.");
        VideoMark blurring = lost with { WhenLost = LostFieldPolicy.BlurWholePicture };
        Check(blurring.BlursWholePictureAt(3.6) && !blurring.BlursWholePictureAt(3.4) && !blurring.BlursWholePictureAt(5.1), "Lost-field blur is not limited to the lost stretch of the mark.");
        var state = new RecordingEditState(EditTimeline.Create(10), [blurring, second], HideMarkup: true);
        Check(state.VisibleMarks.Single() == blurring && state.BlursWholePictureAt(4) && state.HasRedaction, "Hiding markup must keep redactions covering.");
        Check(mark.NeedsSourcePixels == false && (mark with { Shape = shape with { RedactionMode = RedactionStyle.Pixelate } }).NeedsSourcePixels, "Pixel-sampling redactions are not detected.");
    }

    private static void VerifyHistory()
    {
        var history = new RecordingEditHistory(RecordingEditState.Create(10));
        Check(!history.IsDirty && !history.CanUndo, "New history is dirty.");
        history.Apply(state => state with { Timeline = state.Timeline.SplitAt(4) });
        history.Apply(state => state with { Timeline = state.Timeline.SetKept(0, false) });
        Check(history.IsDirty && Near(history.Current.Timeline.OutputDuration, 6), "Edits were not applied.");
        Check(!history.Apply(state => state), "A no-op edit added an undo step.");
        history.Undo();
        Check(Near(history.Current.Timeline.OutputDuration, 10) && history.CanRedo, "Undo failed.");
        history.Redo();
        Check(Near(history.Current.Timeline.OutputDuration, 6), "Redo failed.");
        VideoMark mark = VideoMark.CreateAt(new LineAnnotation(new Point(0, 0), new Point(9, 9), Colors.Red, 2), 1, 10);
        history.Apply(state => state.AddMark(mark));
        history.Apply(state => state.WithMark(mark.WithTiming(2, 3, 10)), "drag");
        history.Apply(state => state.WithMark(mark.WithTiming(3, 3, 10)), "drag");
        history.EndGroup();
        history.Undo();
        Check(Near(history.Current.Marks.Single().Start, 1), "A drag was not one undo step.");
        history.MarkSaved();
        Check(!history.IsDirty, "Saving did not clear dirty.");
    }

    /// <summary>A textured patch moves 3 px per frame over a flat background, then a flat box covers it.</summary>
    private static void VerifyTracking()
    {
        const int width = 160, height = 120, patch = 20;
        var random = new Random(7);
        byte[] texture = new byte[patch * patch];
        random.NextBytes(texture);
        GrayFrame Frame(int x, int y, bool covered)
        {
            byte[] pixels = Enumerable.Repeat((byte)90, width * height).ToArray();
            for (int row = 0; row < patch; row++)
                for (int column = 0; column < patch; column++)
                    pixels[(y + row) * width + x + column] = covered ? (byte)200 : texture[row * patch + column];
            return new GrayFrame(width, height, pixels);
        }
        var tracker = new RedactionTracker(Frame(20, 30, false), new TrackBox(20, 30, patch, patch), searchRadius: 10);
        Check(!tracker.IsFlat, "The textured patch was considered flat.");
        for (int step = 1; step <= 15; step++)
        {
            TrackStep result = tracker.Step(Frame(20 + step * 3, 30 + step, false));
            Check(!result.Lost && result.Box.X == 20 + step * 3 && result.Box.Y == 30 + step && result.Score > 0.95, $"Tracker lost the moving pattern at step {step} ({result.Box}, {result.Score:0.00}).");
        }
        TrackStep covered = tracker.Step(Frame(20 + 48, 30 + 16, true));
        Check(covered.Lost && tracker.Box.X == 20 + 45, "Covering the pattern was not reported as lost, or the box moved.");
        TrackStep away = tracker.Step(Frame(5, 5, false));
        Check(away.Lost, "A pattern outside the search window was found.");
        var flat = new RedactionTracker(new GrayFrame(width, height, new byte[width * height]), new TrackBox(10, 10, 20, 20));
        Check(flat.IsFlat && !flat.Step(Frame(10, 10, false)).Lost, "A flat region should stay put, not report lost.");
        byte[] bgra = [10, 20, 30, 255, 255, 255, 255, 255];
        GrayFrame gray = GrayFrame.FromBgra(bgra, 2, 1, 8);
        Check(gray[1, 0] == 254 || gray[1, 0] == 255, "BGRA to gray conversion is wrong.");
    }

    private static void VerifyTrackPath()
    {
        TrackKeyframe[] straight = Enumerable.Range(0, 30).Select(index => new TrackKeyframe(index / 15.0, new Rect(index * 2, 10, 20, 20))).ToArray();
        Check(TrackPath.Simplify(straight, 1).Count == 2, "A straight path was not reduced to its ends.");
        TrackKeyframe[] turn = Enumerable.Range(0, 30).Select(index => new TrackKeyframe(index / 15.0, new Rect(index < 15 ? index * 2 : 28, index < 15 ? 10 : 10 + (index - 14) * 2, 20, 20))).ToArray();
        IReadOnlyList<TrackKeyframe> simplified = TrackPath.Simplify(turn, 1);
        Check(simplified.Count == 3 && Near(simplified[1].Time, 14 / 15.0), $"A path with one turn kept {simplified.Count} keyframes.");
    }

    private static RecordingEditState SampleState()
    {
        var redaction = new VideoMark(Guid.NewGuid(), new RectangleAnnotation(new Rect(10, 20, 30, 40), Colors.Black, 4, Redaction: true) { RedactionMode = RedactionStyle.Pixelate }, 1, 3)
        {
            Tracked = true, Keyframes = [new TrackKeyframe(1, new Rect(10, 20, 30, 40)), new TrackKeyframe(2.5, new Rect(60, 20, 30, 40))], LostAt = 3.25, WhenLost = LostFieldPolicy.BlurWholePicture
        };
        var text = new VideoMark(Guid.NewGuid(), new TextAnnotation(new Point(5, 5), "Click here", Colors.Yellow, 32) { Tail = new Point(40, 60) }, 0.5, 2.5);
        var step = new VideoMark(Guid.NewGuid(), new StepAnnotation(new Point(50, 50), 2, Colors.Red, 16), 4, 3);
        EditTimeline timeline = EditTimeline.Create(12).SplitAt(5).SplitAt(6).SetKept(1, false).WithInPoint(0.5);
        return new RecordingEditState(timeline, [redaction, text, step], false);
    }

    private static void VerifyProject(string root)
    {
        Directory.CreateDirectory(root);
        string movie = Path.Combine(root, "Recording 2026-11-19 at 09.00.00.mp4");
        File.WriteAllBytes(movie, [0]);
        RecordingEditState state = SampleState();
        RecordingEditProject.Save(movie, state, new RecordingEditViewOptions(false), "ABC", [], null);
        string sidecar = RecordingEditProject.SidecarPath(movie);
        Check(Path.GetFileName(sidecar) == ".Recording 2026-11-19 at 09.00.00.mp4.bettershot-edit" && File.GetAttributes(sidecar).HasFlag(FileAttributes.Hidden), "Edit sidecar is misnamed or not hidden.");
        RecordingEditProject.Save(movie, state, new RecordingEditViewOptions(false), "ABC", [], null); // Rewriting a hidden file must work.
        LoadedEditProject loaded = RecordingEditProject.Load(movie, 12);
        Check(loaded.Warning is null && loaded.MarkedCopyFingerprint == "ABC" && !loaded.View.MarksOnOwnRows, "Project options did not round-trip.");
        Check(loaded.State.Timeline.Segments.SequenceEqual(state.Timeline.Segments) && loaded.State.Timeline.InPoint == 0.5, "Cuts did not round-trip.");
        Check(loaded.State.Marks.Count == 3 && loaded.State.Marks.SequenceEqual(state.Marks, MarkComparer.Instance), "Marks did not round-trip.");
        Check(RecordingEditProject.Fingerprint(loaded.State) == RecordingEditProject.Fingerprint(state), "Fingerprint is not stable across save/load.");
        RecordingEditState moved = state.WithMark(state.Marks[2] with { Start = 4.5 });
        Check(RecordingEditProject.Fingerprint(moved) != RecordingEditProject.Fingerprint(state), "Fingerprint ignored a mark change.");
        Check(RecordingEditProject.Fingerprint(state with { HideMarkup = true }) != RecordingEditProject.Fingerprint(state), "Fingerprint ignored hidden markup.");

        JsonNode node = JsonNode.Parse(File.ReadAllText(sidecar))!;
        node["version"] = 99;
        node["futureFeature"] = new JsonObject { ["enabled"] = true };
        ((JsonArray)node["marks"]!).Add(JsonNode.Parse("{\"id\":\"" + Guid.NewGuid() + "\",\"start\":1,\"duration\":2,\"shape\":{\"Kind\":\"hologram\",\"A\":255,\"R\":0,\"G\":0,\"B\":0,\"Width\":1,\"X1\":0,\"Y1\":0,\"X2\":0,\"Y2\":0,\"Radius\":0,\"Number\":0,\"Redaction\":false,\"Text\":null,\"Points\":null}}"));
        string newer = node.ToJsonString();
        LoadedEditProject future = RecordingEditProject.Deserialize(newer, 12);
        Check(future.State.Marks.Count == 3 && future.PreservedMarks.Count == 1 && future.Warning is not null, "A newer project with an unknown mark was not read tolerantly.");
        string rewritten = RecordingEditProject.Serialize(future.State, future.View, future.MarkedCopyFingerprint, future.PreservedMarks, future.UnknownFields);
        JsonNode again = JsonNode.Parse(rewritten)!;
        Check(again["futureFeature"]?["enabled"]?.GetValue<bool>() == true && ((JsonArray)again["marks"]!).Count == 4 && again["version"]!.GetValue<int>() == RecordingEditProject.CurrentVersion,
            "Unknown fields or marks were dropped on rewrite.");
        LoadedEditProject resized = RecordingEditProject.Deserialize(File.ReadAllText(sidecar), 20);
        Check(resized.State.Timeline.IsUnedited && resized.State.Marks.Count == 3 && resized.MarkedCopyFingerprint is null && resized.Warning is not null, "A changed movie length did not reset cuts safely.");
        File.SetAttributes(sidecar, FileAttributes.Normal);
        File.WriteAllText(sidecar, "{ not json");
        LoadedEditProject damaged = RecordingEditProject.Load(movie, 12);
        Check(damaged.Warning is not null && damaged.State.Timeline.IsUnedited && damaged.State.Marks.Count == 0, "A damaged project did not start fresh with a warning.");
        Check(RecordingEditProject.Load(Path.Combine(root, "none.mp4"), 5).Warning is null, "A missing project produced a warning.");
    }

    private static void VerifyTrimRemap()
    {
        RecordingEditState state = SampleState(); // Kept: [0.5, 5) and [6, 12) → 10.5 s.
        RecordingEditState trimmed = TrimRemap.Apply(state, 10.5);
        Check(trimmed.Timeline.IsUnedited && Near(trimmed.Timeline.Duration, 10.5), "Trimmed timeline is not clean.");
        VideoMark text = trimmed.Marks.Single(mark => mark.Kind == MarkKind.Callout);
        Check(Near(text.Start, 0) && Near(text.Duration, 2.5), $"Text mark remapped to {text.Start}+{text.Duration}.");
        VideoMark step = trimmed.Marks.Single(mark => mark.Kind == MarkKind.Step); // 4..7 spans the cut 5..6 → 3.5..5.5.
        Check(Near(step.Start, 3.5) && Near(step.Duration, 2), $"Step mark across a cut remapped to {step.Start}+{step.Duration}.");
        VideoMark redaction = trimmed.Marks.Single(mark => mark.IsRedaction);
        Check(redaction.Keyframes.Count == 2 && Near(redaction.Keyframes[0].Time, 0.5) && Near(redaction.LostAt ?? -1, 2.75), "Tracking keyframes were not remapped.");
        RecordingEditState allCut = state with { Timeline = state.Timeline.CutRange(0, 12) };
        Check(TrimRemap.Apply(allCut, 0.1).Marks.Count == 0, "Marks inside cuts survived.");
        RecordingEvent[] events = [new(1, RecordingEvent.ClickKind, 4, 5, null), new(5.5, RecordingEvent.ClickKind, 1, 1, null), new(7, RecordingEvent.KeyKind, null, null, "Ctrl+C"), new(-0.2, RecordingEvent.CameraKind, null, null, "start")];
        IReadOnlyList<RecordingEvent> remapped = TrimRemap.RemapEvents(events, state.Timeline);
        Check(remapped.Count == 2 && Near(remapped[0].Time, 0.5) && Near(remapped[1].Time, 5.5) && remapped[1].Text == "Ctrl+C", "Events were not remapped.");
    }

    private static void VerifySharing(string root)
    {
        Directory.CreateDirectory(root);
        RecordingSharing.Configure(Path.Combine(root, "MarkedCopies"), () => true);
        string movie = Path.Combine(root, "clip.mp4");
        File.WriteAllBytes(movie, [0]);
        (string? unedited, string? uneditedNote) = RecordingSharing.ResolveSharePath(movie);
        Check(unedited == movie && uneditedNote is null, "An unedited recording did not share the original.");
        RecordingEditState plain = RecordingEditState.Create(10);
        Check(!RecordingSharing.NeedsMarkedCopy(plain, true), "An unedited recording needs a marked copy.");
        RecordingEditState cutOnly = plain with { Timeline = plain.Timeline.CutRange(1, 2) };
        Check(RecordingSharing.NeedsMarkedCopy(cutOnly, true) && !RecordingSharing.NeedsMarkedCopy(cutOnly, false), "Keep marked copy setting is ignored.");
        RecordingEditState redacted = SampleState();
        Check(RecordingSharing.NeedsMarkedCopy(redacted, false), "A redacted recording must always need a covered copy.");
        RecordingEditProject.Save(movie, redacted, new RecordingEditViewOptions(), null, [], null);
        (string? refused, string? reason) = RecordingSharing.ResolveSharePath(movie);
        Check(refused is null && reason is not null, "A redacted recording without a covered copy was shared.");
        string copy = RecordingSharing.MarkedCopyPath(movie);
        Check(Path.GetFileName(copy) == "clip.mp4" && copy.StartsWith(Path.Combine(root, "MarkedCopies"), StringComparison.OrdinalIgnoreCase), "Marked copy path is wrong.");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllBytes(copy, [1]);
        RecordingEditProject.Save(movie, redacted, new RecordingEditViewOptions(), RecordingEditProject.Fingerprint(redacted), [], null);
        Check(RecordingSharing.ResolveSharePath(movie).Path == copy, "An up-to-date covered copy was not shared.");
        RecordingEditProject.Save(movie, redacted, new RecordingEditViewOptions(), "stale", [], null);
        Check(RecordingSharing.ResolveSharePath(movie).Path is null, "A stale covered copy was shared.");
        RecordingEditProject.Save(movie, cutOnly, new RecordingEditViewOptions(), "stale", [], null);
        Check(RecordingSharing.ResolveSharePath(movie) is (string original, string) && original == movie, "A stale marked copy without redaction should fall back to the original with a note.");
        RecordingSharing.DeleteMarkedCopy(movie);
        Check(!File.Exists(copy) && !Directory.Exists(Path.GetDirectoryName(copy)), "The marked copy was not deleted.");
    }

    private static void VerifySettings(string root)
    {
        CaptureSettings defaults = CaptureSettings.Default;
        Check(defaults.GifFps == 12 && defaults.GifMaxDimension == 960 && defaults.KeepMarkedCopy && !defaults.TrimReplacesOriginal, "Recording editor defaults changed.");
        var store = new CaptureHistoryStore(Path.Combine(root, "roundtrip"));
        store.UpdateSettings(settings => settings with { GifFps = 20, GifMaxDimension = 0, KeepMarkedCopy = false, TrimReplacesOriginal = true });
        CaptureSettings reloaded = new CaptureHistoryStore(Path.Combine(root, "roundtrip")).Settings;
        Check(reloaded.GifFps == 20 && reloaded.GifMaxDimension == 0 && !reloaded.KeepMarkedCopy && reloaded.TrimReplacesOriginal, "Recording editor settings did not round-trip.");
        string legacyRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "settings.json"), "{\"RetentionDays\":7,\"GifFps\":13,\"GifMaxDimension\":-5,\"OnboardingFuture\":{\"Seen\":true}}");
        var legacy = new CaptureHistoryStore(legacyRoot);
        Check(legacy.Settings.GifFps == 12 && legacy.Settings.GifMaxDimension == 960 && legacy.Settings.KeepMarkedCopy, "Invalid GIF settings were not reset.");
        legacy.UpdateSettings(settings => settings with { TrimReplacesOriginal = true });
        JsonNode saved = JsonNode.Parse(File.ReadAllText(Path.Combine(legacyRoot, "settings.json")))!;
        Check(saved["OnboardingFuture"]?["Seen"]?.GetValue<bool>() == true && saved["TrimReplacesOriginal"]!.GetValue<bool>(), "Unknown settings were dropped by an editor settings write.");
    }

    private static void VerifyExportPlanning()
    {
        Check(ExportPlanning.TimelapseSpeed(20, null) == 2 && ExportPlanning.TimelapseSpeed(100, null) == 4 && ExportPlanning.TimelapseSpeed(3600, null) == 128, "Automatic timelapse speed is wrong.");
        Check(ExportPlanning.TimelapseSpeed(100, 8) == 8 && ExportPlanning.TimelapseSpeed(100, 3) == 4, "Chosen timelapse speed is wrong.");
        EditTimeline timeline = EditTimeline.Create(10).CutRange(2, 4);
        IReadOnlyList<double> lapse = ExportPlanning.FrameTimes(timeline, 30, 4);
        Check(lapse.Count == 60 && Near(lapse[0], 0) && lapse.All(time => time < 2 || time >= 4), "Timelapse frames are wrong or fall inside a cut.");
        IReadOnlyList<double> gifTimes = ExportPlanning.FrameTimes(timeline, 12);
        Check(gifTimes.Count == 96 && gifTimes.Zip(gifTimes.Skip(1)).All(pair => pair.Second > pair.First), "GIF frame times are wrong.");
        IReadOnlyList<ushort> delays = ExportPlanning.GifDelays(12, 12);
        Check(delays.Sum(delay => delay) == 100 && delays.All(delay => delay is 8 or 9), "GIF delays do not add up to the frame rate.");
        Check(ExportPlanning.GifDelays(3, 60).All(delay => delay >= 2), "Very short GIF delays were not raised.");
        Check(ExportPlanning.IsTooShortForGif(EditTimeline.Create(10).WithInPoint(5).WithOutPoint(5.1), 12) && !ExportPlanning.IsTooShortForGif(timeline, 12), "Too-short GIF detection is wrong.");
        Check(ExportPlanning.FitWithin(1920, 1080, 960) == (960, 540) && ExportPlanning.FitWithin(640, 480, 960) == (640, 480) && ExportPlanning.FitWithin(1080, 1920, 0) == (1080, 1920), "GIF sizing is wrong.");
        Check(ExportPlanning.Even(641, 361) == (640, 360) && ExportPlanning.NormalizeGifFrameRate(7) == 12 && ExportPlanning.NormalizeGifMaxDimension(1280) == 1280, "Export normalization is wrong.");
    }

    private static void VerifyNv12()
    {
        byte[] bgra = new byte[4 * 4 * 4];
        for (int index = 0; index < 8; index++) { bgra[index * 4] = bgra[index * 4 + 1] = bgra[index * 4 + 2] = 255; bgra[index * 4 + 3] = 255; }
        byte[] nv12 = new byte[Nv12Converter.BufferSize(4, 4)];
        Nv12Converter.Convert(bgra, 4, 4, 16, nv12, bt709: false);
        Check(nv12[0] == 235 && nv12[8] == 16 && nv12[16] == 128 && nv12[17] == 128, $"NV12 levels are wrong (Y white {nv12[0]}, Y black {nv12[8]}, U {nv12[16]}, V {nv12[17]}).");
        byte[] red = [0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255];
        byte[] redNv12 = new byte[Nv12Converter.BufferSize(2, 2)];
        Nv12Converter.Convert(red, 2, 2, 8, redNv12, bt709: true);
        Check(redNv12[0] is >= 62 and <= 64 && redNv12[4] is >= 101 and <= 103 && redNv12[5] == 240, $"BT.709 red converted to Y {redNv12[0]}, U {redNv12[4]}, V {redNv12[5]}.");
    }

    private static void VerifyGif(string root)
    {
        Directory.CreateDirectory(root);
        BitmapSource[] frames = [Solid(48, 32, Colors.Red), Solid(48, 32, Colors.Lime), Solid(48, 32, Colors.Blue)];
        ushort[] delays = [8, 9, 8];
        string path = Path.Combine(root, "loop.gif");
        using (var stream = File.Create(path)) GifWriter.Write(stream, frames, delays);
        byte[] bytes = File.ReadAllBytes(path);
        Check(GifWriter.ReadLoopCount(bytes) == 0, "The GIF does not loop forever.");
        using (var stream = File.OpenRead(path))
        {
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Check(decoder.Frames.Count == 3, $"The GIF has {decoder.Frames.Count} frames instead of 3.");
            for (int index = 0; index < 3; index++)
            {
                var metadata = (BitmapMetadata)decoder.Frames[index].Metadata;
                Check(Convert.ToInt32(metadata.GetQuery("/grctlext/Delay")) == delays[index], $"Frame {index} delay is wrong.");
            }
        }
        var plain = new GifBitmapEncoder();
        plain.Frames.Add(BitmapFrame.Create(GifWriter.Palettize(frames[0])));
        plain.Frames.Add(BitmapFrame.Create(GifWriter.Palettize(frames[1])));
        using var buffer = new MemoryStream();
        plain.Save(buffer);
        byte[] looped = GifWriter.EnsureLooping(buffer.ToArray());
        Check(GifWriter.ReadLoopCount(looped) == 0 && ReferenceEquals(looped, GifWriter.EnsureLooping(looped)), "The loop block was not inserted exactly once.");
        var check = new GifBitmapDecoder(new MemoryStream(looped), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Check(check.Frames.Count == 2, "Inserting the loop block broke the GIF.");
    }

    private static void VerifyCompositor()
    {
        BitmapSource frame = Checkerboard(64, 48);
        var solid = new VideoMark(Guid.NewGuid(), new RectangleAnnotation(new Rect(8, 8, 16, 16), Colors.Black, 4, Redaction: true), 0, 3);
        var pixelate = new VideoMark(Guid.NewGuid(), new RectangleAnnotation(new Rect(32, 8, 24, 24), Colors.Black, 4, Redaction: true) { RedactionMode = RedactionStyle.Pixelate }, 0, 3);
        var state = new RecordingEditState(EditTimeline.Create(10), [solid, pixelate], false);
        Check(MarkCompositor.HasMarkupAt(state, 1) && !MarkCompositor.HasMarkupAt(state, 4), "Markup timing is wrong.");
        DecodedFrame composed = DecodedFrame.FromBitmapSource(MarkCompositor.Compose(frame, state, 1));
        Check(Pixel(composed, 12, 12) == (0, 0, 0), "A solid redaction was not burned in.");
        (byte red, byte green, byte blue) cell = Pixel(composed, 34, 10);
        Check(cell.red is > 40 and < 215 && cell.red == cell.green, $"Pixelation did not average the checkerboard ({cell.red}).");
        Check(Pixel(composed, 2, 40) == Pixel(DecodedFrame.FromBitmapSource(frame), 2, 40), "Pixels outside the marks changed.");
        VideoMark lost = solid with { Tracked = true, Keyframes = [new TrackKeyframe(0, new Rect(8, 8, 16, 16))], LostAt = 0.5, WhenLost = LostFieldPolicy.BlurWholePicture };
        DecodedFrame blurred = DecodedFrame.FromBitmapSource(MarkCompositor.Compose(frame, new RecordingEditState(EditTimeline.Create(10), [lost], false), 1));
        (byte red, _, _) = Pixel(blurred, 2, 40);
        Check(red is > 40 and < 215, "Losing the field did not blur the whole picture.");
    }

    /// <summary>Encodes a synthetic clip, then trims it, burns in a redaction, and writes a GIF and a timelapse.</summary>
    private static async Task<string> VerifyLiveExportsAsync(string root, string screenshotFolder)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return "SKIPPED live exports (Windows 10 2004 or later required)";
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "synthetic.mp4");
        const int width = 320, height = 180, frames = 60;
        const double frameRate = 30;
        try
        {
            await FrameMovieEncoder.EncodeAsync(source, width, height, frameRate, frames, (index, _) => Task.FromResult(SyntheticFrame(width, height, index)), null, null, CancellationToken.None);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or TypeLoadException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
        {
            return $"SKIPPED live exports (Media Foundation encoding unavailable: {error.Message})";
        }
        RecordingFrameReader reader = await RecordingFrameReader.OpenAsync(source);
        Check(reader.Info.Width == width && reader.Info.Height == height && reader.Info.Duration is > 1.8 and < 2.2 && !reader.Info.HasAudio, $"Synthetic clip reads back as {reader.Info}.");
        DecodedFrame first = await reader.ReadAsync(0.5, width, height, CancellationToken.None);
        Check(Pixel(first, 300, 170).red > 200, "The synthetic clip's frame did not decode (expected a red corner).");

        EditTimeline cut = EditTimeline.Create(reader.Info.Duration).CutRange(0.5, 1.0);
        var trimState = new RecordingEditState(cut, [], false);
        string trimmed = Path.Combine(root, "trimmed.mp4");
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var progress = new Progress<double>(_ => { });
        await RecordingExporter.ExportAsync(new RecordingExportJob(RecordingExportKind.Mp4, source, trimmed, trimState, reader.Info, 12, 960, null, root), reader, dispatcher, progress, CancellationToken.None);
        double trimmedDuration = (await RecordingFrameReader.OpenAsync(trimmed)).Info.Duration;
        Check(Math.Abs(trimmedDuration - cut.OutputDuration) < 0.15, $"Trimmed export is {trimmedDuration:0.00} s instead of {cut.OutputDuration:0.00} s.");

        var redaction = new VideoMark(Guid.NewGuid(), new RectangleAnnotation(new Rect(0, 0, 160, 90), Colors.Black, 4, Redaction: true), 0, 10);
        var markedState = new RecordingEditState(cut, [redaction], false);
        string marked = Path.Combine(root, "marked.mp4");
        await RecordingExporter.ExportAsync(new RecordingExportJob(RecordingExportKind.Mp4, source, marked, markedState, reader.Info, 12, 960, null, root), reader, dispatcher, progress, CancellationToken.None);
        RecordingFrameReader markedReader = await RecordingFrameReader.OpenAsync(marked);
        DecodedFrame markedFrame = await markedReader.ReadAsync(0.3, width, height, CancellationToken.None);
        Check(Pixel(markedFrame, 40, 40).red < 40 && Pixel(markedFrame, 40, 40).green < 40, "The redaction was not burned into the MP4.");
        Check(Math.Abs(markedReader.Info.Duration - cut.OutputDuration) < 0.15, "The marked MP4 has the wrong length.");

        string gif = Path.Combine(root, "clip.gif");
        await RecordingExporter.ExportAsync(new RecordingExportJob(RecordingExportKind.Gif, source, gif, trimState, reader.Info, 12, 0, null, root), reader, dispatcher, progress, CancellationToken.None);
        var gifDecoder = new GifBitmapDecoder(new MemoryStream(File.ReadAllBytes(gif)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Check(gifDecoder.Frames.Count == ExportPlanning.FrameTimes(cut, 12).Count && gifDecoder.Frames[0].PixelWidth == width && GifWriter.ReadLoopCount(File.ReadAllBytes(gif)) == 0, "The GIF export has the wrong frames, size, or loop.");

        string lapse = Path.Combine(root, "lapse.mp4");
        await RecordingExporter.ExportAsync(new RecordingExportJob(RecordingExportKind.Timelapse, source, lapse, trimState, reader.Info, 12, 960, 2, root), reader, dispatcher, progress, CancellationToken.None);
        double lapseDuration = (await RecordingFrameReader.OpenAsync(lapse)).Info.Duration;
        Check(Math.Abs(lapseDuration - cut.OutputDuration / 2) < 0.15, $"The 2x timelapse is {lapseDuration:0.00} s.");

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        string canceled = Path.Combine(root, "canceled.mp4");
        try
        {
            await RecordingExporter.ExportAsync(new RecordingExportJob(RecordingExportKind.Mp4, source, canceled, markedState, reader.Info, 12, 960, null, root), reader, dispatcher, progress, cancel.Token);
            Check(false, "A canceled export finished.");
        }
        catch (OperationCanceledException) { }
        Check(!File.Exists(canceled) && !Directory.EnumerateFiles(root, "*.partial*").Any(), "A canceled export left files behind.");

        await RenderEditorAsync(source, reader.Info, root, screenshotFolder);
        return $"live exports of a synthetic {width}x{height} clip (precise trim {trimmedDuration:0.00} s, burned-in redaction, {gifDecoder.Frames.Count}-frame looping GIF, 2x timelapse {lapseDuration:0.00} s, cancel cleanup) and editor window render";
    }

    private static async Task RenderEditorAsync(string movie, VideoInfo info, string root, string screenshotFolder)
    {
        var entry = new RecordingEntry(Guid.NewGuid(), movie, null, DateTime.UtcNow, info.Duration, info.Width, info.Height);
        RecordingEditorWindow editor = RecordingEditorWindow.Open(entry, new RecordingEditorContext(() => CaptureSettings.Default, _ => { }, _ => { }, root));
        try
        {
            await Task.Delay(1500);
            await editor.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            editor.UpdateLayout();
            DpiScale dpi = VisualTreeHelper.GetDpi(editor);
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(editor.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(editor.ActualHeight * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(editor);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(screenshotFolder, "recording-editor.png"));
            encoder.Save(stream);
        }
        finally { editor.Close(); }
    }

    private static DecodedFrame SyntheticFrame(int width, int height, int index)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                bool square = x >= index * 4 && x < index * 4 + 40 && y >= 60 && y < 100;
                bool corner = x >= width - 40 && y >= height - 30;
                pixels[offset] = (byte)(square ? 255 : 40);
                pixels[offset + 1] = (byte)(square ? 255 : 120);
                pixels[offset + 2] = (byte)(square || corner ? 255 : 60);
                if (corner) { pixels[offset] = 0; pixels[offset + 1] = 0; }
                pixels[offset + 3] = 255;
            }
        return new DecodedFrame(width, height, pixels);
    }

    private static BitmapSource Solid(int width, int height, Color color)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4) { pixels[index] = color.B; pixels[index + 1] = color.G; pixels[index + 2] = color.R; pixels[index + 3] = 255; }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Checkerboard(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte value = (byte)(((x / 2) + (y / 2)) % 2 == 0 ? 255 : 0);
                int offset = (y * width + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static (byte red, byte green, byte blue) Pixel(DecodedFrame frame, int x, int y)
    {
        int offset = y * frame.Stride + x * 4;
        return (frame.Bgra[offset + 2], frame.Bgra[offset + 1], frame.Bgra[offset]);
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-6;

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    /// <summary>Marks compare by value, with keyframe lists compared element by element.</summary>
    private sealed class MarkComparer : IEqualityComparer<VideoMark>
    {
        internal static readonly MarkComparer Instance = new();

        public bool Equals(VideoMark? first, VideoMark? second) =>
            first is not null && second is not null && first.Id == second.Id && first.Shape == second.Shape && first.Start == second.Start && first.Duration == second.Duration
            && first.Tracked == second.Tracked && first.Keyframes.SequenceEqual(second.Keyframes) && first.LostAt == second.LostAt && first.WhenLost == second.WhenLost;

        public int GetHashCode(VideoMark mark) => mark.Id.GetHashCode();
    }
}
