using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Diagnostics;

/// <summary>
/// Routes genuine engine/IL2CPP-interop exceptions to <see cref="CrashReporter"/>,
/// so they surface in the admin console instead of merely being logged.
///
/// Two sources:
/// - 2A: Harmony patch on Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher.ReportException,
///   which sees the exceptions from native->managed trampolines (our patches/callbacks that throw).
/// - 2C: subscription to UnityEngine.Application.logMessageReceived, filtered to
///   LogType.Exception whose condition/stack mentions our namespace (so we don't
///   report base-game internal exceptions).
///
/// Anti-spam: a shared guard caps the number of distinct signatures per session;
/// fine-grained deduplication is still handled by CrashReporter.
/// </summary>
public static class Il2CppExceptionCapture
{
    // Namespace marker: only Unity exceptions whose condition/stack mentions the
    // mod are reported (otherwise = base-game exception).
    private const string ModNamespaceMarker = "CairnMultiplayer";

    // Shared 2A+2C guard: beyond this limit of distinct signatures, we stop
    // reporting for the session (protects against a flood of varied signatures
    // that per-signature deduplication wouldn't cover).
    private const int MaxDistinctPerSession = 25;

    private static readonly object GateLock = new();
    private static readonly HashSet<string> SeenSignatures = new();
    private static bool _capLogged;
    private static int _installed;

    // Keeps a reference to the converted Unity delegate to prevent it from being GC'd.
    private static Application.LogCallback _logCallback;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        InstallTrampolinePatch();
        InstallUnityLogHook();
    }

    // 2A — patch of the Il2CppInterop trampolines' exception reporter.
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

            // Dedicated Harmony instance. The prefix is non-blocking (void): it coexists
            // with cairnloader's own (which skips the original).
            var harmony = new HarmonyLib.Harmony("CairnMultiplayerMod.Il2CppExceptionCapture");
            harmony.Patch(reportException, prefix: new HarmonyMethod(prefix));
            Mod.Log.Msg("[CairnMP] Il2Cpp exception capture: trampoline hook installed.");
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnMP] Il2Cpp exception capture: trampoline hook failed: {ex.Message}");
        }
    }

    // Non-blocking prefix: observes the exception without altering the patcher's flow.
    // __0 = first parameter of ReportException(Exception).
    private static void OnTrampolineException(Exception __0)
    {
        if (__0 == null) return;
        try
        {
            var signature = $"Il2CppInterop\0{__0.GetType().FullName}\0{__0.Message}";
            if (PassesGate(signature))
                CrashReporter.ReportCaughtExceptionOnce(__0, "Il2CppInterop");
        }
        catch { /* never rethrow from the hook */ }
    }

    // 2C — subscription to Unity logs (engine/MonoBehaviour/coroutine-side exceptions).
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
            // Guard 1: only genuine exceptions (Error/Warning are too noisy).
            if (type != LogType.Exception) return;

            // Guard 2: only what the mod caused (namespace filter).
            if (!ContainsModMarker(condition) && !ContainsModMarker(stackTrace)) return;

            var signature = $"UnityException\0{condition}";
            if (PassesGate(signature))
                CrashReporter.ReportLogExceptionOnce(condition, stackTrace, "UnityException");
        }
        catch { /* never rethrow from the log handler (loop risk) */ }
    }

    private static bool ContainsModMarker(string s)
        => !string.IsNullOrEmpty(s) && s.IndexOf(ModNamespaceMarker, StringComparison.Ordinal) >= 0;

    // Shared 2A+2C guard: ignores an already-seen signature (CrashReporter
    // would deduplicate anyway) and caps the number of distinct signatures.
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

    // Resolves the internal Il2CppDetourMethodPatcher type from the assembly already
    // loaded by the loader, without adding a csproj reference to Il2CppInterop.HarmonySupport.
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
            catch { /* some assemblies refuse GetType — we ignore them */ }
        }
        return null;
    }
}
