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
/// Remontée des exceptions non gérées du mod vers /v1/crashes de l'API.
///
/// Hooks installés au démarrage :
/// - AppDomain.UnhandledException : exceptions process-wide (peuvent crash)
/// - TaskScheduler.UnobservedTaskException : exceptions de Task non observées
///
/// Les handlers du mod (NetworkManager, UI, …) peuvent aussi appeler
/// ReportException explicitement pour les erreurs catchées qui méritent
/// remontée sans crasher le jeu.
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
    /// Remontée explicite — appeler depuis un catch quand l'erreur est
    /// récupérable mais mérite traçabilité (ex. paquet réseau malformé).
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
    /// Remontée d'une exception captée via le log Unity (logMessageReceived) :
    /// on ne dispose que de chaînes (condition + stacktrace), pas d'objet
    /// Exception. Dédupliquée une fois par session par signature.
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
    /// Cœur privé : construit le payload de crash et l'envoie best-effort.
    /// Ne relance jamais d'exception (sinon boucle infinie via le hook
    /// UnhandledException).
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

            // Best-effort, fire-and-forget. Pas de await sinon on bloque le
            // thread qui est en train de mourir.
            _ = SendAsync(payload);
        }
        catch (Exception sendErr)
        {
            try { Mod.Log.Error($"[CairnMP] crash reporter failed: {sendErr.Message}"); }
            catch { /* Mod.Log peut ne pas être initialisé */ }
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
            // Pas de throw : un 4xx/5xx n'a aucune valeur ici, on l'ignore.
        }
        catch
        {
            // best-effort : pas de réseau, pas d'API, etc. — on absorbe.
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
/// Identifiant utilisateur dérivé du nom de machine (hashé). Stable pour
/// dédupliquer un user récurrent sans révéler d'information personnelle.
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
/// Cache la version du mod lue via reflexion sur l'attribut MelonInfo, pour
/// éviter de la recalculer à chaque crash et pour ne pas dépendre d'un
/// const string désynchronisé du MelonInfo.
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
