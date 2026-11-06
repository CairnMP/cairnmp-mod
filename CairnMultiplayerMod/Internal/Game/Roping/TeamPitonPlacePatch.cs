using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// The follower does not plant pitons. The leader plants with Cairn's own tool, so those
/// pitons land on the leader's securing rope in chain order.
/// </summary>
internal static class TeamPitonPlacePatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.TeamPitonPlace");
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        try
        {
            var method = AccessTools.Method(
                typeof(ClimbingV2PawnControllerModulePitonExecution),
                nameof(ClimbingV2PawnControllerModulePitonExecution.CanAddPiton));
            if (method == null)
                throw new MissingMethodException(
                    nameof(ClimbingV2PawnControllerModulePitonExecution), nameof(ClimbingV2PawnControllerModulePitonExecution.CanAddPiton));
            Harmony.Patch(method, prefix: new HarmonyMethod(typeof(TeamPitonPlacePatch), nameof(RefuseWhenFollower)));
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[RopeTeam] Piton placement hook unavailable: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
    }

    private static bool RefuseWhenFollower(
        ref bool __result,
        ref ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason reason)
    {
        if (TeamAnchorSession.AllowsPlacement) return true;
        __result = false;
        reason = ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason.NetplayNotRopeHead;
        return false;
    }
}
