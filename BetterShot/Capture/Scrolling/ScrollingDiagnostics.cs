namespace BetterShot.Capture.Scrolling;

/// <summary>
/// Synthetic-frame checks for <see cref="ScrollStitcher"/>: overlap detection with a sticky header and footer, uneven
/// scroll steps, a blank stretch, end-of-content and no-scroll detection, lost overlap, a blinking caret, and the
/// maximum height. Pure in-memory; never touches the screen.
/// </summary>
internal static class ScrollingDiagnostics
{
    private const int Width = 160;
    private const int ViewportHeight = 400;
    private const int HeaderRows = 40;
    private const int FooterRows = 20;
    private const int PageHeight = 3000;
    private const int ContentRows = ViewportHeight - HeaderRows - FooterRows;

    internal static string Run()
    {
        byte[] page = CreatePage(PageHeight);
        VerifyStitchWithStickyBands(page);
        VerifyNoScroll(page);
        VerifyLostOverlap(page);
        VerifyCaretBlinkIsNoChange(page);
        VerifyMaxHeight(page);
        VerifyComposeWithoutScrolling(page);
        return "scroll stitching with sticky header/footer, uneven steps, blank stretch, end-of-content, no-scroll, lost overlap, caret blink, and max height";
    }

    private static void VerifyStitchWithStickyBands(byte[] page)
    {
        var stitcher = new ScrollStitcher(Width, ViewportHeight);
        int[] steps = [120, 95, 150, 133, 101, 149, 88, 140];
        int scroll = 0, stepIndex = 0;
        Check(stitcher.AddFrame(Viewport(page, scroll)).Status == StitchStatus.Appended, "The first frame was rejected.");
        int unchanged = 0;
        while (unchanged < 2)
        {
            int next = Math.Min(PageHeight - ContentRows, scroll + steps[stepIndex++ % steps.Length]);
            StitchStepResult result = stitcher.AddFrame(Viewport(page, next));
            if (next == scroll)
            {
                Check(result.Status == StitchStatus.NoChange, $"An unchanged frame at the end was {result.Status}.");
                unchanged++;
                continue;
            }
            Check(result.Status == StitchStatus.Appended, $"Scrolling from {scroll} to {next} was {result.Status}.");
            Check(result.Shift == next - scroll, $"Detected shift {result.Shift}, expected {next - scroll}.");
            scroll = next;
        }
        Check(stitcher.FooterRows == FooterRows, $"Detected a {stitcher.FooterRows}-row footer, expected {FooterRows}.");
        (byte[] pixels, int width, int height) = stitcher.Compose();
        int expectedHeight = HeaderRows + PageHeight + FooterRows;
        Check(width == Width && height == expectedHeight, $"Stitched {width}×{height}, expected {Width}×{expectedHeight}.");
        byte[] expected = Expected(page);
        Check(pixels.AsSpan().SequenceEqual(expected), "The stitched image does not match the source page (header once at the top, footer once at the bottom).");
    }

    private static void VerifyNoScroll(byte[] page)
    {
        var stitcher = new ScrollStitcher(Width, ViewportHeight);
        byte[] frame = Viewport(page, 500);
        stitcher.AddFrame(frame);
        Check(stitcher.AddFrame(frame).Status == StitchStatus.NoChange && stitcher.AddFrame(frame).Status == StitchStatus.NoChange, "An unchanged frame was not reported as no change.");
        Check(stitcher.TotalAppendedRows == 0, "Rows were appended although nothing scrolled.");
    }

    private static void VerifyLostOverlap(byte[] page)
    {
        var stitcher = new ScrollStitcher(Width, ViewportHeight);
        stitcher.AddFrame(Viewport(page, 0));
        // Jumping further than the visible content leaves no overlap to match.
        Check(stitcher.AddFrame(Viewport(page, 1500)).Status == StitchStatus.NoMatch, "A frame without overlap was stitched.");
        Check(stitcher.TotalAppendedRows == 0, "A failed match appended rows.");
    }

    private static void VerifyCaretBlinkIsNoChange(byte[] page)
    {
        var stitcher = new ScrollStitcher(Width, ViewportHeight);
        byte[] frame = Viewport(page, 300);
        stitcher.AddFrame(frame);
        byte[] blinked = (byte[])frame.Clone();
        for (int row = 200; row < 216; row++)
        for (int column = 60; column < 62; column++)
        {
            int offset = (row * Width + column) * 4;
            blinked[offset] = blinked[offset + 1] = blinked[offset + 2] = (byte)(255 - blinked[offset]);
        }
        Check(stitcher.AddFrame(blinked).Status == StitchStatus.NoChange, "A blinking caret was treated as scrolling.");
    }

    private static void VerifyMaxHeight(byte[] page)
    {
        const int maxHeight = 1000;
        var stitcher = new ScrollStitcher(Width, ViewportHeight, maxHeight);
        stitcher.AddFrame(Viewport(page, 0));
        StitchStatus last = StitchStatus.Appended;
        for (int scroll = 120; scroll <= PageHeight - ContentRows && last == StitchStatus.Appended; scroll += 120)
            last = stitcher.AddFrame(Viewport(page, scroll)).Status;
        Check(last == StitchStatus.ReachedMaxHeight && stitcher.IsFull, "The maximum height did not stop the capture.");
        Check(stitcher.ResultHeight == maxHeight, $"Result height {stitcher.ResultHeight} exceeds the {maxHeight} px limit.");
        Check(stitcher.AddFrame(Viewport(page, 1200)).Status == StitchStatus.ReachedMaxHeight, "A full stitcher accepted more frames.");
        Check(stitcher.Compose().Height == maxHeight, "The composed image does not respect the maximum height.");
    }

    private static void VerifyComposeWithoutScrolling(byte[] page)
    {
        var stitcher = new ScrollStitcher(Width, ViewportHeight);
        byte[] frame = Viewport(page, 0);
        stitcher.AddFrame(frame);
        (byte[] pixels, _, int height) = stitcher.Compose();
        Check(height == ViewportHeight && pixels.AsSpan().SequenceEqual(frame), "A single frame did not compose to itself.");
    }

    /// <summary>A page with unique textured rows and a 200-row blank stretch, so only correct shifts match exactly.</summary>
    private static byte[] CreatePage(int height)
    {
        var pixels = new byte[Width * height * 4];
        uint state = 2463534242;
        for (int row = 0; row < height; row++)
        {
            bool blank = row is >= 1200 and < 1400;
            for (int column = 0; column < Width; column++)
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                int offset = (row * Width + column) * 4;
                byte value = blank ? (byte)245 : (byte)(40 + (state % 180));
                pixels[offset] = value;
                pixels[offset + 1] = (byte)(blank ? 245 : (value * 3 + row) % 256);
                pixels[offset + 2] = (byte)(blank ? 245 : (column * 5 + row * 7) % 256);
                pixels[offset + 3] = 255;
            }
        }
        return pixels;
    }

    private static byte[] Viewport(byte[] page, int scroll)
    {
        var frame = new byte[Width * ViewportHeight * 4];
        FillBand(frame, 0, HeaderRows, 30, 60, 200);
        Buffer.BlockCopy(page, scroll * Width * 4, frame, HeaderRows * Width * 4, ContentRows * Width * 4);
        FillBand(frame, ViewportHeight - FooterRows, FooterRows, 200, 200, 40);
        return frame;
    }

    private static byte[] Expected(byte[] page)
    {
        int height = HeaderRows + PageHeight + FooterRows;
        var expected = new byte[Width * height * 4];
        FillBand(expected, 0, HeaderRows, 30, 60, 200);
        Buffer.BlockCopy(page, 0, expected, HeaderRows * Width * 4, PageHeight * Width * 4);
        FillBand(expected, height - FooterRows, FooterRows, 200, 200, 40);
        return expected;
    }

    /// <summary>A sticky band: a fixed pattern (with a label-like stripe) that never moves.</summary>
    private static void FillBand(byte[] frame, int top, int rows, byte blue, byte green, byte red)
    {
        for (int row = top; row < top + rows; row++)
        for (int column = 0; column < Width; column++)
        {
            int offset = (row * Width + column) * 4;
            bool stripe = (row - top) % 9 < 2 && column % 13 < 8;
            frame[offset] = stripe ? (byte)255 : blue;
            frame[offset + 1] = stripe ? (byte)255 : green;
            frame[offset + 2] = stripe ? (byte)255 : red;
            frame[offset + 3] = 255;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Scrolling capture: " + message);
    }
}
