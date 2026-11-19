using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Editing;

/// <summary>A standalone window around the shared <see cref="EditorSurface"/>, used when the library window is not the host.</summary>
public sealed class EditorWindow : Window
{
    public EditorWindow(CaptureDocument document) : this(document, null) { }

    internal EditorWindow(CaptureDocument document, EditorStyleStore? styles)
    {
        Surface = new EditorSurface(document, styles);
        Title = "BetterShot Editor"; Width = 1180; Height = 790; MinWidth = 780; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x17));
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/BetterShot.ico", UriKind.Absolute)); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException) { } // The default icon is fine.
        Content = Surface;
        Surface.DoneRequested += (_, _) => Close();
        Closing += (_, _) => Surface.CommitPendingEdits();
        Closed += (_, _) => Surface.Detach();
    }

    /// <summary>The editor shown in this window.</summary>
    internal EditorSurface Surface { get; }
}
