using BetterShot.Controls;
using BetterShot.Editing.BackgroundRemoval;
using System.Net.Http;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterShot.Editing;

/// <summary>"Remove the background" in the Background popup: local U²-Netp inference stored as an undoable document mask.</summary>
public sealed partial class EditorSurface
{
    private static readonly Lazy<HttpClient> ModelClient = new(CreateModelClient);
    private CancellationTokenSource? _backgroundRemoval;

    internal bool BackgroundRemovalBusyForTest => _backgroundRemoval is not null;

    private FrameworkElement BuildBackgroundRemovalSection()
    {
        bool removed = _document.BackgroundMask is not null;
        bool busy = _backgroundRemoval is not null;
        string text = busy ? "Removing the background…" : removed ? "Restore the background" : "Remove the background";
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new LucideIcon { Icon = LucideIconKind.Eraser, Width = 15, Height = 15, Foreground = Brushes.White, Margin = new Thickness(0, 0, 8, 0) });
        content.Children.Add(new TextBlock { Text = text, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content, Height = 34, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 4, 10, 4),
            Background = Brush("#303133"), Cursor = System.Windows.Input.Cursors.Hand, IsEnabled = !busy,
            ToolTip = removed ? "Show the original background again (undoable)" : "Make everything except the main subject transparent (undoable)"
        };
        button.Template = RoundedButtonTemplate();
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) =>
        {
            if (removed) RestoreRemovedBackground();
            else _ = RemoveBackgroundAsync();
        };
        var section = new StackPanel();
        section.Children.Add(button);
        section.Children.Add(new TextBlock
        {
            Text = $"Runs on this PC. The first use downloads a {BackgroundRemovalModel.SizeDescription} model. PNG keeps transparency; JPEG fills it with white.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#8E939B"), Margin = new Thickness(0, 6, 0, 0)
        });
        return section;
    }

    private void RestoreRemovedBackground()
    {
        CommitTextEdit();
        _document.SetBackgroundMask(null);
        _status.Text = "Background restored.";
        RebuildBackgroundOptions();
    }

    private async Task RemoveBackgroundAsync()
    {
        if (_backgroundRemoval is not null) return;
        CommitTextEdit();
        using var cancellation = new CancellationTokenSource();
        EventHandler cancelOnClose = (_, _) => cancellation.Cancel();
        _backgroundRemoval = cancellation;
        Detached += cancelOnClose;
        RebuildBackgroundOptions();
        try
        {
            var model = new BackgroundRemovalModel();
            if (!await EnsureModelAsync(model, cancellation.Token)) return;
            _status.Text = "Removing the background…";
            (BitmapSource source, Int32Rect? region) = SourceForBackgroundRemoval();
            AlphaMask mask = await BackgroundRemover.ComputeMaskAsync(source, model.ModelPath, cancellation.Token);
            if (region is Int32Rect at) mask = AlphaMask.Embed(mask, at, _document.Original.PixelWidth, _document.Original.PixelHeight);
            cancellation.Token.ThrowIfCancellationRequested();
            _document.SetBackgroundMask(mask);
            _status.Text = "Background removed. Undo (Ctrl+Z) or Background > Restore brings it back.";
        }
        catch (OperationCanceledException) { _status.Text = "Background removal canceled."; }
        catch (Exception ex) { _status.Text = $"Could not remove the background: {ex.Message}"; }
        finally
        {
            Detached -= cancelOnClose;
            _backgroundRemoval = null;
            RebuildBackgroundOptions();
        }
    }

    /// <summary>Uses the cropped area when a crop is set, so the subject is detected in what the user actually keeps.</summary>
    private (BitmapSource Source, Int32Rect? Region) SourceForBackgroundRemoval()
    {
        if (_document.Crop is not Rect crop) return (_document.Original, null);
        var region = new Int32Rect((int)crop.X, (int)crop.Y, (int)crop.Width, (int)crop.Height);
        var cropped = new CroppedBitmap(_document.Original, region);
        cropped.Freeze();
        return (cropped, region);
    }

    /// <summary>Returns true when the verified model is present, downloading it only after the user agrees.</summary>
    private async Task<bool> EnsureModelAsync(BackgroundRemovalModel model, CancellationToken cancellation)
    {
        if (await Task.Run(model.IsInstalled, cancellation)) return true;
        string message =
            $"Background removal runs entirely on this PC using the U²-Netp model ({BackgroundRemovalModel.SizeDescription}).\n\n" +
            $"BetterShot needs to download it once from {BackgroundRemovalModel.SourceDescription}:\n{BackgroundRemovalModel.DownloadUrl}\n\n" +
            $"It will be saved in {model.Directory} and checked against a pinned SHA-256 checksum before use. Your screenshots are never uploaded.\n\n" +
            "Download the model now?";
        MessageBoxResult answer = MessageBox.Show(Window.GetWindow(this) ?? Application.Current.MainWindow, message, "Download background removal model", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            _status.Text = "Background removal needs the model. Nothing was downloaded.";
            return false;
        }
        var progress = new Progress<double>(fraction => _status.Text = $"Downloading the background removal model… {fraction:P0}");
        await model.DownloadAsync(ModelClient.Value, progress, cancellation);
        return true;
    }

    private static HttpClient CreateModelClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BetterShot");
        return client;
    }
}
