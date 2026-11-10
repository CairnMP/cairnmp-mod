using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using Vector3 = System.Numerics.Vector3;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// While a pair is tied, Cairn's own rope questions answer for the rope between the
/// harnesses. <see cref="Harness.LifelineCanGoToward"/> allows a step while that coil
/// still has slack, including when Cairn's own rope is already spent, and refuses a step
/// further out once the coil is used. <see cref="Harness.GetRemainingSecuringRopeDistanceOnLifeline"/>
/// reports the metres still on that coil. Walking does not read that number. It places
/// an invisible wall, and only after a piton gives it a rope direction. For the length
/// of that one walk check, a paid-out team rope is treated as held and supplies a flat
/// direction, so the same wall is placed. That answer is not a belay. A falling or dead
/// climber, and a leader who has left the rope, keep Cairn's answer. This does not call
/// the rope-length HUD.
/// </summary>
internal static class RopeLengthPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.RopeLength");
    private static readonly List<Vector3> PathPoints = new();
    private static readonly List<Vector3> ChainPoints = new();
    private static readonly List<uint> ChainIds = new();
    private static bool _installed;
    private static bool _loggedStop;
    private static bool _holdAnswers;
    private static bool? _lastStep;
    private static float _lastRemaining = float.NaN;
    private static bool _insideWalkCheck;
    private static bool _walkHasDirection;
    private static UnityEngine.Vector3 _walkDirection;
    private static bool _refusedStep;
    private static UnityEngine.Vector3 _refusedDirection;

    /// <summary>
    /// The local body is moving and the other has not. The two harnesses do not
    /// describe the pitch yet, so the length check keeps its last answer.
    /// </summary>
    internal static void HoldLengthAnswers() => _holdAnswers = true;

    internal static void ReleaseLengthAnswers() => _holdAnswers = false;

    public static void Install()
    {
        if (_installed) return;
        try
        {
            var method = AccessTools.Method(typeof(Harness), nameof(Harness.LifelineCanGoToward),
                             new[] { typeof(UnityEngine.Vector3) })
                         ?? throw new MissingMethodException(nameof(Harness), nameof(Harness.LifelineCanGoToward));
            var remaining = AccessTools.Method(typeof(Harness),
                                nameof(Harness.GetRemainingSecuringRopeDistanceOnLifeline))
                            ?? throw new MissingMethodException(
                                nameof(Harness), nameof(Harness.GetRemainingSecuringRopeDistanceOnLifeline));
            Harmony.Patch(method, postfix: new HarmonyMethod(typeof(RopeLengthPatch), nameof(ApplyCoil)));
            Harmony.Patch(remaining, postfix: new HarmonyMethod(typeof(RopeLengthPatch), nameof(CapRemaining)));
            PatchWalkStop();
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[RopeTeam] Rope length hook unavailable: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
        _loggedStop = false;
        _holdAnswers = false;
        _lastStep = null;
        _lastRemaining = float.NaN;
        _insideWalkCheck = false;
        _walkHasDirection = false;
        _refusedStep = false;
        PathPoints.Clear();
        ChainPoints.Clear();
        ChainIds.Clear();
    }

    /// <summary>
    /// Walking places its stop wall from inside this check. The held answer and the
    /// stand-in direction exist only until the check returns.
    /// </summary>
    private static void PatchWalkStop()
    {
        try
        {
            var walk = AccessTools.Method(typeof(WalkingPawnController), "UpdateRopeMoveLimit")
                       ?? throw new MissingMethodException(nameof(WalkingPawnController), "UpdateRopeMoveLimit");
            var secured = AccessTools.Method(typeof(Harness), nameof(Harness.IsSecured), new[] { typeof(int) })
                          ?? throw new MissingMethodException(nameof(Harness), nameof(Harness.IsSecured));
            var direction = AccessTools.Method(typeof(Harness), nameof(Harness.GetRopeDirectionOnHarness))
                            ?? throw new MissingMethodException(
                                nameof(Harness), nameof(Harness.GetRopeDirectionOnHarness));
            Harmony.Patch(walk,
                prefix: new HarmonyMethod(typeof(RopeLengthPatch), nameof(BeginWalkCheck)),
                finalizer: new HarmonyMethod(typeof(RopeLengthPatch), nameof(EndWalkCheck)));
            Harmony.Patch(secured, prefix: new HarmonyMethod(typeof(RopeLengthPatch), nameof(HoldForWalk)));
            Harmony.Patch(direction, prefix: new HarmonyMethod(typeof(RopeLengthPatch), nameof(DirectionForWalk)));
        }
        catch (Exception ex)
        {
            UnpatchWalkStop();
            ModLog.Warning("[RopeTeam] Walk rope stop unavailable: " + ex.Message);
        }
    }

    private static void UnpatchWalkStop()
    {
        Unpatch(typeof(WalkingPawnController), "UpdateRopeMoveLimit");
        Unpatch(typeof(Harness), nameof(Harness.IsSecured));
        Unpatch(typeof(Harness), nameof(Harness.GetRopeDirectionOnHarness));
        _insideWalkCheck = false;
        _walkHasDirection = false;
    }

    private static void Unpatch(Type type, string name)
    {
        var method = AccessTools.Method(type, name);
        if (method != null) Harmony.Unpatch(method, HarmonyPatchType.All, Harmony.Id);
    }

    private static void BeginWalkCheck()
    {
        _insideWalkCheck = true;
        _walkHasDirection = false;
    }

    /// <summary>The check is over, including when it fails. Do not leave the held answer up.</summary>
    private static Exception EndWalkCheck(Exception __exception)
    {
        _insideWalkCheck = false;
        _walkHasDirection = false;
        return __exception;
    }

    /// <summary>
    /// The walk check returns before its wall unless a piton is holding the harness.
    /// A paid-out team rope counts as held for this call only. Every other caller,
    /// including a fall, still sees real pitons.
    /// </summary>
    private static bool HoldForWalk(Harness __instance, ref bool __result)
    {
        if (!TryPrepareWalkDirection(__instance)) return true;
        __result = true;
        return false;
    }

    /// <summary>
    /// The wall faces this direction. Cairn multiplies it by its own distance, so the
    /// vector is one unit long and flat. Skipping the original keeps its missing-piton
    /// warning from deciding the walk.
    /// </summary>
    private static bool DirectionForWalk(Harness __instance, ref UnityEngine.Vector3 __result, ref bool success)
    {
        if (!TryPrepareWalkDirection(__instance)) return true;
        __result = _walkDirection;
        success = true;
        return false;
    }

    /// <summary>
    /// True when this walk check should stop a further step. A rope with slack, a
    /// vertical pair, a swap that has only moved one body, and any other harness
    /// leave Cairn's own answer in place.
    /// </summary>
    private static bool TryPrepareWalkDirection(Harness harness)
    {
        if (!_insideWalkCheck || _holdAnswers || harness == null) return false;
        if (_walkHasDirection) return true;
        if (!TryTeamSpan(harness, out _, out var coil, out var localIsLeader)) return false;
        var flat = RopeLengthPolicy.WalkDirection(localIsLeader, PathPoints, coil);
        if (flat == null) return false;
        _walkDirection = new UnityEngine.Vector3(flat.Value.X, flat.Value.Y, flat.Value.Z);
        _walkHasDirection = true;
        return true;
    }

    private static void ApplyCoil(Harness __instance, UnityEngine.Vector3 direction, ref bool __result)
    {
        if (__instance == null) return;
        try
        {
            if (!TeamAnchorSession.IsLinked && !TeamAnchorSession.LocalKeepsCatch)
            {
                _lastStep = null;
                _lastRemaining = float.NaN;
                _loggedStop = false;
                _refusedStep = false;
                return;
            }

            if (_holdAnswers)
            {
                var held = RopeLengthPolicy.AnswerForStep(StillOnTeamRope(), null, _lastStep);
                if (held == null)
                {
                    _refusedStep = false;
                    return;
                }

                __result = held.Value;
                RememberStep(held, StepTold(direction, __instance));
                return;
            }

            bool? measured = null;
            var told = StepTold(direction, __instance);
            if (TryTeamSpan(__instance, out _, out var coil, out var localIsLeader))
            {
                var here = localIsLeader ? PathPoints[PathPoints.Count - 1] : PathPoints[0];
                var step = RopeLengthPolicy.StepFromToward(ToNumerics(direction), here);
                told = new UnityEngine.Vector3(step.X, step.Y, step.Z);
                measured = RopeLengthPolicy.StepAllowed(
                    teamSpanKnown: true,
                    teamRefuses: RopeLengthPolicy.RefusesDirection(localIsLeader, PathPoints, coil, step));
                _lastStep = measured;
            }

            var answer = RopeLengthPolicy.AnswerForStep(StillOnTeamRope(), measured, _lastStep);
            if (answer == null)
            {
                _refusedStep = false;
                return;
            }

            RememberStep(answer, told);
            if (answer == true)
            {
                __result = true;
                return;
            }

            __result = false;
            NoteStop();
        }
        catch (Exception ex)
        {
            ModLog.Warning("[RopeTeam] Rope length check failed: " + ex.Message);
        }
    }

    private static void CapRemaining(Harness __instance, ref float __result)
    {
        if (__instance == null) return;
        try
        {
            if (!TeamAnchorSession.IsLinked && !TeamAnchorSession.LocalKeepsCatch)
            {
                _lastStep = null;
                _lastRemaining = float.NaN;
                return;
            }

            if (_holdAnswers)
            {
                var held = RopeLengthPolicy.AnswerForRemaining(StillOnTeamRope(), float.NaN, _lastRemaining);
                if (held == null) return;
                __result = RopeLengthPolicy.ReportedRemaining(__result, held.Value);
                return;
            }

            var measured = float.NaN;
            if (TryTeamSpan(__instance, out var length, out var coil, out _))
            {
                measured = RopeLengthPolicy.RemainingMeters(length, coil);
                if (float.IsFinite(measured)) _lastRemaining = measured;
                if (float.IsFinite(measured) && measured <= 0f) NoteStop();
            }

            var answer = RopeLengthPolicy.AnswerForRemaining(StillOnTeamRope(), measured, _lastRemaining);
            if (answer == null) return;
            __result = RopeLengthPolicy.ReportedRemaining(__result, answer.Value);
        }
        catch (Exception ex)
        {
            ModLog.Warning("[RopeTeam] Rope length check failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Tied, and this climber has not left the rope. A fall or a death leaves Cairn's answer.
    /// </summary>
    private static bool StillOnTeamRope()
    {
        if (!TeamAnchorSession.IsLinked && !TeamAnchorSession.LocalKeepsCatch) return false;
        if (TeamAnchorSession.IsLinked && TeamAnchorSession.LeaderLeft && !TeamAnchorSession.IsLocalFollower)
            return false;
        if (TeamAnchorSession.IsLinked
            && !TeamAnchorSession.LeaderLeft
            && !TeamAnchorSession.IsLocalLeader
            && !TeamAnchorSession.IsLocalFollower)
            return false;
        var state = PawnCaptureInterop.GetLocalPawnState();
        return state != NetFrame.PawnStateType.Falling && state != NetFrame.PawnStateType.Dead;
    }

    private static void NoteStop()
    {
        if (_loggedStop) return;
        _loggedStop = true;
        ModLog.Info("[RopeTeam] The coil is paid out. A step that would stretch the rope is refused.");
    }

    /// <summary>
    /// The tied local climber's path and coil. The leader who has left the rope is not held.
    /// The follower they left behind is measured to the stick.
    /// </summary>
    private static bool TryTeamSpan(Harness harness, out float length, out float coil, out bool localIsLeader)
    {
        length = 0f;
        coil = 0f;
        localIsLeader = false;
        if (!TeamAnchorSession.IsLinked)
        {
            if (!TeamAnchorSession.LocalKeepsCatch)
            {
                _loggedStop = false;
                return false;
            }

            var caught = PawnCaptureInterop.GetLocalPawnState();
            if (caught == NetFrame.PawnStateType.Falling || caught == NetFrame.PawnStateType.Dead)
                return false;
            if (!RopeInterop.TryGetHeldCoil(out coil)) return false;
            if (!TryBuildHeldPath(harness)) return false;
            return RopeLengthPolicy.TryMeasureLength(PathPoints, out length);
        }

        if (TeamAnchorSession.LeaderLeft)
        {
            if (!TeamAnchorSession.IsLocalFollower) return false;
            var stateHeld = PawnCaptureInterop.GetLocalPawnState();
            if (stateHeld == NetFrame.PawnStateType.Falling || stateHeld == NetFrame.PawnStateType.Dead) return false;
            if (!RopeInterop.TryGetHeldCoil(out coil)) return false;
            if (!TryBuildHeldPath(harness)) return false;
            return RopeLengthPolicy.TryMeasureLength(PathPoints, out length);
        }

        if (!TeamAnchorSession.IsLocalLeader && !TeamAnchorSession.IsLocalFollower) return false;
        var state = PawnCaptureInterop.GetLocalPawnState();
        if (state == NetFrame.PawnStateType.Falling || state == NetFrame.PawnStateType.Dead) return false;
        if (!RopeInterop.TryGetLiveTeamRope(out var local, out var partner, out coil)) return false;
        if (local == null || local.Pointer != harness.Pointer) return false;
        if (!TryBuildPath(local, partner)) return false;
        localIsLeader = TeamAnchorSession.IsLocalLeader;
        return RopeLengthPolicy.TryMeasureLength(PathPoints, out length);
    }

    /// <summary>
    /// Metres still on the coil the stop is using, including a follower left on the staff.
    /// False for a fall, a death, and a leader who has left. A swap that has not settled
    /// keeps the last leftover, or stays quiet when there is none yet.
    /// </summary>
    internal static bool TryReadRemaining(out float remaining)
    {
        remaining = float.NaN;
        if (!StillOnTeamRope()) return false;
        // The bodies are mid-swap. A fresh measure reads the rope as spent. Keep the last
        // leftover, and stay quiet until one exists.
        if (_holdAnswers)
        {
            if (!float.IsFinite(_lastRemaining) || _lastRemaining < 0f) return false;
            remaining = _lastRemaining;
            return true;
        }

        if (!RopeInterop.TryGetLocalHarness(out var harness)) return false;
        if (!TryTeamSpan(harness, out var length, out var coil, out _)) return false;
        var measured = RopeLengthPolicy.RemainingMeters(length, coil);
        if (!float.IsFinite(measured)) return false;
        remaining = measured;
        return true;
    }

    /// <summary>
    /// The step the coil stop just refused. The warning plays its shake from this and then forgets it.
    /// </summary>
    internal static bool ConsumeRefusedStep(out UnityEngine.Vector3 direction)
    {
        direction = _refusedDirection;
        if (!_refusedStep) return false;
        _refusedStep = false;
        return true;
    }

    /// <summary>
    /// A refused step is remembered until the warning reads it. A later allowed step in the
    /// same check does not forget it. Leaving the rope does.
    /// </summary>
    private static void RememberStep(bool? answer, UnityEngine.Vector3 direction)
    {
        if (answer != false) return;
        _refusedStep = true;
        _refusedDirection = direction;
    }

    /// <summary>The direction of the step, once a world position has been taken from the harness.</summary>
    private static UnityEngine.Vector3 StepTold(UnityEngine.Vector3 direction, Harness harness)
    {
        var body = harness != null ? harness.transform : null;
        if (body == null) return direction;
        var step = RopeLengthPolicy.StepFromToward(ToNumerics(direction), ToNumerics(body.position));
        return new UnityEngine.Vector3(step.X, step.Y, step.Z);
    }

    private static bool TryBuildPath(Harness local, Harness partner)
    {
        if (!RopeInterop.TryGetHarnessAttachPosition(local, out var localPosition)
            || !RopeInterop.TryGetHarnessAttachPosition(partner, out var partnerPosition))
            return false;

        var localIsLeader = TeamAnchorSession.IsLocalLeader;
        PathPoints.Clear();
        ChainPoints.Clear();
        ChainIds.Clear();
        PathPoints.Add(ToNumerics(localIsLeader ? partnerPosition : localPosition));
        AppendChain();
        PathPoints.Add(ToNumerics(localIsLeader ? localPosition : partnerPosition));
        return true;
    }

    /// <summary>Follower harness through the chain. The last point is the stick, not the leader.</summary>
    private static bool TryBuildHeldPath(Harness local)
    {
        if (!RopeInterop.TryGetHarnessAttachPosition(local, out var localPosition)) return false;
        PathPoints.Clear();
        PathPoints.Add(ToNumerics(localPosition));
        AppendChain();
        return PathPoints.Count >= 2;
    }

    private static void AppendChain()
    {
        ChainPoints.Clear();
        ChainIds.Clear();
        var retired = !TeamAnchorSession.IsLinked
            && !TeamAnchorSession.HasLeftoverCatch
            && TeamAnchorSession.HasRetiredCatch;
        if (!(retired
                ? TeamAnchorSession.CopyRetiredPath(ChainPoints)
                : TeamAnchorSession.CopyPath(ChainPoints)))
            return;
        if (retired)
            TeamAnchorSession.CopyRetiredIds(ChainIds);
        else
            TeamAnchorSession.CopyIds(ChainIds);
        for (var i = 0; i < ChainPoints.Count; i++)
        {
            var id = i < ChainIds.Count ? ChainIds[i] : 0;
            PathPoints.Add(ToNumerics(RopeInterop.ChainDrawPoint(id, ChainPoints[i])));
        }
    }

    private static Vector3 ToNumerics(UnityEngine.Vector3 point) => new(point.x, point.y, point.z);
}
