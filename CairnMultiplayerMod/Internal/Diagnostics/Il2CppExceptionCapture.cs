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
/// Trampoline and Unity exceptions travel through different channels, so both are hooked and
/// share a namespace filter and flood cap.
/// </summary>
internal static class Il2CppExceptionCapture
{
    private const string ModNamespaceMarker = "CairnMultiplayer";

    // Per-signature deduplication alone cannot bound a flood of varied failures.
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

    // Every Debug.Log the GAME makes comes through here, and Cairn logs a great deal. Even
    // returning immediately, the two string arguments have already been marshalled from
    // IL2CPP — a stack trace is not cheap to hand across that boundary. Counting the calls
    // is what says whether this hook is affordable; an increment is all it costs.
    private static long _unityLogCallbacks;
    private static long _unityLogTicks;

    internal static long UnityLogCallbacks => _unityLogCallbacks;
    internal static long UnityLogTicks => _unityLogTicks;

    internal static void ResetUnityLogCounters()
    {
        _unityLogCallbacks = 0;
        _unityLogTicks = 0;
    }

    private static void OnUnityLog(string condition, string stackTrace, LogType type)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _unityLogCallbacks++;
        try
        {
            if (type != LogType.Exception) return;

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
        finally
        {
            _unityLogTicks += System.Diagnostics.Stopwatch.GetTimestamp() - startedAt;
        }
    }

    private static bool ContainsModMarker(string s)
        => !string.IsNullOrEmpty(s) && s.Contains(ModNamespaceMarker, StringComparison.Ordinal);

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
