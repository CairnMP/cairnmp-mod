using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MelonLoader;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Reports the mod's unhandled exceptions to the API's /v1/crashes endpoint.
///
/// Hooks installed at startup:
/// - AppDomain.UnhandledException: process-wide exceptions (can crash)
/// - TaskScheduler.UnobservedTaskException: unobserved Task exceptions
///
/// The mod's handlers (NetworkManager, UI, …) can also call ReportException
/// explicitly for caught errors that are worth reporting without crashing
/// the game.
/// </summary>
public static class CrashReporter
{
    private const string SOURCE = "mod";

#if DEBUG
    private const string ApiBase = "http://localhost:8080";
#else
    private const string ApiBase = "https://api.cairnmultiplayer.com";
#endif

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static int _initialized;
    private static readonly object ReportedCaughtLock = new();
    private static readonly HashSet<string> ReportedCaughtSignatures = new();

    public static void Init()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1) return;

        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                ReportException(ex, "AppDomain.UnhandledException");
            }
        };

        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            ReportException(args.Exception, "TaskScheduler.UnobservedTaskException");
            args.SetObserved();
        };
    }

    /// <summary>
    /// Explicit report — call from a catch block when the error is
    /// recoverable but worth tracing (e.g. a malformed network packet).
    /// </summary>
    public static void ReportException(Exception ex, string contextLabel = null)
    {
        if (ex == null) return;
        Report($"{ex.GetType().Name}: {ex.Message}", ex.ToString(), contextLabel);
    }

    public static void ReportCaughtExceptionOnce(Exception ex, string contextLabel)
    {
        if (ex == null) return;

        var signature = $"{contextLabel}\0{ex.GetType().FullName}\0{ex.Message}";
        lock (ReportedCaughtLock)
        {
            if (!ReportedCaughtSignatures.Add(signature))
                return;
        }

        ReportException(ex, contextLabel);
    }

    /// <summary>
    /// Reports an exception captured via the Unity log (logMessageReceived):
    /// we only have strings (condition + stacktrace), not an Exception object.
    /// Deduplicated once per session by signature.
    /// </summary>
    public static void ReportLogExceptionOnce(string condition, string stackTrace, string contextLabel)
    {
        if (string.IsNullOrEmpty(condition)) return;

        var signature = $"{contextLabel}\0{condition}";
        lock (ReportedCaughtLock)
        {
            if (!ReportedCaughtSignatures.Add(signature))
                return;
        }

        var stack = string.IsNullOrEmpty(stackTrace) ? condition : $"{condition}\n{stackTrace}";
        Report(condition, stack, contextLabel);
    }

    /// <summary>
    /// Private core: builds the crash payload and sends it best-effort.
    /// Never rethrows (otherwise it would loop infinitely via the
    /// UnhandledException hook).
    /// </summary>
    private static void Report(string message, string stack, string contextLabel)
    {
        try
        {
            var version = MelonInfoCache.Version;
            var payload = new CrashReport
            {
                Source     = SOURCE,
                Version    = version,
                Os         = $"{Environment.OSVersion.Platform} {Environment.OSVersion.Version}",
                Message    = Truncate(message, 1024),
                Stacktrace = Truncate(stack, 64 * 1024),
                UserHash   = MachineHash.Value,
            };
            if (!string.IsNullOrEmpty(contextLabel))
            {
                payload.Context = JsonSerializer.SerializeToElement(
                    new AnonymousContext { Context = contextLabel },
                    CrashJsonContext.Default.AnonymousContext);
            }

            // Best-effort, fire-and-forget. No await, otherwise we'd block the
            // thread that is currently dying.
            _ = SendAsync(payload);
        }
        catch (Exception sendErr)
        {
            try { Mod.Log.Error($"[CairnMP] crash reporter failed: {sendErr.Message}"); }
            catch { /* Mod.Log may not be initialized yet */ }
        }
    }

    private static async Task SendAsync(CrashReport payload)
    {
        try
        {
            var url = $"{ApiBase}/v1/crashes";
            var json = JsonSerializer.Serialize(payload, CrashJsonContext.Default.CrashReport);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, content);
            // No throw: a 4xx/5xx has no value here, so we ignore it.
        }
        catch
        {
            // best-effort: no network, no API, etc. — we swallow it.
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s.Substring(0, max);
    }
}

internal class CrashReport
{
    [JsonPropertyName("source")]     public string Source { get; set; }
    [JsonPropertyName("version")]    public string Version { get; set; }
    [JsonPropertyName("os")]         public string Os { get; set; }
    [JsonPropertyName("message")]    public string Message { get; set; }
    [JsonPropertyName("stacktrace")] public string Stacktrace { get; set; }
    [JsonPropertyName("user_hash")]  public string UserHash { get; set; }
    [JsonPropertyName("context")]    public JsonElement? Context { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CrashReport))]
[JsonSerializable(typeof(AnonymousContext))]
internal partial class CrashJsonContext : JsonSerializerContext { }

internal class AnonymousContext
{
    [JsonPropertyName("context")] public string Context { get; set; }
}

/// <summary>
/// User identifier derived from the machine name (hashed). Stable enough to
/// deduplicate a recurring user without revealing any personal information.
/// </summary>
internal static class MachineHash
{
    private static readonly Lazy<string> _hash = new(Compute);
    public static string Value => _hash.Value;

    private static string Compute()
    {
        try
        {
            var seed = $"cairnmp-mod\0{Environment.MachineName}\0{Environment.UserName}";
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// Caches the mod version read via reflection from the MelonInfo attribute, to
/// avoid recomputing it on every crash and to avoid depending on a const string
/// that could drift out of sync with MelonInfo.
/// </summary>
internal static class MelonInfoCache
{
    private static readonly Lazy<string> _version = new(() =>
    {
        try
        {
            var attrs = typeof(Mod).Assembly.GetCustomAttributes(typeof(MelonInfoAttribute), false);
            if (attrs.Length > 0 && attrs[0] is MelonInfoAttribute info)
                return info.Version ?? "unknown";
        }
        catch { }
        return "unknown";
    });

    public static string Version => _version.Value;
}
