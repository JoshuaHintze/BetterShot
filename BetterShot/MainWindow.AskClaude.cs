using BetterShot.Ai;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;

namespace BetterShot;

/// <summary>Settings > Ask Claude: API key (DPAPI-protected, never shown again after saving) and model choice.</summary>
public partial class MainWindow
{
    private ApiKeyStore _apiKeyStore = new(null);

    internal ApiKeyStore ApiKeyStoreForTest => _apiKeyStore;

    /// <summary>
    /// The installed app keeps the key next to the History folder; diagnostic shells with a fixture history root
    /// keep it beside that root; transient shells keep it in memory only.
    /// </summary>
    private void InitializeAskClaude(bool enableHistory, string? historyRoot)
    {
        string? keyPath = !enableHistory ? null
            : historyRoot is null ? ApiKeyStore.DefaultPath
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(historyRoot))!, "ask-claude-key.bin");
        _apiKeyStore = new ApiKeyStore(keyPath);
        AskClaudeEnvironment.KeyStore = _apiKeyStore;
        AskClaudeEnvironment.ModelProvider = () => CurrentSettings.AiModel;
        AskClaudeEnvironment.OpenSettings = OpenAskClaudeSettings;

        foreach (ClaudeModel model in ClaudeModels.All)
            AiModelComboBox.Items.Add(new ComboBoxItem { Content = model.Label, Tag = model.Id });
        LoadAskClaudeSettingsIntoControls();
    }

    private void LoadAskClaudeSettingsIntoControls()
    {
        _updatingSettings = true;
        try { SelectTagged(AiModelComboBox, CurrentSettings.AiModel); }
        finally { _updatingSettings = false; }
        UpdateApiKeyState();
    }

    private void UpdateApiKeyState()
    {
        bool saved = _apiKeyStore.HasKey;
        ApiKeySavedState.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        RemoveApiKeyButton.IsEnabled = saved;
        SaveApiKeyButton.IsEnabled = ApiKeyBox.Password.Trim().Length > 0;
        ApiKeyPlaceholder.Text = saved ? "Paste a new key to replace the saved one" : "Paste your API key";
        ApiKeyPlaceholder.Visibility = ApiKeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowApiKeyProblem(string? problem)
    {
        ApiKeyProblemText.Text = problem ?? string.Empty;
        ApiKeyProblemText.Visibility = string.IsNullOrEmpty(problem) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ShowApiKeyProblem(null);
        UpdateApiKeyState();
    }

    private void ApiKeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SaveApiKey();
    }

    private void SaveApiKey_Click(object sender, RoutedEventArgs e) => SaveApiKey();

    private void SaveApiKey()
    {
        string key = ApiKeyBox.Password.Trim();
        if (key.Length == 0) return;
        if (key.Any(char.IsWhiteSpace)) { ShowApiKeyProblem("That doesn't look like an API key: it contains spaces or line breaks."); return; }
        try
        {
            _apiKeyStore.Save(key);
            ApiKeyBox.Clear();
            ShowApiKeyProblem(null);
            SetStatus("API key saved securely.");
        }
        catch (Exception ex) { ShowApiKeyProblem($"Could not save the key: {FriendlyMessage(ex)}"); }
        UpdateApiKeyState();
    }

    private void RemoveApiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _apiKeyStore.Remove();
            ShowApiKeyProblem(null);
            SetStatus("API key removed.");
        }
        catch (Exception ex) { ShowApiKeyProblem($"Could not remove the key: {FriendlyMessage(ex)}"); }
        UpdateApiKeyState();
    }

    private void AiModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _updatingSettings || SelectedTag(AiModelComboBox) is not string model) return;
        if (model == CurrentSettings.AiModel) return;
        TrySaveSettings(settings => settings with { AiModel = model }, "the Ask Claude model");
        LoadAskClaudeSettingsIntoControls();
    }

    private void OpenAskClaudeSettings()
    {
        ShowAndActivate();
        SetSettingsVisible(true);
        Dispatcher.BeginInvoke(() => { AskClaudeSection.BringIntoView(); ApiKeyBox.Focus(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
