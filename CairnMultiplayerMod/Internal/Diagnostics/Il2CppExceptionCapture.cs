using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>
/// Records genuine engine/IL2CPP-interop exceptions in the local diagnostic log.
///
/// Two sources:
/// - 2A: Harmony patch on Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher.ReportException,
///   which sees the exceptions from native->managed trampolines (our patches/callbacks that throw).
/// - 2C: subscription to UnityEngine.Application.logMessageReceived, filtered to
///   LogType.Exception whose condition/stack mentions our namespace (so we don't
///   report base-game internal exceptions).
///
/// Anti-spam: a shared guard caps the number of distinct signatures per session.
/// </summary>
internal static class Il2CppExceptionCapture
{
    // Namespace marker: only Unity exceptions whose condition or stack mentions the
    // mod are reported; all other exceptions are treated as base-game exceptions.
    private const string ModNamespaceMarker = "CairnMultiplayer";

    // Shared 2A+2C guard: beyond this limit of distinct signatures, we stop
    // reporting for the session (protects against a flood of varied signatures
    // that per-signature deduplication wouldn't cover).
    private const int MaxDistinctPerSession = 25;

    private static readonly object GateLock = new();
    private static readonly HashSet<string> SeenSignatures = [];
    private static bool _capLogged;
    private static int _installed;

    // Keeps a reference to the converted Unity delegate to prevent garbage collection.
    private static Application.LogCallback _logCallback;
    private static HarmonyLib.Harmony _harmony;

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
                ModLog.Warning("[CairnMP] Il2Cpp exception capture: Il2CppDetourMethodPatcher not found, trampoline capture disabled.");
                return;
            }

            var reportException = detourType.GetMethod("ReportException",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (reportException == null)
            {
                ModLog.Warning("[CairnMP] Il2Cpp exception capture: ReportException method not found, trampoline capture disabled.");
                return;
            }

            var prefix = typeof(Il2CppExceptionCapture).GetMethod(
                nameof(OnTrampolineException), BindingFlags.NonPublic | BindingFlags.Static);

            // Dedicated Harmony instance. The prefix is non-blocking (void), so it
            // coexists with the loader's prefix, which skips the original method.
            _harmony = new HarmonyLib.Harmony("CairnMultiplayerMod.Il2CppExceptionCapture");
            _harmony.Patch(reportException, prefix: new HarmonyMethod(prefix));
            ModLog.Info("[CairnMP] Il2Cpp exception capture: trampoline hook installed.");
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[CairnMP] Il2Cpp exception capture: trampoline hook failed: {ex.Message}");
        }
    }

    // Non-blocking prefix: observes the exception without altering the patcher's flow.
    // __0 = the first parameter of ReportException(Exception).
    private static void OnTrampolineException(Exception __0)
    {
        if (__0 == null) return;
        try
        {
            var signature = $"Il2CppInterop\0{__0.GetType().FullName}\0{__0.Message}";
            if (PassesGate(signature))
                CrashHandler.RecordRecoverableExceptionOnce(__0, "Il2CppInterop");
        }
        catch (Exception exception)
        {
            // Never feed this failure back into Unity's logger: that would recurse into this hook.
            System.Diagnostics.Debug.WriteLine($"CairnMP trampoline exception hook failed: {exception}");
        }
    }

    // 2C — subscription to Unity logs (engine/MonoBehaviour/coroutine-side exceptions).
    private static void InstallUnityLogHook()
    {
        try
        {
            _logCallback = DelegateSupport.ConvertDelegate<Application.LogCallback>(
                new Action<string, string, LogType>(OnUnityLog));
            Application.add_logMessageReceived(_logCallback);
            ModLog.Info("[CairnMP] Il2Cpp exception capture: Unity log hook installed.");
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[CairnMP] Il2Cpp exception capture: Unity log hook failed: {ex.Message}");
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
                CrashHandler.RecordLogExceptionOnce(condition, stackTrace, "UnityException");
        }
        catch (Exception exception)
        {
            // Never feed this failure back into Unity's logger: that would recurse into this callback.
            System.Diagnostics.Debug.WriteLine($"CairnMP Unity exception callback failed: {exception}");
        }
    }

    private static bool ContainsModMarker(string s)
        => !string.IsNullOrEmpty(s) && s.Contains(ModNamespaceMarker, StringComparison.Ordinal);

    // Shared 2A+2C guard: ignores an already-seen signature and caps the number
    // of distinct signatures written to the local session log.
    private static bool PassesGate(string signature)
    {
        lock (GateLock)
        {
            if (SeenSignatures.Contains(signature)) return false;
            if (SeenSignatures.Count >= MaxDistinctPerSession)
            {
                if (_capLogged) return false;

                _capLogged = true;
                ModLog.Warning("[CairnMP] Il2Cpp exception capture: per-session cap reached, further distinct exceptions suppressed.");
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
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"CairnMP could not inspect assembly '{asm.FullName}' for the IL2CPP patcher: {exception}");
            }
        }
        return null;
    }

    public static void Uninstall()
    {
        if (Interlocked.Exchange(ref _installed, 0) == 0) return;

        try
        {
            if (_logCallback != null)
                Application.remove_logMessageReceived(_logCallback);
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[CairnMP] Il2Cpp exception capture: Unity log hook removal failed: {exception.Message}");
        }
        _logCallback = null;

        try { _harmony?.UnpatchSelf(); }
        catch (Exception exception)
        {
            ModLog.Warning($"[CairnMP] Il2Cpp exception capture: trampoline unpatch failed: {exception.Message}");
        }
        _harmony = null;
    }
}
