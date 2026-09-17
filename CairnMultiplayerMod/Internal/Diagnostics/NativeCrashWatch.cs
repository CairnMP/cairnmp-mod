using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>
/// Detects sessions that died without the mod ever hearing about it.
///
/// An access violation in UnityPlayer.dll or GameAssembly.dll kills the process outright: it
/// raises no <see cref="Exception"/>, so <see cref="CrashHandler"/>, RunGuarded and every
/// try/catch in the mod stay silent, and the logs simply stop mid-line. On .NET 6+ such
/// corrupted-state exceptions cannot be caught at all, so the only workable approach is to
/// leave a marker behind and read it on the next launch.
///
/// A marker file is written at startup, kept up to date with a short breadcrumb of what the
/// mod was doing, and deleted on a clean shutdown. Finding one at startup therefore means the
/// previous session was killed — and the marker says where it was when it happened.
/// </summary>
internal static class NativeCrashWatch
{
    private const string MarkerFileName = "session-active.txt";

    private static string _markerPath;
    private static string _breadcrumb;
    private static DateTime _sessionStartUtc;
    private static bool _enabled;

    /// <summary>
    /// Reads the previous session's marker (if any), then arms the marker for this session.
    /// Returns the report for the previous crash, or null when the last session ended cleanly.
    /// </summary>
    public static string InitializeAndCollectPreviousReport(string diagnosticsDirectory)
    {
        string report = null;
        try
        {
            Directory.CreateDirectory(diagnosticsDirectory);
            _markerPath = Path.Combine(diagnosticsDirectory, MarkerFileName);

            if (File.Exists(_markerPath))
            {
                report = BuildReport(File.ReadAllText(_markerPath));
                File.Delete(_markerPath);
            }

            _sessionStartUtc = DateTime.UtcNow;
            _enabled = true;
            Write("starting up");
        }
        catch (Exception exception)
        {
            _enabled = false;
            System.Diagnostics.Debug.WriteLine($"CairnMP could not arm the native-crash marker: {exception}");
        }
        return report;
    }

    /// <summary>
    /// Records what the mod is currently doing. Only a change is written, so callers must pass
    /// a value that tracks coarse activity (state, scene) rather than one that moves every
    /// frame. No time-based throttle on purpose: the marker exists to hold the *last* known
    /// activity, and dropping an update would defeat that.
    /// </summary>
    public static void SetBreadcrumb(string breadcrumb)
    {
        if (!_enabled || string.IsNullOrEmpty(breadcrumb)) return;
        if (string.Equals(_breadcrumb, breadcrumb, StringComparison.Ordinal)) return;

        Write(breadcrumb);
    }

    /// <summary>Marks this session as ended normally, so the next launch reports nothing.</summary>
    public static void Disarm()
    {
        if (!_enabled) return;
        _enabled = false;
        try
        {
            if (_markerPath != null && File.Exists(_markerPath)) File.Delete(_markerPath);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"CairnMP could not clear the native-crash marker: {exception}");
        }
    }

    private static void Write(string breadcrumb)
    {
        if (_markerPath == null) return;
        try
        {
            var builder = new StringBuilder();
            builder.Append("version=").Append(SafeVersion).Append('\n');
            builder.Append("pid=").Append(CurrentProcessId).Append('\n');
            builder.Append("started=").Append(_sessionStartUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("at=").Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("doing=").Append(breadcrumb).Append('\n');
            File.WriteAllText(_markerPath, builder.ToString());
            _breadcrumb = breadcrumb;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"CairnMP could not update the native-crash marker: {exception}");
        }
    }

    // Resolving the version reaches into MelonLoader attributes, which can fail outside the
    // game. Nothing here is worth losing the marker over.
    private static string SafeVersion
    {
        get
        {
            try { return MelonInfoCache.Version; }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"CairnMP could not resolve its version for the marker: {exception}");
                return "unknown";
            }
        }
    }

    private static int CurrentProcessId
    {
        get
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"CairnMP could not read its process id: {exception}");
                return 0;
            }
        }
    }

    internal static string BuildReport(string markerContents)
        => BuildReport(markerContents, MinidumpReader.DefaultDumpDirectory);

    internal static string BuildReport(string markerContents, string dumpDirectory)
    {
        var fields = ParseMarker(markerContents);
        var builder = new StringBuilder();
        builder.Append("The previous session ended without shutting the mod down — ");
        builder.Append("that is the signature of a native crash, which no try/catch can see.");

        if (fields.TryGetValue("doing", out var doing))
            builder.Append("\n  Last known activity: ").Append(doing);
        if (fields.TryGetValue("at", out var at))
            builder.Append("\n  Last heartbeat: ").Append(at);
        if (fields.TryGetValue("version", out var version))
            builder.Append("\n  Mod version: ").Append(version);

        AppendDumpDetails(builder, fields, dumpDirectory);
        return builder.ToString();
    }

    private static void AppendDumpDetails(StringBuilder builder,
        IReadOnlyDictionary<string, string> fields, string dumpDirectory)
    {
        if (string.IsNullOrWhiteSpace(dumpDirectory)) return;

        var processId = 0;
        if (fields.TryGetValue("pid", out var pidText))
            int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out processId);

        var notBefore = DateTime.MinValue;
        if (fields.TryGetValue("started", out var startedText)
            && DateTime.TryParse(startedText, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var started))
            notBefore = started.ToUniversalTime();

        var dump = MinidumpReader.FindDumpForProcess(dumpDirectory, processId, notBefore);
        if (dump == null)
        {
            builder.Append("\n  No Windows crash dump found for that session.");
            return;
        }

        builder.Append("\n  Crash dump: ").Append(dump);
        if (MinidumpReader.TryRead(dump, out var summary, out var error))
            builder.Append("\n  Faulted with ").Append(summary.Describe());
        else
            builder.Append("\n  Dump could not be read: ").Append(error);
    }

    private static Dictionary<string, string> ParseMarker(string contents)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(contents)) return fields;

        foreach (var rawLine in contents.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ');
            if (line.Length == 0) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            fields[line.Substring(0, separator)] = line.Substring(separator + 1);
        }
        return fields;
    }
}
