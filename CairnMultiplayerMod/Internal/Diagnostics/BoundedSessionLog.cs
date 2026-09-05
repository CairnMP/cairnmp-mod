using System;
using System.IO;
using System.Text;

namespace CairnMultiplayerMod.Internal.Diagnostics;

internal static class BoundedSessionLog
{
    private static readonly object WriteLock = new();
    internal const int MaxFileBytes = 2 * 1024 * 1024;

    internal static void Append(string path, string text)
    {
        lock (WriteLock)
        {
            // Bound encoding allocation too; one exceptional message cannot consume the budget.
            if (text.Length > MaxFileBytes / 4) text = text.Substring(0, MaxFileBytes / 4) + "\n[truncated]\n";
            var bytes = Encoding.UTF8.GetBytes(text);
            if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaxFileBytes)
                File.Move(path, Path.Combine(Path.GetDirectoryName(path),
                    Path.GetFileNameWithoutExtension(path) + "-part-" + Guid.NewGuid().ToString("N") + ".log"));
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                stream.Write(bytes, 0, bytes.Length);
            DiagnosticRetention.Prune(Path.GetDirectoryName(path), "CairnMP-session-*.log", 10, 20L * 1024 * 1024, path);
        }
    }
}
