using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Engine-level settings the mod can change on the game's behalf, with the measurement needed
/// to tell whether a change actually helped.
///
/// A full-trace profile of Cairn showed 78% of process time in SwapContext and
/// KeAlertThreadByThreadIdEx — putting threads to sleep and waking them up — identically with
/// and without the mod. That is the signature of a job system spreading small tasks over many
/// workers: Unity sizes its pool from the core count, and on a 32-thread machine the wake-up
/// traffic can cost more than the parallelism returns.
///
/// Unity exposes that pool size, so it can be tried. Whether fewer workers helps depends on
/// the machine and the scene, so this ships OFF and reports frame pacing either way.
/// </summary>
internal static class GameTuning
{
    private static readonly FrameRateMonitor Frames = new();
    private static double _nextReportAt;
    private static bool _applied;
    private static int _originalWorkerCount = -1;

    private const double ReportIntervalSeconds = 10;

    /// <summary>Applies the configured worker count once. A value of 0 leaves Unity alone.</summary>
    public static void Apply()
    {
        if (_applied) return;
        _applied = true;

        var requested = ModConfig.JobWorkerCount.Value;

        try
        {
            _originalWorkerCount = JobsUtility.JobWorkerCount;
            var maximum = JobsUtility.JobWorkerMaximumCount;

            if (requested <= 0)
            {
                ModLog.Info($"[GameTuning] Job workers left as the engine set them: " +
                            $"{_originalWorkerCount} (max {maximum}). Set Debug/JobWorkerCount to try another value.");
                return;
            }

            // Unity rejects a count above its maximum, and 0 would mean "run everything on the
            // main thread", which is not what a tuning experiment should do by accident.
            var clamped = Math.Clamp(requested, 1, maximum);
            JobsUtility.JobWorkerCount = clamped;

            ModLog.Info($"[GameTuning] Job workers {_originalWorkerCount} -> {clamped} (max {maximum})." +
                        (clamped == requested ? "" : $" Requested {requested}, clamped."));
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[GameTuning] Could not read or set the job worker count: {exception.Message}");
        }
    }

    /// <summary>Restores whatever the engine had chosen, so the setting never outlives the mod.</summary>
    public static void Restore()
    {
        if (_originalWorkerCount <= 0) return;
        try { JobsUtility.JobWorkerCount = _originalWorkerCount; }
        catch (Exception exception)
        {
            ModLog.Warning($"[GameTuning] Could not restore the job worker count: {exception.Message}");
        }
        _originalWorkerCount = -1;
    }

    /// <summary>
    /// Samples frame pacing. Always on: comparing two worker counts needs the same measurement
    /// on both sides, and this costs one array write per frame.
    /// </summary>
    public static void Tick()
    {
        Frames.Add(Time.unscaledDeltaTime);

        var now = Time.realtimeSinceStartupAsDouble;
        if (_nextReportAt == 0) { _nextReportAt = now + ReportIntervalSeconds; return; }
        if (now < _nextReportAt) return;
        _nextReportAt = now + ReportIntervalSeconds;

        var workers = _originalWorkerCount <= 0 ? "engine default" : JobsUtility.JobWorkerCount.ToString();
        ModLog.Info($"[GameTuning] workers={workers} — {Frames.Describe()}");
        Frames.Reset();
    }
}
