using BetterShot.Controls;
using BetterShot.Editing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterShot.Ai;

/// <summary>
/// "Ask about this capture": preset or free-text questions about a rendered capture, answered by Claude with
/// streaming, follow-ups, and copy. Nothing is sent until the user clicks a preset or sends a question.
/// When opened for a live capture, the preview follows edits until a conversation starts, and the image is rendered
/// again right before the first question of each conversation, so Claude sees the capture as it is when asked.
/// </summary>
public partial class AskClaudeWindow : Window
{
    private static readonly Brush YouBrush = FrozenBrush(0x9B, 0x9D, 0xA5);
    private static readonly Brush ClaudeBrush = FrozenBrush(0xFF, 0xD8, 0x4A);
    private static readonly Brush QuestionBrush = FrozenBrush(0xF0, 0xF1, 0xF3);
    private static readonly Brush MutedBrush = FrozenBrush(0x8F, 0x92, 0x99);

    private readonly Func<BitmapSource>? _renderCurrent;
    private readonly CaptureDocument? _liveDocument;
    private readonly AskClaudeConversation _conversation = new();
    private readonly DispatcherTimer _renderTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly DispatcherTimer _previewRefreshTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
    private BitmapSource _image;
    private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };
    private CancellationTokenSource? _requestCancellation;
    private string? _imagePngBase64;
    private Section? _streamingAnswer;
    private string _streamingText = string.Empty;
    private int _requestGeneration;
    private string _lastAnswer = string.Empty;
    private bool _closing;

    internal AskClaudeWindow(BitmapSource image, string title) : this(image, null, null, title) { }

    private AskClaudeWindow(BitmapSource image, Func<BitmapSource>? renderCurrent, CaptureDocument? liveDocument, string title)
    {
        ArgumentNullException.ThrowIfNull(image);
        _image = image;
        _renderCurrent = renderCurrent;
        _liveDocument = liveDocument;
        InitializeComponent();
        PreviewImage.Source = image;
        CaptureTitleText.Text = title;
        AnswerView.Document = CreateDocument();
        foreach (AskClaudePreset preset in AskClaudePresets.All)
        {
            var chip = new Button { Style = (Style)FindResource("PresetChip"), Content = preset.Label, ToolTip = preset.Prompt, Tag = preset };
            AutomationProperties.SetName(chip, "Ask: " + preset.Label);
            chip.Click += Preset_Click;
            PresetPanel.Children.Add(chip);
        }
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); RenderStreamingAnswer(); };
        _copiedTimer.Tick += (_, _) => { _copiedTimer.Stop(); CopyAnswerText.Text = "Copy answer"; CopyIcon.Icon = LucideIconKind.Copy; };
        Closing += (_, _) => { _closing = true; _requestCancellation?.Cancel(); };
        Closed += (_, _) => { _renderTimer.Stop(); _copiedTimer.Stop(); _previewRefreshTimer.Stop(); if (_liveDocument is not null) _liveDocument.Changed -= LiveDocument_Changed; };
        _previewRefreshTimer.Tick += (_, _) => { _previewRefreshTimer.Stop(); RefreshPreviewBeforeConversation(); };
        if (_liveDocument is not null) _liveDocument.Changed += LiveDocument_Changed;
        Loaded += (_, _) => QuestionBox.Focus();
        PreviewKeyDown += Window_PreviewKeyDown;
        UpdateState();
    }

    /// <summary>
    /// Opens an Ask Claude window for an already-rendered capture (annotations included). Safe to call from any
    /// UI surface, including the editor. The window is independent of <paramref name="owner"/> so closing a
    /// thumbnail or hiding the library does not close it; the owner only positions it.
    /// </summary>
    public static AskClaudeWindow Show(Window? owner, BitmapSource image, string title)
    {
        ArgumentNullException.ThrowIfNull(image);
        return ShowWindow(owner, new AskClaudeWindow(image, CleanTitle(title)));
    }

    /// <summary>
    /// Opens Ask Claude for a capture that may still change: <paramref name="renderCurrent"/> is called again right before
    /// the first question of each conversation (and on New conversation), so the sent image matches what is on screen.
    /// </summary>
    internal static AskClaudeWindow Show(Window? owner, Func<BitmapSource> renderCurrent, string title)
    {
        ArgumentNullException.ThrowIfNull(renderCurrent);
        return ShowWindow(owner, new AskClaudeWindow(renderCurrent(), renderCurrent, null, CleanTitle(title)));
    }

    /// <summary>Opens Ask Claude for a live capture document; the preview also follows its edits until a conversation starts.</summary>
    internal static AskClaudeWindow Show(Window? owner, CaptureDocument document, string title)
    {
        ArgumentNullException.ThrowIfNull(document);
        return ShowWindow(owner, new AskClaudeWindow(document.Render(), document.Render, document, CleanTitle(title)));
    }

    private static string CleanTitle(string title) => string.IsNullOrWhiteSpace(title) ? string.Empty : title;

    private static AskClaudeWindow ShowWindow(Window? owner, AskClaudeWindow window)
    {
        if (owner is { IsVisible: true, WindowState: not WindowState.Minimized } && owner.ActualWidth >= window.Width && owner.ActualHeight >= window.Height)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = owner.Left + (owner.ActualWidth - window.Width) / 2;
            window.Top = owner.Top + (owner.ActualHeight - window.Height) / 2;
        }
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        window.Activate();
        return window;
    }

    internal string LastAnswerForTest => _lastAnswer;
    internal BitmapSource PreviewForTest => _image;
    internal bool IsBusyForTest => _requestCancellation is not null;
    internal string ErrorForTest => ErrorBanner.Visibility == Visibility.Visible ? ErrorText.Text : string.Empty;
    internal string ConversationTextForTest => new TextRange(AnswerView.Document.ContentStart, AnswerView.Document.ContentEnd).Text;
    internal int TurnCountForTest => _conversation.Messages.Count;
    internal Task AskForTest(string question) => AskAsync(question);
    internal void ClickPresetForTest(int index) => ((Button)PresetPanel.Children[index]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AskClaudePreset preset }) _ = AskAsync(preset.Prompt);
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_requestCancellation is not null) { _requestCancellation.Cancel(); return; }
        _ = AskAsync(QuestionBox.Text);
    }

    private void QuestionBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        if (_requestCancellation is null) _ = AskAsync(QuestionBox.Text);
    }

    /// <summary>Esc stops an answer in progress; otherwise it closes the window.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (_requestCancellation is not null) _requestCancellation.Cancel(); else Close();
    }

    private void QuestionBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateState();

    private async Task AskAsync(string rawQuestion)
    {
        string question = rawQuestion.Trim();
        if (question.Length == 0 || _requestCancellation is not null || _closing) return;
        HideError();

        string? apiKey = AskClaudeEnvironment.KeyStore.Load();
        if (string.IsNullOrWhiteSpace(apiKey)) { ShowError(AskClaudeErrors.MissingKey()); return; }
        string model = AskClaudeEnvironment.ModelProvider();

        Paragraph questionBlock = AppendQuestion(question);
        Section answer = AppendAnswerSection();
        int generation = ++_requestGeneration;
        int turnsBefore = _conversation.Messages.Count;
        _streamingAnswer = answer;
        _streamingText = string.Empty;
        QuestionBox.Clear();
        using var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        SetStatus($"Asking {ClaudeModels.LabelFor(model)}…");
        UpdateState();
        var progress = new Progress<string>(text =>
        {
            if (generation != _requestGeneration || _streamingAnswer is null) return;
            _streamingText = text;
            if (!_renderTimer.IsEnabled) _renderTimer.Start();
        });
        try
        {
            string reply = await _conversation.AskAsync(AskClaudeEnvironment.ClientFactory(), apiKey, model, question,
                EncodeImage, progress, cancellation.Token);
            if (generation != _requestGeneration) return;
            FinishAnswer(answer, reply);
            _lastAnswer = reply;
            SetStatus(string.Empty);
        }
        catch (OperationCanceledException)
        {
            if (_closing || generation != _requestGeneration) return;
            // The conversation keeps a partial answer (user + assistant turns) only when some text had arrived.
            string partial = _conversation.Messages.Count == turnsBefore + 2
                ? _conversation.Messages[^1].Text
                : string.Empty;
            if (string.IsNullOrWhiteSpace(partial)) { RemoveTurn(questionBlock, answer); QuestionBox.Text = question; }
            else { FinishAnswer(answer, partial); _lastAnswer = partial; answer.Blocks.Add(new Paragraph(new Run("Stopped.")) { Foreground = MutedBrush, FontSize = 12 }); }
            SetStatus("Stopped.");
        }
        catch (AskClaudeException ex)
        {
            if (generation != _requestGeneration) return;
            RemoveTurn(questionBlock, answer);
            if (QuestionBox.Text.Length == 0) QuestionBox.Text = question;
            ShowError(ex);
            SetStatus(string.Empty);
        }
        catch (Exception ex)
        {
            // Image encoding or unexpected client failures: keep the window usable and say what happened.
            if (generation != _requestGeneration) return;
            RemoveTurn(questionBlock, answer);
            if (QuestionBox.Text.Length == 0) QuestionBox.Text = question;
            ShowError(new AskClaudeException($"Something went wrong: {ex.Message}"));
            SetStatus(string.Empty);
        }
        finally
        {
            if (ReferenceEquals(_requestCancellation, cancellation)) _requestCancellation = null;
            if (generation == _requestGeneration) _streamingAnswer = null;
            _renderTimer.Stop();
            if (!_closing) { UpdateState(); QuestionBox.Focus(); }
        }
    }

    /// <summary>
    /// Runs on the first ask of a conversation only, on the UI thread (WPF resizing), right before sending. A live capture
    /// is rendered again here so edits made after the window opened are included.
    /// </summary>
    private string EncodeImage()
    {
        if (_renderCurrent is not null)
        {
            _previewRefreshTimer.Stop();
            ShowCurrentRender();
        }
        return _imagePngBase64 ??= ClaudeImage.ToPngBase64(_image);
    }

    private void LiveDocument_Changed(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => LiveDocument_Changed(sender, e)); return; }
        if (_closing || !_conversation.IsEmpty) return;
        _previewRefreshTimer.Stop();
        _previewRefreshTimer.Start();
    }

    /// <summary>Follows edits only while no conversation has started; once asked, the preview shows what was sent.</summary>
    private void RefreshPreviewBeforeConversation()
    {
        if (_closing || _renderCurrent is null || !_conversation.IsEmpty) return;
        ShowCurrentRender();
    }

    private void ShowCurrentRender()
    {
        if (_renderCurrent is null) return;
        _image = _renderCurrent();
        _imagePngBase64 = null;
        PreviewImage.Source = _image;
    }

    private void RenderStreamingAnswer()
    {
        if (_streamingAnswer is null) return;
        if (_streamingText.Length > 0) SetStatus("Claude is answering…");
        bool atBottom = AnswerView.VerticalOffset + AnswerView.ViewportHeight >= AnswerView.ExtentHeight - 24;
        ReplaceAnswerBlocks(_streamingAnswer, _streamingText);
        if (atBottom) AnswerView.ScrollToEnd();
    }

    private void FinishAnswer(Section answer, string text)
    {
        ReplaceAnswerBlocks(answer, text);
        AnswerView.ScrollToEnd();
    }

    private static void ReplaceAnswerBlocks(Section answer, string text)
    {
        Block label = answer.Blocks.FirstBlock;
        answer.Blocks.Clear();
        answer.Blocks.Add(label);
        answer.Blocks.AddRange(AnswerMarkdown.ToBlocks(text));
    }

    private Paragraph AppendQuestion(string question)
    {
        var block = new Paragraph { Margin = new Thickness(0, AnswerView.Document.Blocks.Count == 0 ? 0 : 14, 0, 10), Foreground = QuestionBrush };
        block.Inlines.Add(new Run("You") { Foreground = YouBrush, FontSize = 12, FontWeight = FontWeights.SemiBold });
        block.Inlines.Add(new LineBreak());
        block.Inlines.Add(new Run(question));
        AnswerView.Document.Blocks.Add(block);
        AnswerView.ScrollToEnd();
        UpdateState();
        return block;
    }

    private Section AppendAnswerSection()
    {
        var section = new Section();
        var label = new Paragraph(new Run("Claude") { Foreground = ClaudeBrush, FontSize = 12, FontWeight = FontWeights.SemiBold }) { Margin = new Thickness(0, 0, 0, 4) };
        section.Blocks.Add(label);
        section.Blocks.Add(new Paragraph(new Run("…")) { Foreground = MutedBrush });
        AnswerView.Document.Blocks.Add(section);
        AnswerView.ScrollToEnd();
        return section;
    }

    private void RemoveTurn(Paragraph question, Section answer)
    {
        AnswerView.Document.Blocks.Remove(answer);
        AnswerView.Document.Blocks.Remove(question);
        UpdateState();
    }

    private void NewConversation_Click(object sender, RoutedEventArgs e)
    {
        _requestGeneration++;
        _requestCancellation?.Cancel();
        _conversation.Reset();
        _lastAnswer = string.Empty;
        _previewRefreshTimer.Stop();
        ShowCurrentRender();
        AnswerView.Document = CreateDocument();
        HideError();
        SetStatus(string.Empty);
        UpdateState();
        QuestionBox.Focus();
    }

    private async void CopyAnswer_Click(object sender, RoutedEventArgs e)
    {
        if (_lastAnswer.Length == 0) return;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Clipboard.SetText(_lastAnswer);
                CopyAnswerText.Text = "Copied";
                CopyIcon.Icon = LucideIconKind.Check;
                _copiedTimer.Stop(); _copiedTimer.Start();
                return;
            }
            catch (COMException) when (attempt < 5) { await Task.Delay(attempt * 60); }
            catch (COMException) { SetStatus("The clipboard is busy. Please try again."); }
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => AskClaudeEnvironment.OpenSettings?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowError(AskClaudeException error)
    {
        ErrorText.Text = error.Message;
        OpenSettingsButton.Visibility = error.NeedsApiKey && AskClaudeEnvironment.OpenSettings is not null ? Visibility.Visible : Visibility.Collapsed;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBanner.Visibility = Visibility.Collapsed;

    private void SetStatus(string text) => StatusText.Text = text;

    private void UpdateState()
    {
        if (!IsInitialized) return;
        bool busy = _requestCancellation is not null;
        bool started = AnswerView.Document.Blocks.Count > 0 || !_conversation.IsEmpty;
        PresetPanel.Visibility = started ? Visibility.Collapsed : Visibility.Visible;
        foreach (UIElement chip in PresetPanel.Children) chip.IsEnabled = !busy;
        EmptyAnswerText.Visibility = started ? Visibility.Collapsed : Visibility.Visible;
        QuestionPlaceholder.Text = _conversation.IsEmpty ? "What would you like to know?" : "Ask a follow-up";
        QuestionPlaceholder.Visibility = QuestionBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SendIcon.Icon = busy ? LucideIconKind.Square : LucideIconKind.Send;
        SendButton.ToolTip = busy ? "Stop (Esc)" : "Send (Enter). Shift+Enter adds a new line.";
        AutomationProperties.SetName(SendButton, busy ? "Stop" : "Send");
        SendButton.IsEnabled = busy || QuestionBox.Text.Trim().Length > 0;
        CopyAnswerButton.IsEnabled = _lastAnswer.Length > 0;
        NewConversationButton.IsEnabled = started;
    }

    private static FlowDocument CreateDocument() => new()
    {
        PagePadding = new Thickness(0),
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 14,
        Foreground = FrozenBrush(0xE7, 0xE8, 0xEB),
        LineHeight = 21
    };

    private static Brush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
