using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CairnMultiplayerMod.Internal.Diagnostics;

internal static class DiagnosticRetention
{
    public static void Prune(string directory, string pattern, int maxFiles, long maxTotalBytes,
        string protectedPath = null, Action<string> warning = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(pattern)
            || maxFiles < 1 || maxTotalBytes < 1 || !Directory.Exists(directory))
            return;

        string protectedFullPath = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(protectedPath))
                protectedFullPath = Path.GetFullPath(protectedPath);
        }
        catch (Exception exception)
        {
            ReportFailure(warning, $"Could not normalize protected diagnostic path '{protectedPath}'", exception);
        }

        var files = new List<FileInfo>();
        try
        {
            files.AddRange(Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => protectedFullPath != null
                    && string.Equals(file.FullName, protectedFullPath, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(file => file.LastWriteTimeUtc));
        }
        catch (Exception exception)
        {
            ReportFailure(warning, $"Could not enumerate diagnostics in '{directory}'", exception);
            return;
        }

        long keptBytes = 0;
        var keptFiles = 0;
        foreach (var file in files)
        {
            var isProtected = protectedFullPath != null
                              && string.Equals(file.FullName, protectedFullPath, StringComparison.OrdinalIgnoreCase);
            var keep = isProtected || (keptFiles < maxFiles && keptBytes + file.Length <= maxTotalBytes);
            if (keep)
            {
                keptFiles++;
                keptBytes += file.Length;
                continue;
            }

            try { file.Delete(); }
            catch (Exception exception)
            {
                ReportFailure(warning, $"Could not delete expired diagnostic '{file.FullName}'", exception);
            }
        }
    }

    private static void ReportFailure(Action<string> warning, string operation, Exception exception)
    {
        var message = $"{operation}: {exception.GetType().Name}: {exception.Message}";
        if (warning == null)
        {
            System.Diagnostics.Debug.WriteLine(message);
            return;
        }

        try { warning(message); }
        catch (Exception loggerException)
        {
            System.Diagnostics.Debug.WriteLine($"{message}{Environment.NewLine}Diagnostic logger also failed: {loggerException}");
        }
    }
}
