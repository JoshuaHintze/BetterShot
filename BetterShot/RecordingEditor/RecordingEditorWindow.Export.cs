using BetterShot.Controls;
using BetterShot.Export;
using BetterShot.Persistence;
using BetterShot.Recording;
using Microsoft.Win32;
using System.IO;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using static BetterShot.RecordingEditor.RecordingEditorChrome;

namespace BetterShot.RecordingEditor;

/// <summary>Exports (MP4, MP4 without audio, GIF, timelapse), saving the trim, and the marked copy used for sharing.</summary>
internal sealed partial class RecordingEditorWindow
{
    private bool NeedsMarkedCopyUpdate
    {
        get
        {
            bool camera = CameraSource.Resolve(Entry, _events).IsAvailable;
            if (!RecordingSharing.NeedsMarkedCopy(State, _context.Settings().KeepMarkedCopy, camera)) return false;
            return _markedCopyFingerprint != RecordingSharing.SharingFingerprint(Entry.MoviePath, State, CameraForExport())
                || !File.Exists(RecordingSharing.MarkedCopyPath(Entry.MoviePath));
        }
    }

    /// <summary>The export button: the selected card, with muted tracks left out of an MP4.</summary>
    private async Task ExportSelectedAsync()
    {
        switch (_exportKind)
        {
            case RecordingExportKind.Gif: await ExportAsync(RecordingExportKind.Gif, null); break;
            case RecordingExportKind.Timelapse: await ExportAsync(RecordingExportKind.Timelapse, _timelapseSpeed); break;
            default:
                (RecordingExportKind kind, IReadOnlyList<AudioTrackKind>? remix) = MovieAudioChoice();
                await ExportAsync(kind, null, remix);
                break;
        }
    }

    /// <summary>An MP4 keeps the original mix, re-mixes the unmuted tracks, or has no audio, following the track mutes.</summary>
    private (RecordingExportKind Kind, IReadOnlyList<AudioTrackKind>? Remix) MovieAudioChoice()
    {
        AudioOutputPlan plan = CurrentAudioPlan;
        return plan.Mode switch
        {
            AudioOutputMode.Silent => (RecordingExportKind.Mp4WithoutAudio, null),
            AudioOutputMode.Remix => (RecordingExportKind.Mp4, plan.RemixTracks),
            _ => (RecordingExportKind.Mp4, null)
        };
    }

    private OperationScope BeginOperation(string label)
    {
        var scope = new OperationScope(this);
        _operation = scope.Source;
        _progress.Value = 0;
        _progress.Visibility = Visibility.Visible;
        _cancelOperation.Visibility = Visibility.Visible;
        SetStatus(label);
        UpdateToolbarState();
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

    private CameraSource CameraForExport()
    {
        CameraSource source = CameraSource.Resolve(Entry, _events);
        // Preserve an explicit missing path so the exporter reports the problem instead of silently producing a
        // screen-only file. Resolve normally returns only existing files (including legacy-discovered sidecars).
        if (source.IsAvailable || File.Exists(CameraSourceResolver.SuppressionPath(Entry.MoviePath))) return source;
        string? expected = _cameraSource.Path ?? Entry.CameraPath;
        return string.IsNullOrWhiteSpace(expected) ? source : source with { Path = expected };
    }

    private bool CanExport(out RecordingFrameReader reader, out VideoInfo info)
    {
        reader = _reader!;
        info = _info!;
        if (_operation is not null) { SetStatus("Wait for the current export or tracking run to finish, or stop it."); return false; }
        if (_reader is null || _info is null) { SetStatus("Exports need Windows' media editing support, which is unavailable for this recording.", true); return false; }
        if (State.Timeline.KeptRanges.Count == 0) { SetStatus("Everything is cut, so there is nothing to export. Put a cut back first.", true); return false; }
        return true;
    }

    private async Task ExportAsync(RecordingExportKind kind, int? timelapseSpeed, IReadOnlyList<AudioTrackKind>? remixTracks = null)
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
        var job = new RecordingExportJob(kind, Entry.MoviePath, path, State, info, settings.GifFps, settings.GifMaxDimension, timelapseSpeed,
            _context.TemporaryFolder, remixTracks, CameraForExport(),
            kind is RecordingExportKind.Mp4 or RecordingExportKind.Mp4WithoutAudio ? SelectedMp4Preset : Mp4ExportPreset.Original);
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
            string fileSize = RecordingEditorText.Size(new FileInfo(path).Length);
            SetStatus($"Exported {Path.GetFileName(path)} ({fileSize}).{(warning is null ? string.Empty : " " + warning)}", warning is not null);
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
        if (string.Equals(Path.GetFullPath(dialog.FileName), CameraForExport().Path, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Choose a different name to keep the webcam source available for editing.", true);
            return null;
        }
        return dialog.FileName;
    }

    // ---------- Marked copy ----------

    /// <summary>Renders the copy shared when this recording is dragged (cuts and markup burned in) and records its fingerprint.</summary>
    private async Task<bool> UpdateMarkedCopyAsync()
    {
        bool hasCamera = CameraSource.Resolve(Entry, _events).IsAvailable;
        if (!RecordingSharing.NeedsMarkedCopy(State, _context.Settings().KeepMarkedCopy, hasCamera))
        {
            RecordingSharing.DeleteMarkedCopy(Entry.MoviePath);
            if (_markedCopyFingerprint is not null) { _markedCopyFingerprint = null; SaveProject(); }
            UpdateToolbarState();
            return true;
        }
        if (!CanExport(out RecordingFrameReader reader, out VideoInfo info)) return false;
        RecordingEditState state = State;
        string copy = RecordingSharing.MarkedCopyPath(Entry.MoviePath);
        (RecordingExportKind kind, IReadOnlyList<AudioTrackKind>? remix) = MovieAudioChoice();
        var job = new RecordingExportJob(kind, Entry.MoviePath, copy, state, info, 0, 0, null, _context.TemporaryFolder, remix,
            CameraForExport());
        using OperationScope operation = BeginOperation("Updating marked copy…");
        try
        {
            string? warning = await RecordingExporter.ExportAsync(job, reader, Dispatcher, new Progress<double>(ReportProgress), operation.Token);
            CameraSource camera = CameraForExport();
            _markedCopyFingerprint = RecordingSharing.SharingFingerprint(Entry.MoviePath, state, camera);
            bool stillCurrent = ReferenceEquals(state, State) || RecordingSharing.SharingFingerprint(Entry.MoviePath, State, camera) == _markedCopyFingerprint;
            if (!_history.IsDirty || stillCurrent) SaveProject();
            SetStatus(warning ?? "The share copy is ready.", warning is not null);
            return true;
        }
        catch (OperationCanceledException) { SetStatus("The marked copy was not updated."); return false; }
        catch (Exception error) { SetStatus($"Could not update the marked copy: {error.Message}", true); return false; }
        finally { UpdateToolbarState(); }
    }

    // ---------- Done: replace the original ----------

    /// <summary>
    /// Renders the recording with every cut, mute, and mark burned in and puts it in place of the original file. The
    /// edit project is cleared afterwards (its marks are now part of the picture), the separate audio tracks and click
    /// markers are cut to match, and the cached share copy is removed because the original now contains the edit.
    /// </summary>
    private async Task<bool> ReplaceOriginalWithEditsAsync()
    {
        if (!CanExport(out RecordingFrameReader reader, out VideoInfo info)) return false;
        RecordingEditState state = State;
        string folder = Path.GetDirectoryName(Entry.MoviePath)!;
        string temporary = Path.Combine(folder, "." + Path.GetFileNameWithoutExtension(Entry.MoviePath) + "-" + Guid.NewGuid().ToString("N")[..8] + ".partial.mp4");
        (RecordingExportKind kind, IReadOnlyList<AudioTrackKind>? remix) = MovieAudioChoice();
        CameraSource camera = CameraForExport();
        var job = new RecordingExportJob(kind, Entry.MoviePath, temporary, state, info, 0, 0, null, _context.TemporaryFolder, remix, camera);
        using OperationScope operation = BeginOperation("Saving your edits into the recording…");
        try
        {
            string? warning = await RecordingExporter.ExportAsync(job, reader, Dispatcher, new Progress<double>(ReportProgress), operation.Token);
            double duration = await MeasureDurationAsync(temporary, state.Timeline.KeptRanges.Sum(range => range.Duration));
            IReadOnlyList<KeptRange> ranges = state.Timeline.KeptRanges;
            IReadOnlyList<RecordingEvent> events = TrimRemap.RemapEvents(_events, state.Timeline);
            ShutDownPlayer();
            _trackPreview?.Dispose(); // Releases the separate tracks so they can be replaced.
            _trackPreview = null;
            _filmstrip.Attach(null, 0, 0);
            _reader?.Dispose();
            _reader = null;
            await MoveWithRetryAsync(temporary, Entry.MoviePath);
            if (state.Timeline.HasCuts) AudioRemixer.TrimSidecars(Entry.MoviePath, ranges, Entry.MoviePath);
            WriteEvents(Entry.MoviePath, events);
            if (camera.IsAvailable) CameraSourceResolver.Suppress(Entry.MoviePath);
            RecordingSharing.DeleteMarkedCopy(Entry.MoviePath);
            try { string project = RecordingEditProject.SidecarPath(Entry.MoviePath); if (File.Exists(project)) File.Delete(project); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // A stale project would only re-show marks; the file itself is replaced.
            _history.Reset(RecordingEditState.Create(Math.Max(0.1, duration)));
            Entry = Entry with { CameraPath = camera.IsAvailable ? null : Entry.CameraPath, DurationSeconds = duration };
            _context.RecordingChanged(Entry);
            if (warning is not null) MessageBox.Show(this, warning, "BetterShot", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (OperationCanceledException) { SetStatus("The original was not replaced."); return false; }
        catch (Exception error) { SetStatus($"The original was not replaced: {error.Message}", true); return false; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ---------- Save trim ----------

    /// <summary>
    /// Writes the kept stretch (audio included, no markup) to a movie: replacing the original, or (the default) as a
    /// trimmed copy beside it. When the movie allows it the samples are copied as they are (lossless, video starting
    /// on a keyframe); otherwise the kept stretch is re-encoded precisely. The separate audio tracks are cut to match,
    /// and marks, tracking paths, and click markers move to the new timeline and stay editable.
    /// </summary>
    private async Task ApplyTrimAsync()
    {
        CommitTextEdit();
        Pause();
        if (!State.Timeline.HasCuts) { SetStatus("Drag the yellow handles or cut a stretch out first."); return; }
        if (!CanExport(out RecordingFrameReader reader, out VideoInfo info)) return;
        bool replace = _replaceOriginal;
        string cameraNote = HasCamera ? " The camera layout becomes permanent in the trimmed video." : string.Empty;
        if (replace && MessageBox.Show(this, "Replace the original recording with the trimmed one? The cut stretches are removed from the file for good. Marks stay editable." + cameraNote,
                "Trim the recording?", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        RecordingEditState state = State;
        LosslessTrimPlan? lossless = CurrentLosslessPlan(out _);
        Mp4MovieIndex? index = _losslessIndex;
        IReadOnlyList<KeptRange> ranges = lossless?.Ranges ?? state.Timeline.KeptRanges;
        string folder = Path.GetDirectoryName(Entry.MoviePath)!;
        string target = replace ? Entry.MoviePath : FileNameTemplate.ResolveUniquePath(folder, Path.GetFileNameWithoutExtension(Entry.MoviePath) + " (trimmed).mp4", File.Exists);
        string temporary = Path.Combine(folder, "." + Path.GetFileNameWithoutExtension(target) + "-" + Guid.NewGuid().ToString("N")[..8] + ".partial.mp4");
        using OperationScope operation = BeginOperation(lossless is null ? "Trimming (re-encoding the kept stretch)…" : "Trimming without re-encoding…");
        try
        {
            var progress = new Progress<double>(ReportProgress);
            CameraSource camera = CameraForExport();
            if (camera.IsAvailable)
            {
                // A trimmed movie gets a self-contained camera picture. Keep annotations editable by rendering only
                // the camera here; the project's marks are remapped below as before.
                RecordingEditState cameraOnly = state with
                {
                    Timeline = EditTimeline.FromKeptRanges(state.Timeline.Duration, ranges),
                    Marks = [],
                    HideMarkup = true
                };
                var job = new RecordingExportJob(RecordingExportKind.Mp4, Entry.MoviePath, temporary, cameraOnly, info, 0, 0, null,
                    _context.TemporaryFolder, null, camera);
                await RecordingExporter.ExportAsync(job, reader, Dispatcher, progress, operation.Token);
                lossless = null;
            }
            else if (lossless is not null && index is not null)
                await Task.Run(() => Mp4Remuxer.Write(index, ranges, Entry.MoviePath, temporary, progress, operation.Token), operation.Token);
            else
            {
                Mp4ExportPlan plan = ExportPlanning.PlanMp4(Mp4ExportPreset.Original, info.Width, info.Height, info.FrameRate);
                await TrimmedMovieRenderer.RenderAsync(Entry.MoviePath, ranges, temporary, plan, includeAudio: info.HasAudio, progress, operation.Token);
            }
            double duration = await MeasureDurationAsync(temporary, ranges.Sum(range => range.Duration));
            RecordingEditState actual = state with { Timeline = EditTimeline.FromKeptRanges(state.Timeline.Duration, ranges) };
            RecordingEditState trimmed = TrimRemap.Apply(actual, duration);
            if (camera.IsAvailable) trimmed = trimmed with { Camera = CameraEdit.Default };
            IReadOnlyList<RecordingEvent> events = TrimRemap.RemapEvents(_events, actual.Timeline);
            string how = lossless is null ? "re-encoded" : "copied without re-encoding";
            if (replace) await ReplaceOriginalAsync(temporary, trimmed, events, duration, ranges, how, camera.IsAvailable);
            else SaveTrimmedCopy(temporary, target, trimmed, events, duration, info, ranges, how, camera.IsAvailable);
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

    private void SaveTrimmedCopy(string temporary, string target, RecordingEditState trimmed, IReadOnlyList<RecordingEvent> events, double duration, VideoInfo info,
        IReadOnlyList<KeptRange> ranges, string how, bool cameraRendered)
    {
        File.Move(temporary, target);
        AudioRemixer.TrimSidecars(Entry.MoviePath, ranges, target);
        RecordingEditProject.Save(target, trimmed, _view, null, [], null);
        WriteEvents(target, events);
        if (cameraRendered) CameraSourceResolver.Suppress(target);
        var entry = new RecordingEntry(Guid.NewGuid(), target, null, DateTime.UtcNow, duration, info.Width, info.Height);
        _context.RecordingAdded(entry);
        SetStatus($"Saved the trimmed copy ({how}) beside the original as {Path.GetFileName(target)}. The original is unchanged.");
    }

    private async Task ReplaceOriginalAsync(string temporary, RecordingEditState trimmed, IReadOnlyList<RecordingEvent> events, double duration,
        IReadOnlyList<KeptRange> ranges, string how, bool cameraRendered)
    {
        ShutDownPlayer();
        _trackPreview?.Dispose(); // Releases the separate tracks so they can be replaced.
        _trackPreview = null;
        _filmstrip.Attach(null, 0, 0);
        _reader?.Dispose();
        _reader = null;
        await MoveWithRetryAsync(temporary, Entry.MoviePath);
        AudioRemixer.TrimSidecars(Entry.MoviePath, ranges, Entry.MoviePath);
        RecordingSharing.DeleteMarkedCopy(Entry.MoviePath);
        _markedCopyFingerprint = null;
        _history.Reset(trimmed);
        _selectedSegment = null;
        RecordingEditProject.Save(Entry.MoviePath, trimmed, _view, null, _loaded.PreservedMarks, _loaded.UnknownFields);
        WriteEvents(Entry.MoviePath, events);
        if (cameraRendered) CameraSourceResolver.Suppress(Entry.MoviePath);
        _events = events;
        Entry = Entry with { CameraPath = cameraRendered ? null : Entry.CameraPath, DurationSeconds = duration };
        _context.RecordingChanged(Entry);
        try
        {
            _reader = await RecordingFrameReader.OpenAsync(Entry.MoviePath);
            _info = _reader.Info;
        }
        catch (Exception error) { SetStatus($"Trimmed, but the editor could not reopen the movie: {error.Message}", true); }
        OpenPlayer();
        UpdateSubtitle();
        LoadAudioTracks();
        LoadLosslessIndex();
        await AttachFilmstripAsync();
        _timeline.Fit();
        OnStateChanged();
        SetStatus($"Replaced the original with the trimmed recording ({RecordingTimeText.Tenths(duration)}, {how}).");
    }

    // ---------- Sharing ----------

    private string? _sharePath;
    private bool _shareHooked;
    private Point? _shareDragStart;

    private void OpenShareMenu(FrameworkElement target)
    {
        ContextMenu menu = Menu();
        menu.Items.Add(MenuHeader(State.HasRedaction ? "Shares the covered copy, never the unredacted recording." : "Shares the recording with your cuts, mutes, and markup (the marked copy)."));
        menu.Items.Add(MenuItem("Copy the file", async (_, _) => await CopyShareFileAsync(), LucideIconKind.Copy, tip: "Paste it into a chat, an email, or a folder."));
        menu.Items.Add(MenuItem("Share…", async (_, _) => await ShareWithWindowsAsync(), LucideIconKind.Share, tip: "Opens the Windows share sheet (Nearby sharing, Mail, and apps)."));
        if (State.HasEdits || HasCamera && State.Camera.Visible)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Replace original recording…", async (_, _) =>
            {
                Pause();
                CommitTextEdit();
                if (MessageBox.Show(this, "Permanently replace the source movie with the current video, audio and camera layout?",
                    "Replace original recording", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
                if (!SaveProject() || !await ReplaceOriginalWithEditsAsync()) return;
                _closingConfirmed = true;
                Close();
            }));
        }
        OpenBelow(menu, target);
    }

    /// <summary>Saves, brings the marked copy up to date, and returns the file that may be shared (null with a status when none may be).</summary>
    private async Task<string?> PrepareShareFileAsync()
    {
        if (!SaveEditableProject()) return null;
        if (NeedsMarkedCopyUpdate && !await UpdateMarkedCopyAsync()) return null;
        (string? path, string? note) = RecordingSharing.ResolveSharePath(Entry.MoviePath, Entry.CameraPath);
        if (note is not null) SetStatus(note, path is null);
        return path;
    }

    private async Task CopyShareFileAsync()
    {
        if (await PrepareShareFileAsync() is not string path) return;
        try
        {
            var files = new System.Collections.Specialized.StringCollection { path };
            Clipboard.SetFileDropList(files);
            SetStatus($"Copied {Path.GetFileName(path)}. Paste it into a chat, an email, or a folder.");
        }
        catch (System.Runtime.InteropServices.ExternalException error) { SetStatus($"The clipboard is busy: {error.Message}", true); }
    }

    private async Task ShareWithWindowsAsync()
    {
        if (await PrepareShareFileAsync() is not string path) return;
        try
        {
            IntPtr window = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Windows.ApplicationModel.DataTransfer.DataTransferManager manager = Windows.ApplicationModel.DataTransfer.DataTransferManagerInterop.GetForWindow(window);
            _sharePath = path;
            if (!_shareHooked)
            {
                manager.DataRequested += Share_DataRequested;
                _shareHooked = true;
            }
            Windows.ApplicationModel.DataTransfer.DataTransferManagerInterop.ShowShareUIForWindow(window);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or TypeLoadException)
        {
            SetStatus($"The Windows share sheet is unavailable ({error.Message}). Use Copy the file instead.", true);
        }
    }

    private async void Share_DataRequested(Windows.ApplicationModel.DataTransfer.DataTransferManager sender, Windows.ApplicationModel.DataTransfer.DataRequestedEventArgs args)
    {
        Windows.ApplicationModel.DataTransfer.DataRequestDeferral deferral = args.Request.GetDeferral();
        try
        {
            if (_sharePath is not string path) { args.Request.FailWithDisplayText("Nothing to share."); return; }
            Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            args.Request.Data.Properties.Title = Path.GetFileName(path);
            args.Request.Data.SetStorageItems([file]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            args.Request.FailWithDisplayText(error.Message);
        }
        finally { deferral.Complete(); }
    }

    /// <summary>Dragging the share button drops the shareable file (like dragging a library card).</summary>
    private void AttachShareDrag(Button button)
    {
        button.PreviewMouseLeftButtonDown += (_, e) => _shareDragStart = e.GetPosition(button);
        button.PreviewMouseLeftButtonUp += (_, _) => _shareDragStart = null;
        button.PreviewMouseMove += (_, e) =>
        {
            if (_shareDragStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
            Vector moved = e.GetPosition(button) - start;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _shareDragStart = null;
            if (!SaveEditableProject()) return;
            if (NeedsMarkedCopyUpdate)
            {
                SetStatus("Updating the marked copy first. Drag again when it is ready.");
                _ = UpdateMarkedCopyAsync();
                return;
            }
            (string? path, string? note) = RecordingSharing.ResolveSharePath(Entry.MoviePath, Entry.CameraPath);
            if (note is not null) SetStatus(note, path is null);
            if (path is null) return;
            DragDrop.DoDragDrop(button, Presentation.RecordingFiles.CreateDragData(path), DragDropEffects.Copy);
        };
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
