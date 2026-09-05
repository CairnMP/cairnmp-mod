using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CairnMultiplayerMod.Bootstrap;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Diagnostics;

internal sealed class FatalCrashState
{
    public DateTimeOffset OccurredAtUtc { get; init; }
    public DateTimeOffset? ArchiveCompletedAtUtc { get; init; }
    public string Context { get; init; }
    public string Message { get; init; }
    public string ArchivePath { get; init; }
    public string ArchiveError { get; init; }
}

/// <summary>
/// Owns the local fatal-error lifecycle. Nothing in this type uploads diagnostics:
/// recoverable errors are logged locally and fatal errors produce a ZIP chosen by the user.
/// </summary>
internal static class CrashHandler
{
    private const int AutomaticCloseDelaySeconds = 30;
    private const int MaxSessionLogs = 10;
    private const long MaxSessionLogBytes = 20L * 1024 * 1024;
    private const int MaxCrashArchives = 5;
    private const long MaxCrashArchiveBytes = 100L * 1024 * 1024;
    private static readonly object StateLock = new();
    private static readonly object RecoverableLock = new();
    private static readonly HashSet<string> RecoverableSignatures = new();
    private static readonly List<string> LogPaths = [];
    private static Action<string> _info = _ => { };
    private static Action<string> _warning = _ => { };
    private static Action<string> _error = _ => { };
    private static string _crashDirectory;
    private static string _sessionLogPath;
    private static FatalCrashState _fatalState;
    private static int _initialized;
    private static int _fatalTriggered;
    private static int _closeRequested;

    public static bool IsFatal => Volatile.Read(ref _fatalTriggered) != 0;
    public static FatalCrashState State
    {
        get { lock (StateLock) return _fatalState; }
    }

    public static int SecondsUntilAutomaticClose
    {
        get
        {
            var state = State;
            if (state?.ArchiveCompletedAtUtc == null) return AutomaticCloseDelaySeconds;
            var elapsed = (int)(DateTimeOffset.UtcNow - state.ArchiveCompletedAtUtc.Value).TotalSeconds;
            return Math.Max(0, AutomaticCloseDelaySeconds - elapsed);
        }
    }

    public static bool ShouldClose => Volatile.Read(ref _closeRequested) != 0 || SecondsUntilAutomaticClose <= 0;

    public static void Initialize(Action<string> info, Action<string> warning, Action<string> error)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1) return;

        _info = info ?? (_ => { });
        _warning = warning ?? (_ => { });
        _error = error ?? (_ => { });

        var gameRoot = ResolveGameRoot();
        var dataRoot = Path.Combine(gameRoot, "UserData", "CairnMultiplayer");
        _crashDirectory = Path.Combine(dataRoot, "Crashes");
        var logsDirectory = Path.Combine(dataRoot, "Logs");

        try
        {
            Directory.CreateDirectory(logsDirectory);
            _sessionLogPath = Path.Combine(logsDirectory,
                $"CairnMP-session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.AppendAllText(_sessionLogPath,
                $"{DateTimeOffset.Now:O} CairnMP {MelonInfoCache.Version} diagnostic session started. No diagnostics are uploaded.\n");
            DiagnosticRetention.Prune(logsDirectory, "CairnMP-session-*.log",
                MaxSessionLogs, MaxSessionLogBytes, _sessionLogPath, ReportRetentionWarning);
        }
        catch (Exception exception)
        {
            _sessionLogPath = null;
            _warning($"[CairnMP] Could not create the local diagnostic log: {exception.Message}");
        }

        AddLogPath(_sessionLogPath);
        try { AddLogPath(Path.Combine(MelonEnvironment.MelonLoaderDirectory, "Latest.log")); }
        catch (Exception exception) { ReportInternalFailure("resolve the MelonLoader log", exception); }
        try { AddLogPath(Application.consoleLogPath); }
        catch (Exception exception) { ReportInternalFailure("resolve the Unity log", exception); }
        DiscoverMelonLogs();

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _initialized, 0) == 0) return;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
            Fatal(exception, "AppDomain.UnhandledException", buildSynchronously: true);
    }

    private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs args)
    {
        RecordRecoverableExceptionOnce(args.Exception, "TaskScheduler.UnobservedTaskException");
        args.SetObserved();
    }

    public static void RecordRecoverableExceptionOnce(Exception exception, string context)
    {
        if (exception == null || IsFatal) return;
        var signature = $"{context}\0{exception.GetType().FullName}\0{exception.Message}";
        lock (RecoverableLock)
        {
            if (RecoverableSignatures.Count >= 1024 || !RecoverableSignatures.Add(signature)) return;
        }

        AppendSessionLog("RECOVERABLE", context, exception.ToString());
        _warning($"[CairnMP] Recoverable error in {context}: {exception.GetType().Name}: {exception.Message}");
    }

    public static void RecordLogExceptionOnce(string condition, string stackTrace, string context)
    {
        if (string.IsNullOrWhiteSpace(condition) || IsFatal) return;
        var signature = $"{context}\0{condition}";
        lock (RecoverableLock)
        {
            if (RecoverableSignatures.Count >= 1024 || !RecoverableSignatures.Add(signature)) return;
        }

        AppendSessionLog("RECOVERABLE", context,
            string.IsNullOrWhiteSpace(stackTrace) ? condition : condition + Environment.NewLine + stackTrace);
    }

    public static void Fatal(Exception exception, string context)
        => Fatal(exception, context, buildSynchronously: false);

    private static void Fatal(Exception exception, string context, bool buildSynchronously)
    {
        if (exception == null) exception = new InvalidOperationException("Unknown fatal mod error.");
        if (Interlocked.CompareExchange(ref _fatalTriggered, 1, 0) != 0) return;

        var occurredAt = DateTimeOffset.UtcNow;
        var stack = exception.ToString();
        var incident = new CrashIncident
        {
            OccurredAtUtc = occurredAt,
            ModVersion = MelonInfoCache.Version,
            Context = context,
            ExceptionType = exception.GetType().FullName,
            Message = exception.Message,
            StackTrace = stack,
            Fingerprint = CrashFingerprint.Build("fatal", context, exception.GetType().FullName, stack),
        };

        AppendSessionLog("FATAL", context, stack);
        _error($"[CairnMP] Fatal mod error in {context}. Multiplayer is stopping safely.");

        lock (StateLock)
        {
            _fatalState = new FatalCrashState
            {
                OccurredAtUtc = occurredAt,
                Context = context ?? "unknown",
                Message = exception.Message,
            };
        }

        if (buildSynchronously)
            BuildArchive(incident);
        else
            _ = Task.Run(() => BuildArchive(incident));
    }

    private static void BuildArchive(CrashIncident incident)
    {
        string archivePath = null;
        string archiveError = null;
        try
        {
            // Pick up any log file created after initialization as well.
            DiscoverMelonLogs();
            archivePath = CrashArchiveBuilder.Create(_crashDirectory, incident, SnapshotLogPaths());
            DiagnosticRetention.Prune(_crashDirectory, "CairnMP-crash-*.zip",
                MaxCrashArchives, MaxCrashArchiveBytes, archivePath, ReportRetentionWarning);
            _info($"[CairnMP] Crash logs saved locally: {archivePath}");
        }
        catch (Exception archiveException)
        {
            archiveError = archiveException.Message;
            _error($"[CairnMP] Crash archive creation failed: {archiveException.Message}");
        }

        lock (StateLock)
        {
            _fatalState = new FatalCrashState
            {
                OccurredAtUtc = incident.OccurredAtUtc,
                ArchiveCompletedAtUtc = DateTimeOffset.UtcNow,
                Context = incident.Context ?? "unknown",
                Message = incident.Message,
                ArchivePath = archivePath,
                ArchiveError = archiveError,
            };
        }
    }

    public static void RequestClose()
    {
        if (State?.ArchiveCompletedAtUtc != null)
            Interlocked.Exchange(ref _closeRequested, 1);
    }

    private static void AppendSessionLog(string severity, string context, string details)
    {
        var path = _sessionLogPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            BoundedSessionLog.Append(path,
                $"\n{DateTimeOffset.Now:O} [{severity}] {context ?? "unknown"}\n{details ?? string.Empty}\n");
        }
        catch (Exception exception)
        {
            ReportInternalFailure("append to the local session log", exception);
        }
    }

    private static string ResolveGameRoot()
    {
        try
        {
            var melonDirectory = MelonEnvironment.MelonLoaderDirectory;
            var parent = Directory.GetParent(melonDirectory);
            if (parent != null) return parent.FullName;
        }
        catch (Exception exception)
        {
            ReportInternalFailure("resolve the game directory", exception);
        }
        return AppContext.BaseDirectory;
    }

    private static void DiscoverMelonLogs()
    {
        try
        {
            var directories = new[]
            {
                MelonEnvironment.MelonLoaderDirectory,
                Path.Combine(MelonEnvironment.MelonLoaderDirectory, "Logs"),
            };
            foreach (var directory in directories.Where(Directory.Exists))
                foreach (var path in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
                    AddLogPath(path);
        }
        catch (Exception exception)
        {
            ReportInternalFailure("discover MelonLoader logs", exception);
        }
    }

    private static void AddLogPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (LogPaths)
        {
            if (!LogPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                LogPaths.Add(path);
        }
    }

    private static string[] SnapshotLogPaths()
    {
        lock (LogPaths) return LogPaths.ToArray();
    }

    private static void ReportRetentionWarning(string message)
        => ReportInternalFailure("apply diagnostic retention", new IOException(message));

    private static void ReportInternalFailure(string operation, Exception exception)
    {
        var message = $"[CairnMP] Could not {operation}: {exception.GetType().Name}: {exception.Message}";
        try { _warning(message); }
        catch (Exception loggerException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"{message}{Environment.NewLine}Diagnostic logger also failed: {loggerException}");
        }
    }
}

internal static class MelonInfoCache
{
    private static readonly Lazy<string> CachedVersion = new(() =>
    {
        try
        {
            var attributes = typeof(Mod).Assembly.GetCustomAttributes(typeof(MelonInfoAttribute), false);
            if (attributes.Length > 0 && attributes[0] is MelonInfoAttribute info)
                return info.Version ?? "unknown";
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"CairnMP could not resolve its assembly version: {exception}");
        }
        return "unknown";
    });

    public static string Version => CachedVersion.Value;
}
