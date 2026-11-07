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
/// While a pair is tied, Cairn's <see cref="Harness.LifelineCanGoToward"/> also answers for the
/// rope between the two harnesses. The piton rope's answer is left as Cairn gave it. A falling
/// or dead climber is not held, and this does not call the rope-length HUD.
/// </summary>
internal static class RopeLengthPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.RopeLength");
    private static readonly List<Vector3> PathPoints = new();
    private static readonly List<Vector3> ChainPoints = new();
    private static readonly List<uint> ChainIds = new();
    private static bool _installed;
    private static bool _loggedStop;

    public static void Install()
    {
        if (_installed) return;
        try
        {
            var method = AccessTools.Method(typeof(Harness), nameof(Harness.LifelineCanGoToward),
                             new[] { typeof(UnityEngine.Vector3) })
                         ?? throw new MissingMethodException(nameof(Harness), nameof(Harness.LifelineCanGoToward));
            Harmony.Patch(method, postfix: new HarmonyMethod(typeof(RopeLengthPatch), nameof(ApplyCoil)));
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
        PathPoints.Clear();
        ChainPoints.Clear();
        ChainIds.Clear();
    }

    private static void ApplyCoil(Harness __instance, UnityEngine.Vector3 direction, ref bool __result)
    {
        if (!__result || __instance == null) return;
        try
        {
            if (!Refuses(__instance, direction)) return;
            __result = false;
            if (_loggedStop) return;
            _loggedStop = true;
            ModLog.Info("[RopeTeam] The coil is paid out. A step that would stretch the rope is refused.");
        }
        catch (Exception ex)
        {
            ModLog.Warning("[RopeTeam] Rope length check failed: " + ex.Message);
        }
    }

    private static bool Refuses(Harness harness, UnityEngine.Vector3 direction)
    {
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
            if (!RopeInterop.TryGetHeldCoil(out var heldCoil)) return false;
            return TryBuildHeldPath(harness)
                   && RopeLengthPolicy.RefusesDirection(
                       localIsLeader: false, PathPoints, heldCoil, ToNumerics(direction));
        }

        if (!TeamAnchorSession.IsLocalLeader && !TeamAnchorSession.IsLocalFollower) return false;
        var state = PawnCaptureInterop.GetLocalPawnState();
        if (state == NetFrame.PawnStateType.Falling || state == NetFrame.PawnStateType.Dead) return false;
        if (!RopeInterop.TryGetLiveTeamRope(out var local, out var partner, out var coil)) return false;
        if (local == null || local.Pointer != harness.Pointer) return false;
        return TryBuildPath(local, partner)
               && RopeLengthPolicy.RefusesDirection(
                   TeamAnchorSession.IsLocalLeader, PathPoints, coil, ToNumerics(direction));
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
