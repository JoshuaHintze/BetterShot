using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace BetterShot.Platform;

[SupportedOSPlatform("windows")]
internal sealed class WindowsStartupRegistration
{
    private const string ShortcutFileName = "BetterShot.lnk";
    private const string BackgroundArgument = "--background";

    private readonly string _startupDirectory;
    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly string? _runtimeRoot;
    private readonly string _powerShellPath;
    private readonly bool _selfContained;

    internal WindowsStartupRegistration(
        string? startupDirectory = null,
        string? executablePath = null,
        string? runtimeRoot = null)
    {
        _startupDirectory = Path.GetFullPath(startupDirectory ??
            Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        _executablePath = Path.GetFullPath(executablePath ??
            Environment.ProcessPath ?? throw new InvalidOperationException("The BetterShot executable path is unavailable."));
        _workingDirectory = Path.GetDirectoryName(_executablePath) ??
            throw new InvalidOperationException("The BetterShot executable directory is unavailable.");
        _selfContained = File.Exists(Path.Combine(_workingDirectory, "coreclr.dll"));
        _runtimeRoot = FindRuntimeRoot(runtimeRoot);
        _powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
    }

    internal bool IsEnabled => File.Exists(ShortcutPath);

    internal string LaunchArgumentsForTest => BuildArguments();

    internal void EnsureEnabled()
    {
        Directory.CreateDirectory(_startupDirectory);
        string arguments = BuildArguments();

        if (ShortcutMatches(ShortcutPath, arguments))
        {
            return;
        }

        string temporaryPath = Path.Combine(
            _startupDirectory,
            $"BetterShot.{Guid.NewGuid():N}.lnk");

        try
        {
            CreateShortcut(temporaryPath, arguments);
            File.Move(temporaryPath, ShortcutPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal void Disable()
    {
        if (File.Exists(ShortcutPath))
        {
            File.Delete(ShortcutPath);
        }
    }

    private string ShortcutPath => Path.Combine(_startupDirectory, ShortcutFileName);

    private string BuildArguments()
    {
        if (_selfContained) return BackgroundArgument;
        StringBuilder script = new();
        if (_runtimeRoot is not null)
        {
            string runtimeLiteral = ToPowerShellLiteral(_runtimeRoot);
            script.Append("$env:DOTNET_ROOT=").Append(runtimeLiteral).Append(';');
            script.Append("$env:DOTNET_ROOT_X64=").Append(runtimeLiteral).Append(';');
        }

        script.Append("Start-Process -FilePath ")
            .Append(ToPowerShellLiteral(_executablePath))
            .Append(" -ArgumentList '--background' -WorkingDirectory ")
            .Append(ToPowerShellLiteral(_workingDirectory))
            .Append(" -WindowStyle Hidden");

        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
        return $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encodedCommand}";
    }

    private bool ShortcutMatches(string path, string arguments)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = CreateShell();
            shortcut = ((dynamic)shell).CreateShortcut(path);
            dynamic link = shortcut;
            return PathsEqual((string)link.TargetPath, _selfContained ? _executablePath : _powerShellPath)
                && string.Equals((string)link.Arguments, arguments, StringComparison.Ordinal)
                && PathsEqual((string)link.WorkingDirectory, _workingDirectory);
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private void CreateShortcut(string path, string arguments)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = CreateShell();
            shortcut = ((dynamic)shell).CreateShortcut(path);
            dynamic link = shortcut;
            link.TargetPath = _selfContained ? _executablePath : _powerShellPath;
            link.Arguments = arguments;
            link.WorkingDirectory = _workingDirectory;
            link.WindowStyle = 7;
            link.IconLocation = _executablePath + ",0";
            link.Description = "Start BetterShot in the background";
            link.Save();
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static object CreateShell()
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)
            ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        return Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Windows Script Host could not be started.");
    }

    private static string? FindRuntimeRoot(string? requestedRoot)
    {
        if (!string.IsNullOrWhiteSpace(requestedRoot))
        {
            return Path.GetFullPath(requestedRoot);
        }

        string? environmentRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(environmentRoot) && Directory.Exists(environmentRoot))
        {
            return Path.GetFullPath(environmentRoot);
        }

        try
        {
            DirectoryInfo? directory = new(RuntimeEnvironment.GetRuntimeDirectory());
            if (directory.Exists
                && directory.Parent?.Name.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase) == true
                && directory.Parent.Parent?.Name.Equals("shared", StringComparison.OrdinalIgnoreCase) == true)
            {
                DirectoryInfo? root = directory.Parent.Parent.Parent;
                if (root?.Exists == true)
                {
                    return root.FullName;
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string ToPowerShellLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}