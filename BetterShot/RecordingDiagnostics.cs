using BetterShot.Capture;
using BetterShot.Export;
using BetterShot.Persistence;
using BetterShot.Platform;
using BetterShot.Presentation;
using BetterShot.Recording;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls.Primitives;
using Key = System.Windows.Input.Key;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace BetterShot;

/// <summary>
/// Phase 6 screen-recording checks: settings round-trip and migration, recording file names, crop/even-size/DPI math,
/// the click/key event sidecar, keystroke filtering, the recording state machine and pausable clock, audio conversion
/// and mixing, the recordings library index, Settings rendering, and (when Windows graphics capture is available) a
/// real two-second display recording verified as a playable MP4. Uses isolated fixture folders only.
/// </summary>
internal static class RecordingDiagnostics
{
    private static readonly DateTime SampleTime = new(2026, 8, 5, 10, 22, 31, DateTimeKind.Local);

    internal static async Task RunAsync(string? output)
    {
        string directory = string.IsNullOrWhiteSpace(output) ? AppContext.BaseDirectory : Path.HasExtension(output) ? Path.GetDirectoryName(Path.GetFullPath(output))! : output;
        Directory.CreateDirectory(directory);
        string runRoot = Path.Combine(directory, "recording-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        VerifySettings(Path.Combine(runRoot, "settings"));
        VerifyFileNames();
        VerifyGeometry();
        VerifyEventSidecar(Path.Combine(runRoot, "events"));
        VerifyKeystrokes();
        VerifyStateMachine();
        VerifyClock();
        VerifyAudio();
        VerifyLibrary(Path.Combine(runRoot, "library"));
        VerifySetupLayout();
        VerifyControlsBar();
        VerifySetupPanel(Path.Combine(runRoot, "setup"), directory);
        await VerifyUiAsync(directory);
        string live = await VerifyLiveRecordingAsync(Path.Combine(runRoot, "live"));
        App.WriteTestResult(Path.Combine(directory, "recording-test.txt"),
            "PASS: recording settings round-trip/migration/clamping, recording file names, crop and even-size math with odd sizes, negative origins and DPI, event sidecar JSON, keystroke filtering, state machine incl. cancel paths, pausable clock, PCM conversion/resampling/mixing with pause, recordings library index, setup panel (toggle/countdown/device round-trip, Enter/Esc, placement near edges, full-screen and negative-origin monitors, 100%/150% renders), controls bar time format/placement/level bars, Settings rendering and sync from the setup panel; " + live);
    }

    private static void VerifySettings(string root)
    {
        CaptureSettings defaults = CaptureSettings.Default;
        Check(!defaults.RecordMicrophone && !defaults.RecordSystemAudio && !defaults.RecordCamera && !defaults.HighlightClicks && !defaults.ShowKeystrokes && !defaults.ReviewAfterRecording, "Recording toggles should default off.");
        Check(defaults.RecordingCountdownSeconds == 3 && defaults.RecordingFrameRate == 30 && defaults.RecordingFileNamePattern == "Recording %date at %time" && defaults.RecordHotkey.IsEmpty, "Recording defaults changed.");

        var store = new CaptureHistoryStore(Path.Combine(root, "roundtrip"));
        var recordKey = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x52);
        store.UpdateSettings(settings => settings with
        {
            RecordMicrophone = true, MicrophoneDeviceId = "{0.0.1.00000000}.{mic}", RecordSystemAudio = true, RecordCamera = true,
            CameraDeviceId = "\\\\?\\USB#camera", HighlightClicks = true, ShowKeystrokes = true, RecordingCountdownSeconds = 5,
            ReviewAfterRecording = true, RecordingFileNamePattern = "Demo %counter", RecordingFrameRate = 60, RecordHotkey = recordKey
        });
        CaptureSettings reloaded = new CaptureHistoryStore(Path.Combine(root, "roundtrip")).Settings;
        Check(reloaded.RecordMicrophone && reloaded.RecordSystemAudio && reloaded.RecordCamera && reloaded.HighlightClicks && reloaded.ShowKeystrokes && reloaded.ReviewAfterRecording, "Recording toggles did not round-trip.");
        Check(reloaded.MicrophoneDeviceId == "{0.0.1.00000000}.{mic}" && reloaded.CameraDeviceId == "\\\\?\\USB#camera", "Device ids did not round-trip.");
        Check(reloaded.RecordingCountdownSeconds == 5 && reloaded.RecordingFrameRate == 60 && reloaded.RecordingFileNamePattern == "Demo %counter" && reloaded.RecordHotkey == recordKey, "Recording choices did not round-trip.");
        Check(reloaded.HotkeyFor(HotkeyAction.Record) == recordKey && reloaded.WithHotkey(HotkeyAction.Record, HotkeyBinding.None).RecordHotkey.IsEmpty, "Record hotkey accessors are wrong.");

        string legacyRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        string legacyPath = Path.Combine(legacyRoot, "settings.json");
        File.WriteAllText(legacyPath, "{\"RetentionDays\":14,\"CaptureDelaySeconds\":3,\"AskClaudeFuture\":{\"Enabled\":true}}");
        var legacy = new CaptureHistoryStore(legacyRoot);
        Check(legacy.Settings.RecordingCountdownSeconds == 3 && legacy.Settings.RecordingFrameRate == 30 && !legacy.Settings.RecordMicrophone, "Older settings did not get recording defaults.");
        legacy.UpdateSettings(settings => settings with { RecordSystemAudio = true });
        JsonNode saved = JsonNode.Parse(File.ReadAllText(legacyPath))!;
        Check(saved["AskClaudeFuture"]?["Enabled"]?.GetValue<bool>() == true, "Unknown settings fields were dropped by a recording write.");
        Check(saved["RecordSystemAudio"]!.GetValue<bool>() && saved["RetentionDays"]!.GetValue<int>() == 14 && saved["CaptureDelaySeconds"]!.GetValue<int>() == 3, "A recording write changed other fields.");

        string clampRoot = Path.Combine(root, "clamp");
        Directory.CreateDirectory(clampRoot);
        File.WriteAllText(Path.Combine(clampRoot, "settings.json"), "{\"RetentionDays\":7,\"RecordingCountdownSeconds\":7,\"RecordingFrameRate\":45,\"RecordingFileNamePattern\":\" \",\"RecordHotkey\":{\"Modifiers\":0,\"Key\":65},\"MicrophoneDeviceId\":null}");
        CaptureSettings clamped = new CaptureHistoryStore(clampRoot).Settings;
        Check(clamped.RecordingCountdownSeconds == 3 && clamped.RecordingFrameRate == 30 && clamped.RecordingFileNamePattern == CaptureSettings.DefaultRecordingFileNamePattern, "Invalid recording choices were not reset.");
        Check(clamped.RecordHotkey.IsEmpty && clamped.MicrophoneDeviceId.Length == 0, "Unsafe record hotkey or null device was accepted.");
    }

    private static void VerifyFileNames()
    {
        Check(FileNameTemplate.BuildFileName(CaptureSettings.DefaultRecordingFileNamePattern, SampleTime, 1, ".mp4") == "Recording 2026-08-05 at 10.22.31.mp4", "Default recording file name changed.");
        Check(FileNameTemplate.BuildFileName("Demo: %counter", SampleTime, 12, ".mp4") == "Demo- 12.mp4", "Recording names are not sanitized or counted.");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine("C:\\out", "Clip.mp4") };
        Check(FileNameTemplate.ResolveUniquePath("C:\\out", "Clip.mp4", taken.Contains) == Path.Combine("C:\\out", "Clip (2).mp4"), "Recording collisions are not suffixed.");
        Check(FileNameTemplate.ResolveUniquePath("C:\\out", "Clip-camera.mp4", taken.Contains) == Path.Combine("C:\\out", "Clip-camera.mp4"), "Camera sidecar name changed.");
    }

    private static void VerifyGeometry()
    {
        Check(RecordingGeometry.EvenDown(1081) == 1080 && RecordingGeometry.EvenDown(1080) == 1080 && RecordingGeometry.EvenDown(1) == 2 && RecordingGeometry.EvenDown(3) == 2, "Even rounding is wrong.");
        var primary = new Int32Rect(0, 0, 2560, 1440);
        var left = new Int32Rect(-1920, 0, 1920, 1080);
        Check(RecordingGeometry.ComputeCrop(new Int32Rect(101, 51, 641, 361), primary) == new Int32Rect(101, 51, 640, 360), "Odd region was not shrunk to even size.");
        Check(RecordingGeometry.ComputeCrop(new Int32Rect(-1000, 100, 333, 201), left) == new Int32Rect(920, 100, 332, 200), "Negative-origin monitor crop is wrong.");
        Check(RecordingGeometry.ComputeCrop(new Int32Rect(2500, 1400, 300, 300), primary) == Int32Rect.Empty, "A sliver below the minimum was accepted.");
        Check(RecordingGeometry.ComputeCrop(new Int32Rect(2400, 1300, 300, 300), primary) == new Int32Rect(2400, 1300, 160, 140), "Region past the monitor edge was not clipped.");
        Check(RecordingGeometry.ChooseMonitor(new Int32Rect(-100, 0, 400, 300), [left, primary]) == primary, "Region spanning monitors picked the smaller overlap.");
        Check(RecordingGeometry.ChooseMonitor(new Int32Rect(5000, 5000, 10, 10), [left, primary]) is null, "Off-screen region matched a monitor.");
        Check(RecordingGeometry.ClampToFrame(new Int32Rect(100, 100, 400, 300), 300, 250) == new Int32Rect(100, 100, 200, 150), "Shrunken frames are not clamped.");
        Check(RecordingGeometry.FitFrameToOutput(1283, 721, 1282, 720) == new Int32Rect(0, 0, 1282, 720) && RecordingGeometry.FitFrameToOutput(0, 10, 100, 100).IsEmpty, "Window frames are not fitted to the output.");
        Check(RecordingGeometry.EvenSize(1365, 767) == (1364, 766), "Window even size is wrong.");
        Check(RecordingGeometry.ScreenToOverlay(-1500, 450, new Int32Rect(-1920, 0, 1920, 1080), 1.5) == new Point(280, 300), "150% DPI overlay mapping is wrong.");
        Check(RecordingGeometry.ScreenToOverlay(300, 200, new Int32Rect(0, 0, 100, 100), 1.25) == new Point(240, 160), "125% DPI overlay mapping is wrong.");
        Check(RecordingGeometry.ToVideoPixels(150, 120, new Int32Rect(100, 100, 640, 360)) == new Point(50, 20) && RecordingGeometry.ToVideoPixels(99, 120, new Int32Rect(100, 100, 640, 360)) is null, "Click to video mapping is wrong.");
        Check(RecordingGeometry.ExceedsEncoderLimits(5120, 1440) && !RecordingGeometry.ExceedsEncoderLimits(3840, 2160), "Encoder size limits are wrong.");
        Check(RecordingGeometry.SuggestBitrate(1920, 1080, 30) is >= 2_000_000 and <= 40_000_000, "Bitrate is out of range.");
        Geometry dim = RecordingGeometry.BuildDimGeometry(new Rect(0, 0, 100, 100), new Rect(25, 25, 50, 50));
        Check(!dim.FillContains(new Point(50, 50)) && dim.FillContains(new Point(5, 5)), "Dim overlay hole is wrong.");
    }

    private static void VerifyEventSidecar(string root)
    {
        Directory.CreateDirectory(root);
        var log = new RecordingEventLog();
        log.AddKey(TimeSpan.FromSeconds(2.5004), "Ctrl+Shift+K");
        log.AddClick(TimeSpan.FromSeconds(1.25), 640.4, 360.6);
        log.AddCamera(TimeSpan.FromSeconds(-0.4), "start");
        string movie = Path.Combine(root, "Recording 2026-08-05 at 10.22.31.mp4");
        string sidecar = log.WriteSidecar(movie);
        Check(Path.GetFileName(sidecar) == ".Recording 2026-08-05 at 10.22.31.mp4.bettershot-events", "Sidecar name is wrong.");
        Check(File.GetAttributes(sidecar).HasFlag(System.IO.FileAttributes.Hidden), "Sidecar is not hidden.");
        log.WriteSidecar(movie); // Rewriting a hidden file must work.
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(sidecar));
        JsonElement[] items = json.RootElement.EnumerateArray().ToArray();
        Check(items.Length == 3 && items[0].GetProperty("kind").GetString() == "camera" && items[0].GetProperty("time").GetDouble() == -0.4, "Events are not ordered by time.");
        JsonElement click = items[1];
        Check(click.GetProperty("kind").GetString() == "click" && click.GetProperty("time").GetDouble() == 1.25 && click.GetProperty("x").GetDouble() == 640 && click.GetProperty("y").GetDouble() == 361 && click.GetProperty("text").ValueKind == JsonValueKind.Null, "Click event JSON is wrong.");
        JsonElement key = items[2];
        Check(key.GetProperty("kind").GetString() == "key" && key.GetProperty("time").GetDouble() == 2.5 && key.GetProperty("text").GetString() == "Ctrl+Shift+K" && key.GetProperty("x").ValueKind == JsonValueKind.Null, "Key event JSON is wrong.");
        Check(RecordingEventLog.Deserialize(File.ReadAllText(sidecar)).Count == 3, "Sidecar does not read back.");
    }

    private static void VerifyKeystrokes()
    {
        Check(KeystrokeFormatter.Format(0x4B, control: true, alt: false, shift: true, windows: false) == "Ctrl+Shift+K", "Ctrl+Shift+K format changed.");
        Check(KeystrokeFormatter.Format(0x4B, false, false, false, false) is null && KeystrokeFormatter.Format(0x4B, false, false, true, false) is null, "Plain typing or Shift+letter would be shown.");
        Check(KeystrokeFormatter.Format(0x11, true, false, false, false) is null, "A lone modifier would be shown.");
        Check(KeystrokeFormatter.Format(0x0D, false, false, false, true) == "Win+Enter" && KeystrokeFormatter.Format(0x73, false, true, false, false) == "Alt+F4", "Special keys are formatted incorrectly.");
        Check(KeystrokeFormatter.Format(0x51, true, true, false, false, () => true) is null && KeystrokeFormatter.Format(0x37, true, true, true, false, () => true) is null, "AltGr typing (Ctrl+Alt producing a character) would be shown.");
        Check(KeystrokeFormatter.Format(0x44, true, true, false, false, () => false) == "Ctrl+Alt+D", "A Ctrl+Alt shortcut that types nothing was hidden.");
        bool askedLayout = false;
        Check(KeystrokeFormatter.Format(0x4B, true, false, true, false, () => askedLayout = true) == "Ctrl+Shift+K" && KeystrokeFormatter.Format(0x44, true, true, false, true, () => askedLayout = true) == "Win+Ctrl+Alt+D" && !askedLayout, "The AltGr check ran for a combination AltGr cannot produce.");
    }

    private static void VerifyStateMachine()
    {
        var machine = new RecordingStateMachine();
        Check(!machine.IsBusy && machine.State == RecordingState.Idle, "Initial state is wrong.");
        Check(!machine.TryMove(RecordingState.Recording) && !machine.TryMove(RecordingState.Stopping), "Idle allowed skipping straight to recording.");
        foreach (RecordingState step in new[] { RecordingState.Selecting, RecordingState.Countdown, RecordingState.Starting, RecordingState.Recording, RecordingState.Paused, RecordingState.Recording, RecordingState.Stopping, RecordingState.Completed })
            Check(machine.TryMove(step), $"Happy path refused {step}.");
        Check(!machine.IsBusy && !machine.TryMove(RecordingState.Stopping), "Completed recording could be stopped again.");
        Check(machine.TryMove(RecordingState.Selecting) && machine.IsBusy && machine.TryMove(RecordingState.Canceled), "Selection cancel path failed.");
        Check(machine.TryMove(RecordingState.Selecting) && machine.TryMove(RecordingState.Countdown) && machine.TryMove(RecordingState.Canceled), "Countdown cancel path failed.");
        Check(machine.TryMove(RecordingState.Starting) && machine.TryMove(RecordingState.Failed) && !machine.IsCapturing, "Start failure path failed.");
        Check(machine.TryMove(RecordingState.Starting) && machine.TryMove(RecordingState.Recording) && machine.IsCapturing && !machine.TryMove(RecordingState.Completed), "Recording could complete without stopping.");
        Check(machine.TryMove(RecordingState.Paused) && machine.TryMove(RecordingState.Stopping) && !machine.TryMove(RecordingState.Paused) && machine.TryMove(RecordingState.Failed), "Pause/stop/finish failure path failed.");
        int changes = 0;
        machine.Changed += (_, _) => changes++;
        machine.TryMove(RecordingState.Idle);
        machine.TryMove(RecordingState.Paused);
        Check(changes == 1, "Changed fired for a refused transition.");
    }

    private static void VerifyClock()
    {
        TimeSpan now = TimeSpan.FromSeconds(100);
        var clock = new RecordingClock(() => now);
        Check(clock.Elapsed == TimeSpan.Zero && !clock.Pause(), "Unstarted clock moved or paused.");
        clock.Start();
        now += TimeSpan.FromSeconds(2);
        Check(clock.Elapsed == TimeSpan.FromSeconds(2), "Clock did not advance.");
        Check(clock.Pause() && !clock.Pause(), "Pause is not idempotent.");
        now += TimeSpan.FromSeconds(5);
        Check(clock.Elapsed == TimeSpan.FromSeconds(2) && clock.IsPaused, "Paused time was counted.");
        Check(clock.Resume() && !clock.Resume(), "Resume is not idempotent.");
        now += TimeSpan.FromSeconds(1);
        Check(clock.Elapsed == TimeSpan.FromSeconds(3), "Clock lost time after resume.");
        clock.Pause();
        now += TimeSpan.FromSeconds(1);
        clock.Stop();
        now += TimeSpan.FromSeconds(10);
        Check(clock.Elapsed == TimeSpan.FromSeconds(3) && clock.IsStopped && !clock.Resume(), "Stopping while paused did not freeze at the pause.");
    }

    private static void VerifyAudio()
    {
        byte[] int16Mono = [0x00, 0x40, 0x00, 0xC0]; // +0.5, -0.5
        float[] stereo = new float[8];
        Check(PcmConverter.ToStereoFloat(int16Mono, PcmSampleKind.Int16, 1, stereo) == 2 && stereo[0] == 0.5f && stereo[1] == 0.5f && stereo[2] == -0.5f && stereo[3] == -0.5f, "Mono 16-bit did not become stereo.");
        byte[] int24 = [0x00, 0x00, 0x80, 0xFF, 0xFF, 0x7F]; // L = -1, R ≈ +1
        Check(PcmConverter.ToStereoFloat(int24, PcmSampleKind.Int24, 2, stereo) == 1 && stereo[0] == -1f && stereo[1] > 0.9999f, "24-bit sign extension is wrong.");
        byte[] float6 = new byte[24];
        for (int channel = 0; channel < 6; channel++) BitConverter.GetBytes(channel / 10f).CopyTo(float6, channel * 4);
        Check(PcmConverter.ToStereoFloat(float6, PcmSampleKind.Float32, 6, stereo) == 1 && stereo[0] == 0f && Math.Abs(stereo[1] - 0.1f) < 1e-6, "Multichannel float did not keep front left/right.");
        byte[] pcm = new byte[4];
        PcmConverter.ToInt16([2f, -2f], pcm);
        Check(BitConverter.ToInt16(pcm, 0) == 32767 && BitConverter.ToInt16(pcm, 2) == -32767, "Mixed samples are not clamped.");

        var resampler = new LinearResampler(44_100, 48_000);
        float[] input = Enumerable.Repeat(0.25f, 4410 * 2).ToArray();
        float[] resampled = new float[resampler.MaxOutputFrames(4410) * 4];
        int frames = resampler.Process(input, 4410, resampled);
        frames += resampler.Process(input, 4410, resampled.AsSpan(frames * 2));
        Check(Math.Abs(frames - 9600) <= 2, $"44.1 kHz to 48 kHz produced {frames} frames instead of about 9600.");
        Check(resampled.Take(frames * 2).All(sample => Math.Abs(sample - 0.25f) < 1e-5), "Resampling changed a constant signal.");
        Check(new LinearResampler(48_000, 48_000).IsPassthrough, "Same-rate audio is resampled.");

        var ring = new FloatRingBuffer(8);
        ring.Write([1, 2, 3, 4, 5, 6]);
        ring.Write([7, 8, 9, 10]);
        float[] read = new float[8];
        Check(ring.Read(read) == 8 && read[0] == 3 && read[7] == 10, "Ring buffer did not drop the oldest samples.");

        TimeSpan now = TimeSpan.Zero;
        var clock = new RecordingClock(() => now);
        using var mixer = new AudioMixer([], clock);
        clock.Start();
        now = TimeSpan.FromSeconds(1);
        mixer.PumpForTest();
        Check(mixer.TryRead(960, 4800) is (byte[] first, 0) && first.Length == 4800 * AudioMixer.BytesPerFrame && first.All(value => value == 0), "Silence was not generated for the timeline.");
        clock.Pause();
        mixer.PumpForTest();
        now = TimeSpan.FromSeconds(5);
        mixer.PumpForTest();
        long total = 4800;
        while (mixer.TryRead(1, 48_000) is (byte[] chunk, _)) total += chunk.Length / AudioMixer.BytesPerFrame;
        Check(total == 48_000, $"Pause flush produced {total} frames instead of exactly one second.");
        clock.Resume();
        now = TimeSpan.FromSeconds(5.5);
        clock.Stop();
        mixer.Finish();
        long after = 0;
        while (mixer.TryRead(960, 48_000) is (byte[] chunk, _)) after += chunk.Length / AudioMixer.BytesPerFrame;
        Check(after == 24_000 && mixer.IsDrained, $"Finish produced {after} frames instead of half a second, or did not drain.");
    }

    private static void VerifyLibrary(string root)
    {
        Directory.CreateDirectory(root);
        string movie = Path.Combine(root, "kept.mp4"), gone = Path.Combine(root, "gone.mp4");
        File.WriteAllBytes(movie, [0]);
        File.WriteAllBytes(gone, [0]);
        var store = new RecordingLibraryStore(root);
        Check(store.Load().Count == 0 && store.FindInterruptedRecordings().Count == 0, "Empty library is not empty.");
        var older = new RecordingEntry(Guid.NewGuid(), gone, null, DateTime.UtcNow.AddMinutes(-5), 3.2, 640, 360);
        var newer = new RecordingEntry(Guid.NewGuid(), movie, Path.Combine(root, "kept-camera.mp4"), DateTime.UtcNow, 12.5, 1920, 1080);
        store.Add(older);
        store.Add(newer);
        File.Delete(gone);
        IReadOnlyList<RecordingEntry> loaded = new RecordingLibraryStore(root).Load();
        Check(loaded.Count == 1 && loaded[0] == newer, "Library did not round-trip or prune a missing movie.");
        Directory.CreateDirectory(store.InProgressFolder);
        File.WriteAllBytes(Path.Combine(store.InProgressFolder, "abc.mp4"), [0]);
        Check(store.FindInterruptedRecordings().Count == 1, "Interrupted recordings are not detected.");
        var reopened = new RecordingLibraryStore(root);
        reopened.Load();
        reopened.Remove(newer.Id);
        Check(new RecordingLibraryStore(root).Load().Count == 0, "Removing a recording did not persist.");
    }

    private static void VerifySetupLayout()
    {
        var primaryWork = new Int32Rect(0, 0, 2560, 1392);
        var leftWork = new Int32Rect(-1920, -200, 1920, 1040);
        const int width = 534, height = 900, gap = 24, label = 44;
        Check(RecordingSetupLayout.PlaceCard(new Int32Rect(200, 100, 2000, 1200), primaryWork, width, height, gap, label) == new Int32Rect(933, 250, width, height), "Card is not centered inside a large area.");
        Check(RecordingSetupLayout.PlaceCard(new Int32Rect(0, 0, 2560, 1440), primaryWork, width, height, gap, 0) == new Int32Rect(1013, 246, width, height), "Card is not centered on the work area for a full-screen area.");
        Check(RecordingSetupLayout.PlaceCard(new Int32Rect(1000, 40, 400, 200), primaryWork, width, height, gap, label) == new Int32Rect(933, 308, width, height), "Card is not placed below a small area, clear of the label.");
        Check(RecordingSetupLayout.PlaceCard(new Int32Rect(1000, 1100, 400, 200), primaryWork, width, height, gap, label) == new Int32Rect(933, 176, width, height), "Card is not placed above a small area near the bottom.");
        Check(RecordingSetupLayout.PlaceCard(new Int32Rect(100, 300, 300, 700), primaryWork, width, height, gap, label) == new Int32Rect(424, 200, width, height), "Card is not placed right of a tall narrow area.");
        Check(RecordingSetupLayout.PlaceCard(new Int32Rect(2200, 300, 300, 700), primaryWork, width, height, gap, label) == new Int32Rect(1642, 200, width, height), "Card is not placed left of a tall area near the right edge.");
        Check(RecordingSetupLayout.PlaceCard(new Int32Rect(-1920, -200, 200, 120), leftWork, width, height, gap, label) == new Int32Rect(-1696, -200, width, height), "Card near a negative-origin corner is not kept on that monitor.");
        Int32Rect cramped = RecordingSetupLayout.PlaceCard(new Int32Rect(-1920, -200, 1920, 1080), leftWork with { Height = 1080 }, width, 1200, gap, 0);
        Check(cramped.X == -1227 && cramped.Y == -200, "An oversized card is not centered and clamped to the top of its monitor.");

        var monitor = new Int32Rect(-1920, -200, 1920, 1080);
        Check(RecordingSetupLayout.PlaceDimensionLabel(new Int32Rect(-1500, 0, 600, 300), monitor, 120, 30, 8) == new Int32Rect(-1260, 308, 120, 30), "Dimension label is not centered below the area.");
        Check(RecordingSetupLayout.PlaceDimensionLabel(monitor, monitor, 120, 30, 8) == new Int32Rect(-1020, 842, 120, 30), "Dimension label is not inside a full-screen area.");
        Check(RecordingSetupLayout.LabelReserve(new Int32Rect(-1500, 0, 600, 300), monitor, 30, 8) == 38 && RecordingSetupLayout.LabelReserve(monitor, monitor, 30, 8) == 0, "Label reserve is wrong.");
        Check(RecordingSetupLayout.Dimensions(2960, 1840) == "2960 × 1840", "Dimension text changed.");
        Check(RecordingSetupLayout.AbbreviateFolder("C:\\Users\\Ada\\Pictures\\BetterShot", "C:\\Users\\Ada") == "~\\Pictures\\BetterShot", "Save folder is not abbreviated with ~.");
        Check(RecordingSetupLayout.AbbreviateFolder("C:\\Users\\Adam\\Pictures", "C:\\Users\\Ada") == "C:\\Users\\Adam\\Pictures" && RecordingSetupLayout.AbbreviateFolder("D:\\Clips", "C:\\Users\\Ada") == "D:\\Clips", "Unrelated folders were abbreviated.");
    }

    private static void VerifyControlsBar()
    {
        Check(RecordingControlsWindow.FormatElapsed(TimeSpan.FromMilliseconds(860)) == "00:00.8" && RecordingControlsWindow.FormatElapsed(TimeSpan.FromSeconds(754.29)) == "12:34.2", "Elapsed time format changed.");
        Check(RecordingControlsWindow.FormatElapsed(TimeSpan.FromSeconds(3725.5)) == "1:02:05.5" && RecordingControlsWindow.FormatElapsed(TimeSpan.FromSeconds(-1)) == "00:00.0", "Long or negative elapsed time format is wrong.");
        Check(RecordingControlsWindow.TopPlacement(new Int32Rect(-1920, -200, 1920, 1040), 600, 90, 18, 15) == new Int32Rect(-1260, -203, 600, 90), "Controls are not centered at the top of a negative-origin display.");
        Check(MicrophoneLevelMeter.LitBars(0, 5) == 0 && MicrophoneLevelMeter.LitBars(1, 5) == 5 && MicrophoneLevelMeter.LitBars(0.005f, 5) == 1 && MicrophoneLevelMeter.LitBars(0.05f, 5) == 3, "Microphone level bars are wrong.");
    }

    /// <summary>Setup card: settings round-trip through a fixture store, Enter/Esc/Area behavior, and 100%/150% renders.</summary>
    private static void VerifySetupPanel(string root, string directory)
    {
        var store = new CaptureHistoryStore(root);
        RecordingDevice[] microphones = [new("{mic-1}", "Studio USB Microphone"), new("{mic-2}", "Headset Microphone (Wireless)")];
        RecordingDevice[] cameras = [new("\\\\?\\USB#cam-1", "Integrated Webcam"), new("\\\\?\\USB#cam-2", "Desk Camera 4K")];
        var info = new RecordingSetupInfo("Area", RecordingSetupLayout.Dimensions(2960, 1840), Controls.LucideIconKind.SquareDashed, microphones, "Studio USB Microphone", cameras, "~\\Pictures\\BetterShot");
        var card = new RecordingSetupCard(info, store.Settings, change => { store.UpdateSettings(change); return store.Settings; });
        var choices = new List<RecordingSetupChoice>();
        card.Chosen += (_, choice) => choices.Add(choice);

        Check(card.MicrophoneToggle.IsChecked == false && card.CountdownThree.IsChecked == true && card.CameraIcon.Icon == Controls.LucideIconKind.CameraOff, "Setup card did not show the default settings.");
        Check((string)card.MicrophoneDeviceButton.Content == "Studio USB Microphone" && (string)card.CameraDeviceButton.Content == "Integrated Webcam", "Default device names are wrong.");
        card.Flip(card.MicrophoneToggle);
        card.Flip(card.SystemAudioToggle);
        card.Flip(card.ClicksToggle);
        card.Flip(card.CameraToggle);
        card.SelectCountdown(5);
        card.SelectMicrophone("{mic-2}");
        card.SelectCamera("\\\\?\\USB#cam-2");
        CaptureSettings saved = new CaptureHistoryStore(root).Settings;
        Check(saved.RecordMicrophone && saved.RecordSystemAudio && saved.HighlightClicks && saved.RecordCamera && !saved.ShowKeystrokes, "Setup toggles did not round-trip through settings.json.");
        Check(saved.RecordingCountdownSeconds == 5 && saved.MicrophoneDeviceId == "{mic-2}" && saved.CameraDeviceId == "\\\\?\\USB#cam-2", "Setup countdown or devices did not round-trip.");
        Check(card.CameraIcon.Icon == Controls.LucideIconKind.Camera && (string)card.MicrophoneDeviceButton.Content == "Headset Microphone (Wireless)" && card.CountdownFive.IsChecked == true, "Setup card did not reflect the saved choices.");
        card.Flip(card.CameraToggle);
        Check(!new CaptureHistoryStore(root).Settings.RecordCamera && card.CameraIcon.Icon == Controls.LucideIconKind.CameraOff, "Turning the camera off did not save.");
        card.SelectMicrophone("{unplugged}");
        Check((string)card.MicrophoneDeviceButton.Content == "Saved device (not connected)", "A missing microphone is not labeled.");

        Check(!card.HandleKey(Key.Tab) && choices.Count == 0, "Tab was swallowed or chose an action.");
        Check(card.HandleKey(Key.Escape) && choices.SequenceEqual([RecordingSetupChoice.Cancel]), "Esc did not cancel.");
        Check(card.HandleKey(Key.Enter) && choices.SequenceEqual([RecordingSetupChoice.Cancel, RecordingSetupChoice.Start]), "Enter did not start.");
        card.AreaButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, card.AreaButton));
        Check(choices[^1] == RecordingSetupChoice.Reselect, "The Area row did not ask to pick the area again.");

        CaptureSettings shown = CaptureSettings.Default with { RecordMicrophone = true, RecordSystemAudio = true, HighlightClicks = true, RecordingCountdownSeconds = 3 };
        var reference = new RecordingSetupCard(info, shown, change => change(shown));
        SaveElement(reference, Path.Combine(directory, "recording-setup-100.png"), 1.0);
        SaveElement(reference, Path.Combine(directory, "recording-setup-150.png"), 1.5);
        var noDevices = new RecordingSetupCard(info with { AreaTitle = "Window — A very long window title that should be trimmed neatly", AreaIcon = Controls.LucideIconKind.AppWindow, Microphones = [], DefaultMicrophoneName = string.Empty, Cameras = [] },
            CaptureSettings.Default, change => change(CaptureSettings.Default));
        Check((string)noDevices.CameraDeviceButton.Content == "No camera found" && !noDevices.CameraDeviceButton.IsEnabled && (string)noDevices.MicrophoneDeviceButton.Content == "Default microphone", "Device fallbacks are wrong.");
        SaveElement(noDevices, Path.Combine(directory, "recording-setup-no-devices.png"), 1.0);
        SaveElement(RecordingSetupBackdrop.CreateDimensionLabel(RecordingSetupLayout.Dimensions(2960, 1840)), Path.Combine(directory, "recording-setup-label-150.png"), 1.5);

        var controls = new RecordingControlsWindow(() => TimeSpan.FromMilliseconds(800), showMicrophone: true, showCamera: false, () => 0.2f);
        var pill = (FrameworkElement)controls.Content;
        controls.Content = null;
        SaveElement(pill, Path.Combine(directory, "recording-controls-100.png"), 1.0);
        SaveElement(pill, Path.Combine(directory, "recording-controls-150.png"), 1.5);
        controls.Close();
    }

    /// <summary>Renders an unparented element on a neutral backdrop at the given DPI scale (1.0 = 100%, 1.5 = 150%).</summary>
    private static void SaveElement(FrameworkElement element, string path, double scale)
    {
        var surface = new System.Windows.Controls.Border { Background = new SolidColorBrush(Color.FromRgb(0x30, 0x34, 0x3C)), Padding = new Thickness(24), Child = element };
        try
        {
            surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            surface.Arrange(new Rect(surface.DesiredSize));
            surface.UpdateLayout();
            int width = (int)Math.Ceiling(surface.ActualWidth * scale), height = (int)Math.Ceiling(surface.ActualHeight * scale);
            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally { surface.Child = null; }
    }

    private static async Task VerifyUiAsync(string directory)
    {
        var shell = new MainWindow();
        try
        {
            shell.Show();
            // Record lives in the library's + capture menu.
            shell.SetCaptureMenuOpenForTest(true);
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(shell.RecordButton.IsVisible && shell.RecordModeButton.IsVisible, "Record button is not visible.");
            shell.SetCaptureMenuOpenForTest(false);
            shell.OpenSettingsForTest();
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            shell.UpdateLayout();
            Check(shell.RecordingSettingsSection is not null && shell.RecorderFor(HotkeyAction.Record) == shell.RecordHotkeyRecorder, "Recording settings are not wired.");
            Check(shell.RecordingStateForTest == RecordingState.Idle && shell.RecordingsPanel.Visibility == Visibility.Collapsed, "Recording UI did not start idle.");
            CaptureSettings synced = shell.SaveFromRecordingSetupForTest(settings => settings with { RecordMicrophone = true, ShowKeystrokes = true, RecordingCountdownSeconds = 0 });
            Check(synced.RecordMicrophone && synced.ShowKeystrokes && synced.RecordingCountdownSeconds == 0, "Setup panel changes were not saved.");
            Check(shell.RecordMicrophoneCheckBox.IsChecked == true && shell.ShowKeystrokesCheckBox.IsChecked == true && (shell.RecordingCountdownComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string == "0", "Settings > Recording did not follow the setup panel.");
            shell.SaveFromRecordingSetupForTest(settings => settings with { RecordMicrophone = false, ShowKeystrokes = false, RecordingCountdownSeconds = 3 });
            shell.RecordingSettingsSection!.BringIntoView();
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveWindow(shell, Path.Combine(directory, "recording-settings.png"));
        }
        finally { shell.CloseForTest(); }
    }

    /// <summary>Records the primary display for two seconds and verifies the MP4 with Windows' media property reader.</summary>
    private static async Task<string> VerifyLiveRecordingAsync(string root)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !ScreenRecorder.IsSupported)
            return "SKIPPED live recording (Windows graphics capture unavailable)";
        Directory.CreateDirectory(root);
        var monitors = Interop.NativeMethods.GetMonitorRects();
        if (monitors.Count == 0) return "SKIPPED live recording (no display)";
        var first = monitors[0];
        RecordingTarget target = RecordingTargetResolver.Resolve(CaptureMode.Display, new Int32Rect(first.Left, first.Top, first.Width, first.Height));
        string movie = Path.Combine(root, "live.mp4");
        var events = new RecordingEventLog();
        var options = new RecordingSessionOptions(30, Microphone: false, MicrophoneId: string.Empty, SystemAudio: false, Camera: false, CameraId: string.Empty);
        ScreenRecorder recorder = await ScreenRecorder.StartAsync(target, options, movie, null, events);
        await Task.Delay(TimeSpan.FromSeconds(2));
        RecordingOutcome outcome = await recorder.StopAsync();
        Check(File.Exists(movie) && new FileInfo(movie).Length > 1024, "Live recording produced no MP4.");
        Check(outcome.Frames >= 10 && outcome.Width % 2 == 0 && outcome.Height % 2 == 0, $"Live recording wrote {outcome.Frames} frames at {outcome.Width}×{outcome.Height}.");
        StorageFile file = await StorageFile.GetFileFromPathAsync(movie);
        VideoProperties video = await file.Properties.GetVideoPropertiesAsync();
        Check(video.Duration >= TimeSpan.FromSeconds(1) && video.Width == outcome.Width && video.Height == outcome.Height,
            $"MP4 reports {video.Duration.TotalSeconds:0.00}s at {video.Width}×{video.Height}; expected about 2s at {outcome.Width}×{outcome.Height}.");
        return $"live 2 s display recording ({outcome.Width}×{outcome.Height}, {outcome.Frames} frames, {video.Duration.TotalSeconds:0.0}s playable MP4)";
    }

    private static void SaveWindow(Window window, string path)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
