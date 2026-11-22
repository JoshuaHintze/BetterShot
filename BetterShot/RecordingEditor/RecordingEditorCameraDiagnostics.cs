using BetterShot.Recording;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterShot.RecordingEditor;

internal static partial class RecordingEditorDiagnostics
{
    private static async Task VerifyCameraExportsAsync(string movie, RecordingFrameReader screen, EditTimeline cut, string root)
    {
        string cameraPath = Path.Combine(root, "synthetic-camera.mp4");
        // The blue side bars reveal a stretched or uncropped webcam. The center changes
        // color after one second so cuts, speed changes and camera offsets are observable.
        DecodedFrame CameraFrame(int index)
        {
            const int width = 160, height = 80;
            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int p = 4 * (y * width + x);
                    pixels[p] = 245;
                    if (x >= 40 && x < 120) pixels[p + (index < 30 ? 2 : 1)] = 245;
                    if (x is >= 57 and < 73 && y is >= 18 and < 34)
                        pixels[p] = pixels[p + 1] = pixels[p + 2] = (byte)(index % 2 == 0 ? 240 : 16);
                    pixels[p + 3] = 255;
                }
            return new DecodedFrame(width, height, pixels);
        }
        await FrameMovieEncoder.EncodeAsync(cameraPath, 160, 80, 30, 63, (i, _) => Task.FromResult(CameraFrame(i)), null, null, CancellationToken.None);
        RecordingEvent[] events = [new(-0.10, RecordingEvent.CameraKind, null, null, "start"),
            new(0.65, RecordingEvent.CameraKind, null, null, "off"), new(1.15, RecordingEvent.CameraKind, null, null, "on")];
        CameraSource source = CameraSourceResolver.Resolve(movie, null, events);
        Check(source.Path == cameraPath && Math.Abs(source.CameraTimeAt(0.4) - 0.5) < .001, "Legacy webcam file or start offset was lost.");
        var circle = new RecordingEditState(cut, [], false).WithCameraPlacement(new CameraPlacement(.36, .17, .38));
        var square = circle.WithCameraShape(CameraShape.Square);
        var hidden = square.WithCameraVisibility(false);
        var history = new RecordingEditHistory(circle);
        Check(history.Apply(_ => square) && history.Apply(_ => hidden) && history.Undo() && history.Current.Camera == square.Camera, "Undo lost the camera placement or crop.");
        string json = RecordingEditProject.Serialize(hidden, new RecordingEditViewOptions(), null, [], null);
        Check(RecordingEditProject.Deserialize(json, cut.Duration).State.Camera == hidden.Camera, "Camera layout did not survive save and reopen.");
        Rect bounds = circle.Camera.Placement.ToPixels(screen.Info.Width, screen.Info.Height);
        int centerX = (int)(bounds.X + bounds.Width / 2), centerY = (int)(bounds.Y + bounds.Height / 2);
        int cornerX = (int)bounds.X + 5, cornerY = (int)bounds.Y + 5;
        bool Pink(DecodedFrame frame, int x, int y) { var p = Pixel(frame, x, y); return p.red > 180 && p.blue > 180 && p.green < 100; }
        bool Cyan(DecodedFrame frame, int x, int y) { var p = Pixel(frame, x, y); return p.red < 100 && p.blue > 175 && p.green > 175; }

        string mp4 = Path.Combine(root, "pip-square.mp4");
        var job = new RecordingExportJob(RecordingExportKind.Mp4, movie, mp4, square, screen.Info, 12, 0, 2, root,
            [AudioTrackKind.Microphone], Camera: source);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var progress = new Progress<double>();
        await RecordingExporter.ExportAsync(job, screen, dispatcher, progress, CancellationToken.None);
        var exported = await RecordingFrameReader.OpenAsync(mp4);
        Check(exported.Info.HasAudio, "Adding a silent webcam dropped the screen recording's selected microphone audio.");
        Task<DecodedFrame> Read(RecordingFrameReader reader, double t) => reader.ReadAsync(t, screen.Info.Width, screen.Info.Height, CancellationToken.None);
        // The camera patch alternates every source frame. A thumbnail-based compositor can
        // pass isolated timestamp checks while repeating an entire group of frames during playback.
        int patchX = (int)(bounds.Left + bounds.Width * .31), patchY = (int)(bounds.Top + bounds.Height * .32);
        double[] stamps = Enumerable.Range(1, 12).Select(i => (i + .15) / 30).ToArray();
        var motion = await exported.ReadAsync(stamps, screen.Info.Width, screen.Info.Height, CancellationToken.None);
        int changes = 0;
        for (int i = 1; i < motion.Count; i++)
            if (Math.Abs(Pixel(motion[i], patchX, patchY).red - Pixel(motion[i - 1], patchX, patchY).red) > 100) changes++;
        Check(changes >= 10, $"The camera patch advanced on only {changes} of 11 consecutive MP4 frames.");
        var early = await Read(exported, .25);
        Check(Pink(early, centerX, centerY) && Pink(early, cornerX, cornerY), "Square PiP failed its position/crop check.");
        Check(!Pink(early, (int)bounds.Left - 7, centerY), "Square PiP escaped its bounds.");
        var off = await Read(exported, .55); // Source 1.05 after cutting 0.5..1.
        Check(!Cyan(off, centerX, centerY) && !Pink(off, centerX, centerY), "Recorded camera-off interval survived in MP4.");
        Check(Cyan(await Read(exported, .80), centerX, centerY), "Post-cut camera frames are out of sync.");

        string gif = Path.Combine(root, "pip-circle.gif");
        await RecordingExporter.ExportAsync(job with { Kind = RecordingExportKind.Gif, OutputPath = gif, State = circle }, screen, dispatcher, progress, CancellationToken.None);
        var gifDecoder = new GifBitmapDecoder(new MemoryStream(File.ReadAllBytes(gif)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var gifFrame = DecodedFrame.FromBitmapSource(gifDecoder.Frames[2]);
        Check(Pink(gifFrame, centerX, centerY) && !Pink(gifFrame, cornerX, cornerY), "GIF did not match the circular crop.");

        string lapse = Path.Combine(root, "pip-timelapse.mp4");
        await RecordingExporter.ExportAsync(job with { Kind = RecordingExportKind.Timelapse, OutputPath = lapse, State = circle }, screen, dispatcher, progress, CancellationToken.None);
        var fast = await RecordingFrameReader.OpenAsync(lapse);
        Check(Cyan(await Read(fast, .48), centerX, centerY), "Timelapse used the wrong camera time after a cut.");

        string noCamera = Path.Combine(root, "pip-hidden.mp4");
        await RecordingExporter.ExportAsync(job with { OutputPath = noCamera, State = hidden }, screen, dispatcher, progress, CancellationToken.None);
        var hiddenFrame = await Read(await RecordingFrameReader.OpenAsync(noCamera), .25);
        Check(!Pink(hiddenFrame, centerX, centerY), "Hiding the camera left it visible in MP4.");
    }
}
