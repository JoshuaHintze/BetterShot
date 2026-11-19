using System.IO;

namespace BetterShot.Presentation;

/// <summary>
/// Pure placement and text rules for the "New recording" setup panel, in virtual-screen physical pixels so they work on
/// every monitor arrangement (negative origins included) and at any DPI.
/// </summary>
internal static class RecordingSetupLayout
{
    /// <summary>
    /// Where the card goes: centered over the area when it fits inside it (full-screen areas get a centered card);
    /// otherwise below, above, right of, or left of the area; otherwise centered on the area. Always kept fully on the
    /// work area of the monitor that holds the area. <paramref name="labelReserve"/> keeps the dimension label visible
    /// when the card sits below the area.
    /// </summary>
    internal static Int32Rect PlaceCard(Int32Rect area, Int32Rect workArea, int cardWidth, int cardHeight, int gap, int labelReserve)
    {
        int centerX = area.X + (area.Width - cardWidth) / 2, centerY = area.Y + (area.Height - cardHeight) / 2;
        int workRight = workArea.X + workArea.Width, workBottom = workArea.Y + workArea.Height;
        Int32Rect visibleArea = Intersect(area, workArea);
        bool fitsInside = visibleArea.Width >= cardWidth + gap * 2 && visibleArea.Height >= cardHeight + gap * 2;
        int x, y;
        if (fitsInside)
        {
            x = visibleArea.X + (visibleArea.Width - cardWidth) / 2;
            y = visibleArea.Y + (visibleArea.Height - cardHeight) / 2;
        }
        else if (area.Y + area.Height + labelReserve + gap + cardHeight <= workBottom)
            (x, y) = (centerX, area.Y + area.Height + labelReserve + gap);
        else if (area.Y - gap - cardHeight >= workArea.Y)
            (x, y) = (centerX, area.Y - gap - cardHeight);
        else if (area.X + area.Width + gap + cardWidth <= workRight)
            (x, y) = (area.X + area.Width + gap, centerY);
        else if (area.X - gap - cardWidth >= workArea.X)
            (x, y) = (area.X - gap - cardWidth, centerY);
        else
            (x, y) = (centerX, centerY);
        x = Math.Clamp(x, workArea.X, Math.Max(workArea.X, workRight - cardWidth));
        y = Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workBottom - cardHeight));
        return new Int32Rect(x, y, cardWidth, cardHeight);
    }

    /// <summary>The "W × H" label: centered just below the area when the monitor has room, else just inside its bottom edge.</summary>
    internal static Int32Rect PlaceDimensionLabel(Int32Rect area, Int32Rect monitor, int labelWidth, int labelHeight, int gap)
    {
        int monitorRight = monitor.X + monitor.Width, monitorBottom = monitor.Y + monitor.Height;
        int x = Math.Clamp(area.X + (area.Width - labelWidth) / 2, monitor.X, Math.Max(monitor.X, monitorRight - labelWidth));
        int below = area.Y + area.Height + gap;
        int y = below + labelHeight <= monitorBottom ? below : Math.Max(monitor.Y, Math.Min(area.Y + area.Height, monitorBottom) - gap - labelHeight);
        return new Int32Rect(x, y, labelWidth, labelHeight);
    }

    /// <summary>Space reserved below the area for the dimension label, or 0 when the label sits inside the area.</summary>
    internal static int LabelReserve(Int32Rect area, Int32Rect monitor, int labelHeight, int gap) =>
        area.Y + area.Height + gap + labelHeight <= monitor.Y + monitor.Height ? gap + labelHeight : 0;

    internal static string Dimensions(int width, int height) => $"{width} × {height}";

    /// <summary>Abbreviates the user's profile folder to "~" (C:\Users\Name\Pictures\BetterShot → ~\Pictures\BetterShot).</summary>
    internal static string AbbreviateFolder(string folder, string userProfile)
    {
        if (string.IsNullOrWhiteSpace(userProfile)) return folder;
        string profile = userProfile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (folder.Equals(profile, StringComparison.OrdinalIgnoreCase)) return "~";
        return folder.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase) || folder.StartsWith(profile + "/", StringComparison.OrdinalIgnoreCase)
            ? "~" + folder[profile.Length..]
            : folder;
    }

    private static Int32Rect Intersect(Int32Rect first, Int32Rect second)
    {
        int left = Math.Max(first.X, second.X), top = Math.Max(first.Y, second.Y);
        int right = Math.Min(first.X + first.Width, second.X + second.Width), bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        return right > left && bottom > top ? new Int32Rect(left, top, right - left, bottom - top) : new Int32Rect(first.X, first.Y, 0, 0);
    }
}
