using BetterShot.Controls;
using BetterShot.Recording;
using System.IO;
using System.Threading;

namespace BetterShot.RecordingEditor;

/// <summary>
/// Audio tracks: which lanes the recording has (System and Mic from the separate-track sidecars, or one Audio lane for
/// older recordings), their waveforms (computed off the UI thread), per-track mutes (saved in the project, applied to
/// exports), and the preview that plays only the unmuted tracks while the player's mixed sound is muted.
/// </summary>
internal sealed partial class RecordingEditorWindow
{
    private const double PreviewDriftSeconds = 0.2;
    private readonly FilmstripCache _filmstrip = new();
    private readonly Dictionary<string, WaveformPeaks> _waveforms = [];
    private IReadOnlyList<AudioTrackKind> _separateTracks = [];
    private IReadOnlyList<TimelineAudioLane> _audioLanes = [];
    private TrackAudioPreview? _trackPreview;
    private CancellationTokenSource? _waveformLoad;
    private bool _previewFailed;

    public IReadOnlyList<TimelineAudioLane> AudioLanes => _audioLanes;
    public FilmstripCache Filmstrip => _filmstrip;
    public double VideoAspect => _info is { Width: > 0, Height: > 0 } info ? info.Width / (double)info.Height : 16 / 9.0;

    private AudioOutputPlan CurrentAudioPlan => AudioTrackPlanning.Plan(State.MutedTracks, _separateTracks, _info?.HasAudio ?? false);

    /// <summary>Finds the tracks on disk, builds the lanes, and starts computing their waveforms.</summary>
    private void LoadAudioTracks()
    {
        _separateTracks = RecordingAudioTracks.Existing(Entry.MoviePath);
        _waveforms.Clear();
        RebuildAudioLanes();
        _waveformLoad?.Cancel();
        _waveformLoad = new CancellationTokenSource();
        _ = LoadWaveformsAsync(Entry.MoviePath, _separateTracks, _info?.HasAudio ?? false, _waveformLoad.Token);
    }

    private void RebuildAudioLanes()
    {
        var lanes = new List<TimelineAudioLane>();
        foreach (AudioTrackKind kind in _separateTracks)
        {
            string key = AudioTrackPlanning.Key(kind);
            bool microphone = kind == AudioTrackKind.Microphone;
            lanes.Add(new TimelineAudioLane(key, microphone ? "Mic" : "System", microphone ? LucideIconKind.Mic : LucideIconKind.Volume2,
                _waveforms.GetValueOrDefault(key), State.MutedTracks.HasFlag(AudioTrackPlanning.Flag(kind))));
        }
        if (_separateTracks.Count == 0 && _info?.HasAudio == true)
            lanes.Add(new TimelineAudioLane(AudioTrackPlanning.SoundtrackKey, "Audio", LucideIconKind.Volume2, _waveforms.GetValueOrDefault(AudioTrackPlanning.SoundtrackKey),
                State.MutedTracks.HasFlag(MutedAudioTracks.Soundtrack)));
        bool countChanged = lanes.Count != _audioLanes.Count;
        _audioLanes = lanes;
        if (countChanged) RebuildTrackLabels();
        else UpdateTrackLabels();
        _timeline.InvalidateVisual();
    }

    private async Task LoadWaveformsAsync(string moviePath, IReadOnlyList<AudioTrackKind> tracks, bool movieHasAudio, CancellationToken cancellation)
    {
        try
        {
            foreach (AudioTrackKind kind in tracks)
            {
                string path = RecordingAudioTracks.SidecarPath(moviePath, kind);
                WaveformPeaks peaks = await Task.Run(() => WaveformEnvelope.FromWaveFile(path, cancellation), cancellation);
                if (cancellation.IsCancellationRequested) return;
                _waveforms[AudioTrackPlanning.Key(kind)] = peaks;
                RebuildAudioLanes();
            }
            if (tracks.Count == 0 && movieHasAudio)
            {
                // Older recordings: decode the movie's one soundtrack once (Windows' transcoder) just for its picture.
                string wave = await KeptAudio.ExtractSoundtrackAsync(moviePath, _context.TemporaryFolder, cancellation);
                try
                {
                    WaveformPeaks peaks = await Task.Run(() => WaveformEnvelope.FromWaveFile(wave, cancellation), cancellation);
                    if (cancellation.IsCancellationRequested) return;
                    _waveforms[AudioTrackPlanning.SoundtrackKey] = peaks;
                    RebuildAudioLanes();
                }
                finally { KeptAudio.TryDelete(wave); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Waveforms are a picture only; the lanes still mute and export correctly without them.
            SetStatus($"The audio waveform could not be drawn: {error.Message}");
        }
    }

    private void ToggleTrackMute(string key)
    {
        MutedAudioTracks flag = AudioTrackPlanning.FlagForKey(key);
        if (flag == MutedAudioTracks.None) return;
        bool muting = !State.MutedTracks.HasFlag(flag);
        if (!Apply(state => state with { MutedTracks = state.MutedTracks ^ flag })) return;
        string name = _audioLanes.FirstOrDefault(lane => lane.Key == key)?.Label ?? "Audio";
        SetStatus(muting
            ? $"{name} is muted in the player and left out of exports and the marked copy. The recording itself keeps it."
            : $"{name} is back in the player and the exports.");
    }

    /// <summary>After an edit (or undo) changes mutes: refresh lanes and, while playing, switch what is heard.</summary>
    private void RefreshAudioRouting()
    {
        RebuildAudioLanes();
        ApplyPlayerAudio();
        if (!_playing) return;
        if (CurrentAudioPlan.Mode == AudioOutputMode.Remix) StartTrackPreview(_playhead);
        else StopTrackPreview();
    }

    /// <summary>The player plays the movie's mixed sound only when nothing is muted; otherwise the track preview takes over.</summary>
    private void ApplyPlayerAudio()
    {
        _media.IsMuted = _playerMuted || CurrentAudioPlan.Mode != AudioOutputMode.Original;
        _media.Volume = _volume;
        _trackPreview?.SetVolume(_playerMuted ? 0 : (float)_volume);
    }

    private void StartTrackPreview(double start)
    {
        AudioOutputPlan plan = CurrentAudioPlan;
        if (plan.Mode != AudioOutputMode.Remix || _previewFailed) { StopTrackPreview(); return; }
        string[] paths = plan.RemixTracks.Select(kind => RecordingAudioTracks.SidecarPath(Entry.MoviePath, kind)).ToArray();
        try
        {
            if (_trackPreview is null || !_trackPreview.Paths.SequenceEqual(paths))
            {
                _trackPreview?.Dispose();
                _trackPreview = TrackAudioPreview.Open(paths);
            }
            _trackPreview.Play(start, _speed, _playerMuted ? 0 : (float)_volume);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _previewFailed = true;
            _trackPreview?.Dispose();
            _trackPreview = null;
            SetStatus($"Muted tracks can't be previewed here ({error.Message}), so the player is silent. Exports still leave them out.", true);
        }
    }

    private void StopTrackPreview() => _trackPreview?.Stop();

    /// <summary>Re-seeks the track preview when it drifts from the video (device clocks differ slightly).</summary>
    private void KeepTrackPreviewInStep(double position)
    {
        if (_trackPreview is not { IsPlaying: true } preview) return;
        if (Math.Abs(preview.HeardPosition - position) > PreviewDriftSeconds) preview.Seek(position);
    }

    private void DisposeAudio()
    {
        _waveformLoad?.Cancel();
        _trackPreview?.Dispose();
        _trackPreview = null;
        _filmstrip.Dispose();
    }
}
