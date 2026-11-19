using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BetterShot.Ai;

/// <summary>
/// Keeps the Anthropic API key encrypted with Windows data protection (DPAPI, current-user scope) in its own
/// file, outside settings.json. The key is never logged, displayed after saving, or written anywhere else.
/// A null path keeps the key in memory only (transient diagnostic shells).
/// </summary>
internal sealed class ApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BetterShot.AskClaude.ApiKey.v1");
    private readonly string? _path;
    private string? _memoryKey;

    internal ApiKeyStore(string? path) => _path = path is null ? null : Path.GetFullPath(path);

    internal static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterShot", "ask-claude-key.bin");

    internal string? FilePath => _path;

    internal bool HasKey => _path is null ? _memoryKey is not null : File.Exists(_path);

    internal void Save(string apiKey)
    {
        string trimmed = apiKey?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) throw new ArgumentException("The API key is empty.", nameof(apiKey));
        if (_path is null) { _memoryKey = trimmed; return; }

        byte[] plain = Encoding.UTF8.GetBytes(trimmed);
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tempPath, protectedBytes);
                File.Move(tempPath, _path, true);
            }
            finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>Returns the key, or null when none is saved or it cannot be decrypted (for example, another Windows user's file).</summary>
    internal string? Load()
    {
        if (_path is null) return _memoryKey;
        if (!File.Exists(_path)) return null;
        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal void Remove()
    {
        _memoryKey = null;
        if (_path is not null && File.Exists(_path)) File.Delete(_path);
    }
}
