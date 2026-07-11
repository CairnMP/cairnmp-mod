using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Route les vraies exceptions du moteur/interop IL2CPP vers <see cref="CrashReporter"/>,
/// pour qu'elles remontent dans la console admin au lieu d'etre simplement loggees.
///
/// Deux sources :
/// - 2A : patch Harmony sur Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher.ReportException,
///   qui voit passer les exceptions des trampolines natif->managed (nos patches/callbacks qui throw).
/// - 2C : abonnement a UnityEngine.Application.logMessageReceived, filtre sur les
///   LogType.Exception dont la condition/stack mentionne notre namespace (pour ne pas
///   remonter les exceptions internes du jeu de base).
///
/// Anti-spam : un garde-fou partage plafonne le nombre de signatures distinctes par
/// session ; la deduplication fine reste faite par CrashReporter.
/// </summary>
public static class Il2CppExceptionCapture
{
    // Marqueur de namespace : seules les exceptions Unity dont la condition/stack
    // mentionne le mod sont remontees (sinon = exception du jeu de base).
    private const string ModNamespaceMarker = "CairnMultiplayer";

    // Garde-fou partage 2A+2C : au-dela de cette limite de signatures distinctes,
    // on cesse de reporter pour la session (protege d'un flood de signatures variees
    // que la deduplication par-signature ne couvre pas).
    private const int MaxDistinctPerSession = 25;

    private static readonly object GateLock = new();
    private static readonly HashSet<string> SeenSignatures = new();
    private static bool _capLogged;
    private static int _installed;

    // Conserve la reference du delegue Unity converti pour empecher sa collecte GC.
    private static Application.LogCallback _logCallback;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        InstallTrampolinePatch();
        InstallUnityLogHook();
    }

    // 2A — patch du reporter d'exception des trampolines Il2CppInterop.
    private static void InstallTrampolinePatch()
    {
        try
        {
            var detourType = ResolveDetourMethodPatcherType();
            if (detourType == null)
            {
                Mod.Log.Warning("[CairnMP] Il2Cpp exception capture: Il2CppDetourMethodPatcher not found, trampoline capture disabled.");
                return;
            }

            var reportException = detourType.GetMethod("ReportException",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (reportException == null)
            {
                Mod.Log.Warning("[CairnMP] Il2Cpp exception capture: ReportException method not found, trampoline capture disabled.");
                return;
            }

            var prefix = typeof(Il2CppExceptionCapture).GetMethod(
                nameof(OnTrampolineException), BindingFlags.NonPublic | BindingFlags.Static);

            // Instance Harmony dediee. Le prefix est non-bloquant (void) : il coexiste
            // avec celui de cairnloader (qui, lui, skippe l'original).
            var harmony = new HarmonyLib.Harmony("CairnMultiplayerMod.Il2CppExceptionCapture");
            harmony.Patch(reportException, prefix: new HarmonyMethod(prefix));
            Mod.Log.Msg("[CairnMP] Il2Cpp exception capture: trampoline hook installed.");
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnMP] Il2Cpp exception capture: trampoline hook failed: {ex.Message}");
        }
    }

    // Prefix non-bloquant : observe l'exception sans modifier le flux du patcher.
    // __0 = premier parametre de ReportException(Exception).
    private static void OnTrampolineException(Exception __0)
    {
        if (__0 == null) return;
        try
        {
            var signature = $"Il2CppInterop\0{__0.GetType().FullName}\0{__0.Message}";
            if (PassesGate(signature))
                CrashReporter.ReportCaughtExceptionOnce(__0, "Il2CppInterop");
        }
        catch { /* ne jamais relancer depuis le hook */ }
    }

    // 2C — abonnement aux logs Unity (exceptions cote moteur/MonoBehaviour/coroutines).
    private static void InstallUnityLogHook()
    {
        try
        {
            _logCallback = DelegateSupport.ConvertDelegate<Application.LogCallback>(
                new Action<string, string, LogType>(OnUnityLog));
            Application.add_logMessageReceived(_logCallback);
            Mod.Log.Msg("[CairnMP] Il2Cpp exception capture: Unity log hook installed.");
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnMP] Il2Cpp exception capture: Unity log hook failed: {ex.Message}");
        }
    }

    private static void OnUnityLog(string condition, string stackTrace, LogType type)
    {
        try
        {
            // Garde 1 : uniquement les vraies exceptions (Error/Warning trop bruyants).
            if (type != LogType.Exception) return;

            // Garde 2 : uniquement ce que le mod a cause (filtre namespace).
            if (!ContainsModMarker(condition) && !ContainsModMarker(stackTrace)) return;

            var signature = $"UnityException\0{condition}";
            if (PassesGate(signature))
                CrashReporter.ReportLogExceptionOnce(condition, stackTrace, "UnityException");
        }
        catch { /* ne jamais relancer depuis le handler de log (risque de boucle) */ }
    }

    private static bool ContainsModMarker(string s)
        => !string.IsNullOrEmpty(s) && s.IndexOf(ModNamespaceMarker, StringComparison.Ordinal) >= 0;

    // Garde-fou partage 2A+2C : ignore une signature deja vue (CrashReporter
    // dedupliquerait de toute facon) et plafonne le nombre de signatures distinctes.
    private static bool PassesGate(string signature)
    {
        lock (GateLock)
        {
            if (SeenSignatures.Contains(signature)) return false;
            if (SeenSignatures.Count >= MaxDistinctPerSession)
            {
                if (!_capLogged)
                {
                    _capLogged = true;
                    Mod.Log.Warning("[CairnMP] Il2Cpp exception capture: per-session cap reached, further distinct exceptions suppressed.");
                }
                return false;
            }
            SeenSignatures.Add(signature);
            return true;
        }
    }

    // Resout le type interne Il2CppDetourMethodPatcher depuis l'assembly deja chargee
    // par le loader, sans ajouter de reference csproj a Il2CppInterop.HarmonySupport.
    private static Type ResolveDetourMethodPatcherType()
    {
        const string fullName = "Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher";
        var t = Type.GetType($"{fullName}, Il2CppInterop.HarmonySupport");
        if (t != null) return t;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                t = asm.GetType(fullName);
                if (t != null) return t;
            }
            catch { /* certains assemblies refusent GetType — on ignore */ }
        }
        return null;
    }
}
