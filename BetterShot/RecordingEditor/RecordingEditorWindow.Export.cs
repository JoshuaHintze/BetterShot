using BetterShot.Controls;
using BetterShot.Export;
using BetterShot.Persistence;
using BetterShot.Recording;
using Microsoft.Win32;
using System.IO;
using System.Threading;
using System.Windows.Controls;
using static BetterShot.RecordingEditor.RecordingEditorChrome;

namespace BetterShot.RecordingEditor;

/// <summary>Exports (MP4, MP4 without audio, GIF, timelapse), saving the trim, and the marked copy used for sharing.</summary>
internal sealed partial class RecordingEditorWindow
{
    private bool NeedsMarkedCopyUpdate
    {
        get
        {
            if (!RecordingSharing.NeedsMarkedCopy(State, _context.Settings().KeepMarkedCopy)) return false;
            return _markedCopyFingerprint != RecordingEditProject.Fingerprint(State) || !File.Exists(RecordingSharing.MarkedCopyPath(Entry.MoviePath));
        }
    }

    private void OpenExportMenu(FrameworkElement target)
    {
        ContextMenu menu = Menu();
        menu.Items.Add(MenuItem("MP4", async (_, _) => await ExportAsync(RecordingExportKind.Mp4, null), LucideIconKind.Video,
            tip: "The kept stretch, audio included, as an MP4."));
        menu.Items.Add(MenuItem("MP4 without audio", async (_, _) => await ExportAsync(RecordingExportKind.Mp4WithoutAudio, null), LucideIconKind.MicOff,
            tip: "The kept stretch as a silent MP4."));
        menu.Items.Add(MenuItem("GIF", async (_, _) => await ExportAsync(RecordingExportKind.Gif, null), LucideIconKind.Image,
            tip: "An endlessly looping GIF. Frame rate and size are in Settings > Recording."));
        var timelapse = MenuItem("Timelapse", (_, _) => { }, LucideIconKind.Timer, tip: "Sped up. Audio is left out, so nothing plays back sped up.");
        timelapse.Items.Add(MenuItem("Automatic speed (about 30 seconds long)", async (_, _) => await ExportAsync(RecordingExportKind.Timelapse, null)));
        foreach (int speed in ExportPlanning.TimelapseSpeeds)
        {
            int chosen = speed;
            timelapse.Items.Add(MenuItem($"{speed}x", async (_, _) => await ExportAsync(RecordingExportKind.Timelapse, chosen)));
        }
        menu.Items.Add(timelapse);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuHeader("Drawn into the export. The recording stays as it was, so every mark stays editable."));
        OpenBelow(menu, target);
    }

    private OperationScope BeginOperation(string label)
    {
        var scope = new OperationScope(this);
        _operation = scope.Source;
        _progress.Value = 0;
        _progress.Visibility = Visibility.Visible;
        _cancelOperation.Visibility = Visibility.Visible;
        SetStatus(label);
        return scope;
    }

    private void EndOperation(OperationScope scope)
    {
        if (!ReferenceEquals(_operation, scope.Source)) return;
        _operation = null;
        _progress.Visibility = Visibility.Collapsed;
        _cancelOperation.Visibility = Visibility.Collapsed;
        UpdateToolbarState();
    }

    private void ReportProgress(double fraction) => _progress.Value = Math.Clamp(fraction, 0, 1);

    private bool CanExport(out RecordingFrameReader reader, out VideoInfo info)
    {
        reader = _reader!;
        info = _info!;
        if (_operation is not null) { SetStatus("Wait for the current export or tracking run to finish, or stop it."); return false; }
        if (_reader is null || _info is null) { SetStatus("Exports need Windows' media editing support, which is unavailable for this recording.", true); return false; }
        if (State.Timeline.KeptRanges.Count == 0) { SetStatus("Everything is cut, so there is nothing to export. Put a cut back first.", true); return false; }
        return true;
    }

    private async Task ExportAsync(RecordingExportKind kind, int? timelapseSpeed)
    {
        CommitTextEdit();
        Pause();
        if (!CanExport(out RecordingFrameReader reader, out VideoInfo info)) return;
        CaptureSettings settings = _context.Settings();
        if (kind == RecordingExportKind.Gif && ExportPlanning.IsTooShortForGif(State.Timeline, ExportPlanning.NormalizeGifFrameRate(settings.GifFps)))
        {
            SetStatus("Selection is too short for a GIF.", true);
            return;
        }
        if (AskExportPath(kind) is not string path) return;
        var job = new RecordingExportJob(kind, Entry.MoviePath, path, State, info, settings.GifFps, settings.GifMaxDimension, timelapseSpeed, _context.TemporaryFolder);
        string label = kind switch
        {
            RecordingExportKind.Gif => "Exporting GIF",
            RecordingExportKind.Timelapse => $"Export timelapse ({ExportPlanning.TimelapseSpeed(State.Timeline.OutputDuration, timelapseSpeed)}x, audio left out)",
            _ => "Exporting MP4"
        };
        using OperationScope operation = BeginOperation(label + "…");
        try
        {
            string? warning = await RecordingExporter.ExportAsync(job, reader, Dispatcher, new Progress<double>(ReportProgress), operation.Token);
            SetStatus($"Exported {Path.GetFileName(path)}.{(warning is null ? string.Empty : " " + warning)}", warning is not null);
        }
        catch (OperationCanceledException) { SetStatus("The export stopped."); }
        catch (Exception error) { SetStatus($"The export stopped: {error.Message}", true); }
    }

    private string? AskExportPath(RecordingExportKind kind)
    {
        string stem = Path.GetFileNameWithoutExtension(Entry.MoviePath);
        (string suffix, string extension, string filter) = kind switch
        {
            RecordingExportKind.Gif => (string.Empty, ".gif", "GIF animation (*.gif)|*.gif"),
            RecordingExportKind.Timelapse => (" (timelapse)", ".mp4", "MP4 video (*.mp4)|*.mp4"),
            RecordingExportKind.Mp4WithoutAudio => (" (no audio)", ".mp4", "MP4 video (*.mp4)|*.mp4"),
            _ => (" (edited)", ".mp4", "MP4 video (*.mp4)|*.mp4")
        };
        string folder = Path.GetDirectoryName(Entry.MoviePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var dialog = new SaveFileDialog
        {
            Title = "Export recording", Filter = filter, DefaultExt = extension, AddExtension = true, OverwritePrompt = true,
            InitialDirectory = folder, FileName = Path.GetFileName(FileNameTemplate.ResolveUniquePath(folder, stem + suffix + extension, File.Exists))
        };
        if (dialog.ShowDialog(this) != true) return null;
        if (string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(Entry.MoviePath), StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Choose a different name: exports never overwrite the recording itself. Use Save trim to replace it.", true);
            return null;
        }
        return dialog.FileName;
    }

    // ---------- Marked copy ----------

    /// <summary>Renders the copy shared when this recording is dragged (cuts and markup burned in) and records its fingerprint.</summary>
    private async Task<bool> UpdateMarkedCopyAsync()
    {
        if (!RecordingSharing.NeedsMarkedCopy(State, _context.Settings().KeepMarkedCopy))
        {
            RecordingSharing.DeleteMarkedCopy(Entry.MoviePath);
            if (_markedCopyFingerprint is not null) { _markedCopyFingerprint = null; SaveProject(); }
            UpdateToolbarState();
            return true;
        }
        if (!CanExport(out RecordingFrameReader reader, out VideoInfo info)) return false;
        RecordingEditState state = State;
        string copy = RecordingSharing.MarkedCopyPath(Entry.MoviePath);
        var job = new RecordingExportJob(RecordingExportKind.Mp4, Entry.MoviePath, copy, state, info, 0, 0, null, _context.TemporaryFolder);
        using OperationScope operation = BeginOperation("Updating marked copy…");
        try
        {
            string? warning = await RecordingExporter.ExportAsync(job, reader, Dispatcher, new Progress<double>(ReportProgress), operation.Token);
            _markedCopyFingerprint = RecordingEditProject.Fingerprint(state);
            bool stillCurrent = ReferenceEquals(state, State) || RecordingEditProject.Fingerprint(State) == _markedCopyFingerprint;
            if (!_history.IsDirty || stillCurrent) SaveProject();
            SetStatus((state.HasRedaction ? "Updated the covered copy. Dragging this recording shares it, never the unredacted original." : "Updated the marked copy. Dragging this recording now shares it.")
                + (warning is null ? string.Empty : " " + warning), warning is not null);
            return true;
        }
        catch (OperationCanceledException) { SetStatus("The marked copy was not updated."); return false; }
        catch (Exception error) { SetStatus($"Could not update the marked copy: {error.Message}", true); return false; }
        finally { UpdateToolbarState(); }
    }

    // ---------- Save trim ----------

    /// <summary>
    /// Writes the kept stretch (audio included, no markup) to a movie: replacing the original, or (the default) as a
    /// trimmed copy beside it. Marks, tracking paths, and click markers move to the new timeline and stay editable.
    /// </summary>
    private async Task ApplyTrimAsync()
    {
        CommitTextEdit();
        Pause();
        if (!State.Timeline.HasCuts) { SetStatus("Nothing is cut yet. Cut or set in/out points first."); return; }
        if (!CanExport(out _, out VideoInfo info)) return;
        bool replace = _context.Settings().TrimReplacesOriginal;
        if (replace && MessageBox.Show(this, "Replace the original recording with the trimmed one? The cut stretches are removed from the file for good. Marks stay editable.",
                "Save trim", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        RecordingEditState state = State;
        string folder = Path.GetDirectoryName(Entry.MoviePath)!;
        string target = replace ? Entry.MoviePath : FileNameTemplate.ResolveUniquePath(folder, Path.GetFileNameWithoutExtension(Entry.MoviePath) + " (trimmed).mp4", File.Exists);
        string temporary = Path.Combine(folder, "." + Path.GetFileNameWithoutExtension(target) + "-" + Guid.NewGuid().ToString("N")[..8] + ".partial.mp4");
        using OperationScope operation = BeginOperation("Saving the trimmed recording…");
        try
        {
            await TrimmedMovieRenderer.RenderAsync(Entry.MoviePath, state.Timeline.KeptRanges, temporary, info, includeAudio: info.HasAudio, new Progress<double>(ReportProgress), operation.Token);
            double duration = await MeasureDurationAsync(temporary, state.Timeline.OutputDuration);
            RecordingEditState trimmed = TrimRemap.Apply(state, duration);
            IReadOnlyList<RecordingEvent> events = TrimRemap.RemapEvents(_events, state.Timeline);
            if (replace) await ReplaceOriginalAsync(temporary, trimmed, events, duration);
            else SaveTrimmedCopy(temporary, target, trimmed, events, duration, info);
        }
        catch (OperationCanceledException) { SetStatus("The trim was not saved."); }
        catch (Exception error) { SetStatus($"The trim was not saved: {error.Message}", true); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<double> MeasureDurationAsync(string path, double fallback)
    {
        try { return (await RecordingFrameReader.OpenAsync(path)).Info.Duration; }
        catch (Exception) { return fallback; } // The planned length is exact to within a frame.
    }

    private void SaveTrimmedCopy(string temporary, string target, RecordingEditState trimmed, IReadOnlyList<RecordingEvent> events, double duration, VideoInfo info)
    {
        File.Move(temporary, target);
        RecordingEditProject.Save(target, trimmed, _view, null, [], null);
        WriteEvents(target, events);
        var entry = new RecordingEntry(Guid.NewGuid(), target, null, DateTime.UtcNow, duration, info.Width, info.Height);
        _context.RecordingAdded(entry);
        SetStatus($"Saved the trimmed copy beside the original as {Path.GetFileName(target)}. The original is unchanged.");
    }

    private async Task ReplaceOriginalAsync(string temporary, RecordingEditState trimmed, IReadOnlyList<RecordingEvent> events, double duration)
    {
        ShutDownPlayer();
        _reader = null;
        await MoveWithRetryAsync(temporary, Entry.MoviePath);
        RecordingSharing.DeleteMarkedCopy(Entry.MoviePath);
        _markedCopyFingerprint = null;
        _history.Reset(trimmed);
        _selectedSegment = null;
        RecordingEditProject.Save(Entry.MoviePath, trimmed, _view, null, _loaded.PreservedMarks, _loaded.UnknownFields);
        WriteEvents(Entry.MoviePath, events);
        _events = events;
        Entry = Entry with { DurationSeconds = duration };
        _context.RecordingChanged(Entry);
        try
        {
            _reader = await RecordingFrameReader.OpenAsync(Entry.MoviePath);
            _info = _reader.Info;
        }
        catch (Exception error) { SetStatus($"Trimmed, but the editor could not reopen the movie: {error.Message}", true); }
        OpenPlayer();
        _timeline.Fit();
        OnStateChanged();
        SetStatus($"Replaced the original with the trimmed recording ({FormatTime(duration)}).");
    }

    /// <summary>The player can hold the file for a moment after closing; retry briefly before giving up.</summary>
    private static async Task MoveWithRetryAsync(string source, string destination)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 15) { await Task.Delay(200); }
            catch (UnauthorizedAccessException) when (attempt < 15) { await Task.Delay(200); }
        }
    }

    private static void WriteEvents(string moviePath, IReadOnlyList<RecordingEvent> events)
    {
        var log = new RecordingEventLog();
        foreach (RecordingEvent item in events)
        {
            if (item.Kind == RecordingEvent.ClickKind && item.X is double x && item.Y is double y) log.AddClick(TimeSpan.FromSeconds(item.Time), x, y);
            else if (item.Kind == RecordingEvent.KeyKind && item.Text is string text) log.AddKey(TimeSpan.FromSeconds(item.Time), text);
        }
        try { log.WriteSidecar(moviePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // Click markers are an editing aid only.
    }

    /// <summary>The cancel source of one export or tracking run; disposing it hides the progress bar.</summary>
    private sealed class OperationScope : IDisposable
    {
        private readonly RecordingEditorWindow _owner;

        internal OperationScope(RecordingEditorWindow owner) => _owner = owner;

        internal CancellationTokenSource Source { get; } = new();
        internal CancellationToken Token => Source.Token;

        public void Dispose()
        {
            _owner.EndOperation(this);
            Source.Dispose();
        }
    }
}
