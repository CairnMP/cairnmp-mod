using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace CairnMultiplayerMod.Internal.Diagnostics;

internal sealed class CrashIncident
{
    public DateTimeOffset OccurredAtUtc { get; init; }
    public string ModVersion { get; init; }
    public string Context { get; init; }
    public string ExceptionType { get; init; }
    public string Message { get; init; }
    public string StackTrace { get; init; }
    public string Fingerprint { get; init; }
}

/// <summary>Creates a local, self-contained crash bundle without transmitting it.</summary>
internal static class CrashArchiveBuilder
{
    internal const int MaxLogFiles = 12;
    internal const long MaxLogBytes = 8L * 1024 * 1024;

    public static string Create(string outputDirectory, CrashIncident incident, IEnumerable<string> logPaths)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("A crash output directory is required.", nameof(outputDirectory));
        if (incident == null)
            throw new ArgumentNullException(nameof(incident));

        Directory.CreateDirectory(outputDirectory);

        var stamp = incident.OccurredAtUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var suffix = SanitizeFileName(incident.Context, 36);
        var baseName = string.IsNullOrEmpty(suffix)
            ? $"CairnMP-crash-{stamp}"
            : $"CairnMP-crash-{stamp}-{suffix}";
        var finalPath = UniquePath(outputDirectory, baseName, ".zip");
        var temporaryPath = finalPath + ".partial";

        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                WriteText(archive, "crash-report.txt", FormatReport(incident));
                WriteText(archive, "README.txt",
                    "This archive was created locally by CairnMP after a fatal mod error.\n" +
                    "It was not uploaded or transmitted automatically. You decide whether to share it.\n" +
                    $"To keep the archive manageable, at most {MaxLogFiles} logs and the last {MaxLogBytes / 1024 / 1024} MiB of each log are included.\n");
                AddLogs(archive, logPaths);
            }

            File.Move(temporaryPath, finalPath);
            return finalPath;
        }
        catch (Exception archiveException)
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception cleanupException)
            {
                archiveException.Data["PartialArchiveCleanupError"] = cleanupException.ToString();
            }
            throw;
        }
    }

    private static void AddLogs(ZipArchive archive, IEnumerable<string> logPaths)
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var omittedLogs = new List<string>();

        var candidates = (logPaths ?? Enumerable.Empty<string>())
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(TryGetFullPath)
            .Where(path => path != null && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(SafeLastWriteTimeUtc)
            .Take(MaxLogFiles);

        foreach (var fullPath in candidates)
        {
            if (!seenPaths.Add(fullPath)) continue;

            try
            {
                var fileName = Path.GetFileName(fullPath);
                var entryName = UniqueEntryName(usedNames, string.IsNullOrWhiteSpace(fileName) ? "log.txt" : fileName);
                var entry = archive.CreateEntry("logs/" + entryName, CompressionLevel.Optimal);
                using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var destination = entry.Open();
                var lengthAtOpen = source.Length;
                var bytesToCopy = Math.Min(lengthAtOpen, MaxLogBytes);
                if (lengthAtOpen > MaxLogBytes)
                {
                    var notice = Encoding.UTF8.GetBytes(
                        $"[truncated by CairnMP: showing the last {MaxLogBytes / 1024 / 1024} MiB]\n");
                    destination.Write(notice, 0, notice.Length);
                    source.Seek(lengthAtOpen - MaxLogBytes, SeekOrigin.Begin);
                }
                CopyBounded(source, destination, bytesToCopy);
            }
            catch (Exception exception)
            {
                omittedLogs.Add($"{fullPath}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        if (omittedLogs.Count > 0)
            WriteText(archive, "logs/collection-errors.txt",
                "Some optional logs could not be added to this archive:\n" + string.Join("\n", omittedLogs));
    }

    internal static void CopyBounded(Stream source, Stream destination, long remaining)
    {
        var buffer = new byte[64 * 1024];
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (read == 0) break;
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static string TryGetFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"CairnMP ignored invalid log path '{path}': {exception}");
            return null;
        }
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"CairnMP could not read log timestamp '{path}': {exception}");
            return DateTime.MinValue;
        }
    }

    private static string FormatReport(CrashIncident incident)
    {
        var builder = new StringBuilder();
        builder.AppendLine("CairnMP fatal error");
        builder.AppendLine("==================");
        builder.AppendLine($"Local time: {incident.OccurredAtUtc.ToLocalTime():O}");
        builder.AppendLine($"UTC time:   {incident.OccurredAtUtc:O}");
        builder.AppendLine($"Mod:        {incident.ModVersion ?? "unknown"}");
        builder.AppendLine($"OS:         {Environment.OSVersion}");
        builder.AppendLine($"Context:    {incident.Context ?? "unknown"}");
        builder.AppendLine($"Exception:  {incident.ExceptionType ?? "unknown"}");
        builder.AppendLine($"Fingerprint:{incident.Fingerprint ?? string.Empty}");
        builder.AppendLine();
        builder.AppendLine("Message");
        builder.AppendLine("-------");
        builder.AppendLine(incident.Message ?? string.Empty);
        builder.AppendLine();
        builder.AppendLine("Stack trace");
        builder.AppendLine("-----------");
        builder.AppendLine(incident.StackTrace ?? string.Empty);
        return builder.ToString();
    }

    private static void WriteText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content ?? string.Empty);
    }

    private static string UniqueEntryName(ISet<string> usedNames, string fileName)
    {
        if (usedNames.Add(fileName)) return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            var candidate = $"{stem}-{index}{extension}";
            if (usedNames.Add(candidate)) return candidate;
        }
    }

    private static string UniquePath(string directory, string baseName, string extension)
    {
        var candidate = Path.Combine(directory, baseName + extension);
        for (var index = 2; File.Exists(candidate) || File.Exists(candidate + ".partial"); index++)
            candidate = Path.Combine(directory, $"{baseName}-{index}{extension}");
        return candidate;
    }

    private static string SanitizeFileName(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
            .ToArray()).Trim('-');
        return cleaned.Length <= maxLength ? cleaned : cleaned.Substring(0, maxLength).TrimEnd('-');
    }
}
