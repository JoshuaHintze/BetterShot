using BetterShot.Editing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace BetterShot.Presentation;

public sealed class CapturePresentationManager : IDisposable
{
    private const int MaximumVisibleThumbnails = 4;
    private readonly List<CaptureDocument> _recent = new();
    private readonly List<CaptureThumbnailWindow> _thumbnails = new();
    private readonly Dictionary<CaptureDocument, EditorWindow> _editors = new();
    private readonly Dictionary<CaptureThumbnailWindow, string> _screens = new();

    public event EventHandler<CaptureDocumentEventArgs>? DeleteRequested;
    public event EventHandler<CaptureDocumentEventArgs>? Removed;

    public bool DragAsFile { get; set; }
    public IReadOnlyList<CaptureDocument> Recent => _recent;
    internal IReadOnlyList<CaptureThumbnailWindow> Thumbnails => _thumbnails;
    internal int OpenEditorCount => _editors.Count;
    internal EditorWindow? EditorFor(CaptureDocument document) => _editors.GetValueOrDefault(document);

    public CaptureThumbnailWindow Present(CaptureDocument document)
    {
        _recent.Insert(0, document);
        var thumbnail = new CaptureThumbnailWindow(this, document);
        _thumbnails.Insert(0, thumbnail);
        _screens[thumbnail] = Forms.Screen.FromPoint(Forms.Cursor.Position).DeviceName;
        thumbnail.Show();
        Reflow();
        return thumbnail;
    }

    public void Retain(CaptureDocument document)
    {
        if (!_recent.Contains(document)) _recent.Add(document);
    }

    internal void RequestDelete(CaptureDocument document) =>
        DeleteRequested?.Invoke(this, new CaptureDocumentEventArgs(document));

    internal void CloseEditor(CaptureDocument document)
    {
        if (_editors.TryGetValue(document, out EditorWindow? editor)) editor.Close();
    }

    public void Delete(CaptureDocument document)
    {
        CloseEditor(document);
        foreach (CaptureThumbnailWindow thumbnail in _thumbnails.Where(item => ReferenceEquals(item.Document, document)).ToArray())
        {
            _thumbnails.Remove(thumbnail);
            _screens.Remove(thumbnail);
            thumbnail.Close();
        }
        if (!_recent.Remove(document)) return;
        Reflow();
        Removed?.Invoke(this, new CaptureDocumentEventArgs(document));
    }
    public void OpenEditor(CaptureDocument document)
    {
        if (_editors.TryGetValue(document, out EditorWindow? existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Show(); existing.Activate(); return;
        }
        var editor = new EditorWindow(document);
        _editors.Add(document, editor);
        editor.Closed += (_, _) => _editors.Remove(document);
        editor.Show();
    }

    internal void Dismiss(CaptureThumbnailWindow thumbnail)
    {
        _thumbnails.Remove(thumbnail);
        _screens.Remove(thumbnail);
        thumbnail.Close();
        Reflow();
    }

    public CaptureVisibilitySnapshot HideForCapture()
    {
        CaptureVisibilityEntry[] visible = Application.Current.Windows.Cast<Window>().Where(window => window.IsVisible)
            .Select(window => new CaptureVisibilityEntry(window, window.ShowActivated)).ToArray();
        foreach (CaptureVisibilityEntry entry in visible) entry.Window.Hide();
        return new CaptureVisibilitySnapshot(visible);
    }

    public void RestoreAfterCapture(CaptureVisibilitySnapshot snapshot, Window? keepHidden = null)
    {
        foreach (CaptureVisibilityEntry entry in snapshot.Entries.Where(entry => entry.Window != keepHidden && entry.Window.IsLoaded))
        {
            Window window = entry.Window;
            window.ShowActivated = false;
            window.Show();
            if (new WindowInteropHelper(window).Handle is IntPtr handle && handle != IntPtr.Zero) ShowWindow(handle, 4);
            window.ShowActivated = entry.ShowActivated;
        }
    }

    private void Reflow()
    {
        foreach (IGrouping<string, CaptureThumbnailWindow> group in _thumbnails.GroupBy(thumbnail => _screens[thumbnail]))
        {
            Forms.Screen screen = Forms.Screen.AllScreens.FirstOrDefault(item => item.DeviceName == group.Key) ?? Forms.Screen.PrimaryScreen!;
            const int baseWidth = 332, baseHeight = 242, baseGap = 10, baseInset = 18;
            int index = 0;
            foreach (CaptureThumbnailWindow thumbnail in group)
            {
                IntPtr handle = new WindowInteropHelper(thumbnail).Handle;
                SetWindowPos(handle, IntPtr.Zero, screen.WorkingArea.Left, screen.WorkingArea.Top, 0, 0, 0x0010 | 0x0004 | 0x0001);
                double scale = Math.Max(96u, GetDpiForWindow(handle)) / 96d;
                int width = (int)Math.Round(baseWidth * scale), height = (int)Math.Round(baseHeight * scale);
                int gap = (int)Math.Round(baseGap * scale), inset = (int)Math.Round(baseInset * scale);
                int capacity = Math.Max(1, Math.Min(MaximumVisibleThumbnails, (screen.WorkingArea.Height - inset) / (height + gap)));
                if (index >= capacity) { thumbnail.Hide(); index++; continue; }
                if (!thumbnail.IsVisible) thumbnail.Show();
                int x = screen.WorkingArea.Left + inset;
                int y = screen.WorkingArea.Bottom - inset - height - index * (height + gap);
                SetWindowPos(handle, IntPtr.Zero, x, y, width, height, 0x0010 | 0x0004);
                index++;
            }
        }
    }

    public void Dispose()
    {
        foreach (EditorWindow editor in _editors.Values.ToArray()) editor.Close();
        foreach (CaptureThumbnailWindow thumbnail in _thumbnails.ToArray()) thumbnail.Close();
        _editors.Clear(); _thumbnails.Clear(); _screens.Clear(); _recent.Clear();
    }

    internal bool IsInsideWorkingArea(CaptureThumbnailWindow thumbnail)
    {
        if (!GetWindowRect(new WindowInteropHelper(thumbnail).Handle, out NativeRect rect)) return false;
        Forms.Screen screen = Forms.Screen.FromRectangle(System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
        return screen.WorkingArea.Contains(System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
}

public sealed record CaptureVisibilityEntry(Window Window, bool ShowActivated);
public sealed record CaptureVisibilitySnapshot(IReadOnlyList<CaptureVisibilityEntry> Entries);
public sealed class CaptureDocumentEventArgs(CaptureDocument document) : EventArgs
{
    public CaptureDocument Document { get; } = document;
}
