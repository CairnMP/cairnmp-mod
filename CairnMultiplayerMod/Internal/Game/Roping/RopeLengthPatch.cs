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
/// While a pair is tied, two of Cairn's own rope questions answer for the rope between
/// the harnesses. <see cref="Harness.LifelineCanGoToward"/> allows a step while that coil
/// still has slack, including when Cairn's own rope is already spent, and refuses a step
/// further out once the coil is used. <see cref="Harness.GetRemainingSecuringRopeDistanceOnLifeline"/>
/// reports the metres still on that coil, which is what walking spends. A falling or dead
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
    private static bool? _lastStep;
    private static float _lastRemaining = float.NaN;

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
        _lastStep = null;
        _lastRemaining = float.NaN;
        PathPoints.Clear();
        ChainPoints.Clear();
        ChainIds.Clear();
    }

    private static void ApplyCoil(Harness __instance, UnityEngine.Vector3 direction, ref bool __result)
    {
        if (__instance == null) return;
        try
        {
            if (!TeamAnchorSession.IsLinked)
            {
                _lastStep = null;
                _lastRemaining = float.NaN;
                _loggedStop = false;
                return;
            }

            bool? measured = null;
            if (TryTeamSpan(__instance, out _, out var coil, out var localIsLeader))
            {
                var here = localIsLeader ? PathPoints[PathPoints.Count - 1] : PathPoints[0];
                var step = RopeLengthPolicy.StepFromToward(ToNumerics(direction), here);
                measured = RopeLengthPolicy.StepAllowed(
                    teamSpanKnown: true,
                    teamRefuses: RopeLengthPolicy.RefusesDirection(localIsLeader, PathPoints, coil, step));
                _lastStep = measured;
            }

            var answer = RopeLengthPolicy.AnswerForStep(StillOnTeamRope(), measured, _lastStep);
            if (answer == null) return;
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
            if (!TeamAnchorSession.IsLinked)
            {
                _lastStep = null;
                _lastRemaining = float.NaN;
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
        if (!TeamAnchorSession.IsLinked) return false;
        if (TeamAnchorSession.LeaderLeft && !TeamAnchorSession.IsLocalFollower) return false;
        if (!TeamAnchorSession.LeaderLeft
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
            _loggedStop = false;
            return false;
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

    /// <summary>The path length and coil the rope check is using. False when the pair is not tied.</summary>
    internal static bool TryRead(out float pathLength, out float coilLength)
    {
        pathLength = 0f;
        coilLength = 0f;
        if (!TeamAnchorSession.IsLinked) return false;
        if (!TeamAnchorSession.IsLocalLeader && !TeamAnchorSession.IsLocalFollower) return false;
        if (!RopeInterop.TryGetLiveTeamRope(out var local, out var partner, out coilLength)) return false;
        if (!TryBuildPath(local, partner)) return false;
        if (!RopeLengthPolicy.TryMeasureLength(PathPoints, out pathLength)) return false;
        return true;
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
        if (!TeamAnchorSession.CopyPath(ChainPoints)) return;
        TeamAnchorSession.CopyIds(ChainIds);
        for (var i = 0; i < ChainPoints.Count; i++)
        {
            var id = i < ChainIds.Count ? ChainIds[i] : 0;
            PathPoints.Add(ToNumerics(RopeInterop.ChainDrawPoint(id, ChainPoints[i])));
        }
    }

    private static Vector3 ToNumerics(UnityEngine.Vector3 point) => new(point.x, point.y, point.z);
}
