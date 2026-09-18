using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>
/// Times the native methods the reverse-engineering audit flagged as repeated work, without
/// changing a single thing about what they do.
///
/// The audit (docs/performance-reverse-audit.md) found linear scans repeated every frame in
/// contextual culling, occlusion cell lookup and streaming zone selection — but it measured
/// nothing, and said so: "Ce sont des travaux répétés observés dans le code, pas des goulots
/// d'étranglement mesurés." Whether any of them costs anything depends entirely on how many
/// areas, cells and zones a real scene holds. This probe answers that question before anyone
/// writes a replacement for game code.
///
/// Off by default. A Harmony hook on a per-frame method is not free, so the probe is itself
/// part of what gets measured: compare a PerformanceDiagnostics capture with it off against
/// one with it on.
/// </summary>
internal static class NativePerformanceProbe
{
    // Long enough to average over real movement, short enough to see a scene change.
    private const double ReportIntervalSeconds = 30;

    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.NativePerformanceProbe");

    private static NativeProbeStats _contextualCulling;
    private static NativeProbeStats _occlusionCell;
    private static NativeProbeStats _streamingZone;

    private static bool _installed;
    private static double _windowStart;
    private static double _nextReportAt;

    internal static bool IsActive => _installed;

    public static void Install()
    {
        if (_installed) return;
        if (!ModConfig.NativeProfiling.Value) return;

        var frequency = Stopwatch.Frequency;
        _contextualCulling = new NativeProbeStats("P1 contextual culling LateUpdate", frequency);
        _occlusionCell = new NativeProbeStats("P2 occlusion FindCellIndex", frequency);
        _streamingZone = new NativeProbeStats("P3 streaming zone search", frequency);

        var patched = 0;
        patched += TryPatch(typeof(ContextualCullingManager), "LateUpdate",
            nameof(ContextualCullingPrefix), nameof(ContextualCullingFinalizer));
        patched += TryPatch(typeof(OcclusionCullingData), "FindCellIndex",
            nameof(OcclusionCellPrefix), nameof(OcclusionCellFinalizer));
        patched += TryPatch(typeof(StreamingManager), "GetBestZoneIndexUsingZoneLimiterData",
            nameof(StreamingZonePrefix), nameof(StreamingZoneFinalizer));

        if (patched == 0)
        {
            ModLog.Warning("[NativeProbe] No native method could be instrumented — probe disabled.");
            return;
        }

        _installed = true;
        _windowStart = Time.realtimeSinceStartupAsDouble;
        _nextReportAt = _windowStart + ReportIntervalSeconds;
        ModLog.Info($"[NativeProbe] Native performance probe active on {patched} method(s). " +
                    "This adds hook overhead — compare against a capture with it off.");
    }

    public static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;

        Report("final");
        try { Harmony.UnpatchSelf(); }
        catch (Exception exception)
        {
            ModLog.Warning($"[NativeProbe] Unpatch failed: {exception.Message}");
        }
    }

    /// <summary>Called from the mod update loop; cheap no-op when the probe is off.</summary>
    public static void Tick()
    {
        if (!_installed) return;

        var now = Time.realtimeSinceStartupAsDouble;
        if (now < _nextReportAt) return;

        Report("window");
        _windowStart = now;
        _nextReportAt = now + ReportIntervalSeconds;
    }

    private static void Report(string kind)
    {
        var elapsed = Time.realtimeSinceStartupAsDouble - _windowStart;
        if (elapsed <= 0) return;

        ModLog.Info($"[NativeProbe] {kind} over {elapsed:F1}s — {_contextualCulling.Describe(elapsed)}");
        ModLog.Info($"[NativeProbe] {kind} over {elapsed:F1}s — {_occlusionCell.Describe(elapsed)}");
        ModLog.Info($"[NativeProbe] {kind} over {elapsed:F1}s — {_streamingZone.Describe(elapsed)}");

        // Not a native method, but the one cost the mod imposes on the game permanently: the
        // game's own Debug.Log calls all cross into our managed callback.
        var logCalls = Il2CppExceptionCapture.UnityLogCallbacks;
        var logMs = Il2CppExceptionCapture.UnityLogTicks * 1000d / Stopwatch.Frequency;
        ModLog.Info($"[NativeProbe] {kind} over {elapsed:F1}s — game Debug.Log through our hook: " +
                    $"{logCalls} calls ({logCalls / elapsed:F0}/s), {logMs:F2} ms inside the callback " +
                    $"({logMs / elapsed:F3} ms/s, marshalling of the two strings NOT included)");
        Il2CppExceptionCapture.ResetUnityLogCounters();

        _contextualCulling.Reset();
        _occlusionCell.Reset();
        _streamingZone.Reset();
    }

    private static int TryPatch(Type target, string methodName, string prefixName, string finalizerName)
    {
        try
        {
            var method = AccessTools.Method(target, methodName);
            if (method == null)
            {
                ModLog.Warning($"[NativeProbe] {target.Name}.{methodName} not found — skipped.");
                return 0;
            }

            Harmony.Patch(method,
                prefix: new HarmonyMethod(Self(prefixName)),
                finalizer: new HarmonyMethod(Self(finalizerName)));
            return 1;
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[NativeProbe] Could not instrument {target.Name}.{methodName}: {exception.Message}");
            return 0;
        }
    }

    private static MethodInfo Self(string name)
        => typeof(NativePerformanceProbe).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

    // A finalizer runs even when the original throws, so a stamp is never left dangling.
    // Timestamps are kept per probe rather than on a stack: these methods are not reentrant,
    // and a plain field costs nothing on a per-frame path.
    private static long _contextualStamp;
    private static long _occlusionStamp;
    private static long _streamingStamp;

    private static void ContextualCullingPrefix() => _contextualStamp = Stopwatch.GetTimestamp();
    private static void ContextualCullingFinalizer()
        => _contextualCulling.Add(Stopwatch.GetTimestamp() - _contextualStamp);

    private static void OcclusionCellPrefix() => _occlusionStamp = Stopwatch.GetTimestamp();
    private static void OcclusionCellFinalizer()
        => _occlusionCell.Add(Stopwatch.GetTimestamp() - _occlusionStamp);

    private static void StreamingZonePrefix() => _streamingStamp = Stopwatch.GetTimestamp();
    private static void StreamingZoneFinalizer()
        => _streamingZone.Add(Stopwatch.GetTimestamp() - _streamingStamp);
}
