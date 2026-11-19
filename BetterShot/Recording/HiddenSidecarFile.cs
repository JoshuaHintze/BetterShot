using System.IO;
using System.Text;

namespace BetterShot.Recording;

/// <summary>
/// Writes a hidden sidecar next to a movie atomically: the text goes to a temporary file in the same folder, which then
/// replaces the live file in one move, so a crash or full disk never leaves a truncated sidecar behind.
/// </summary>
internal static class HiddenSidecarFile
{
    internal static void WriteAllText(string path, string text)
    {
        string fullPath = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("The sidecar path has no folder.", nameof(path));
        string temporary = Path.Combine(folder, $"{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        FileAttributes? originalAttributes = null;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(fullPath))
            {
                // Hidden or read-only attributes on the old sidecar can block the replacing move.
                originalAttributes = File.GetAttributes(fullPath);
                File.SetAttributes(fullPath, FileAttributes.Normal);
            }
            File.Move(temporary, fullPath, overwrite: true);
            originalAttributes = null;
            File.SetAttributes(fullPath, File.GetAttributes(fullPath) | FileAttributes.Hidden);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (originalAttributes is FileAttributes restore && File.Exists(fullPath)) File.SetAttributes(fullPath, restore);
        }
    }
}
