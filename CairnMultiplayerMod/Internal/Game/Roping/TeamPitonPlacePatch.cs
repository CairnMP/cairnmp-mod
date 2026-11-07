using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// In Rope Team only the tied leader plants pitons, with Cairn's own tool, so they land on
/// the leader's securing rope in chain order. An untied climber does not plant one.
/// Cairn also refuses anyone who is not <see cref="SharedRopeGamemode.IsLocalClientRopeHead"/>.
/// That flag stays false for our tie, so the leader was refused with the same reason as a follower.
/// </summary>
internal static class TeamPitonPlacePatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.TeamPitonPlace");
    private static bool _installed;
    private static bool _loggedOtherRefusal;

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
            Harmony.Patch(method, postfix: new HarmonyMethod(typeof(TeamPitonPlacePatch), nameof(ApplyLeaderRule)));

            var head = AccessTools.Method(typeof(SharedRopeGamemode), nameof(SharedRopeGamemode.IsLocalClientRopeHead))
                       ?? throw new MissingMethodException(nameof(SharedRopeGamemode), nameof(SharedRopeGamemode.IsLocalClientRopeHead));
            Harmony.Patch(head, prefix: new HarmonyMethod(typeof(TeamPitonPlacePatch), nameof(LeaderIsRopeHead)));
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
        _loggedOtherRefusal = false;
    }

    /// <summary>Cairn asks this before it will arm a piton. Our tied leader is that climber.</summary>
    private static bool LeaderIsRopeHead(ref bool __result)
    {
        if (!TeamAnchorSession.IsLocalLeader || TeamAnchorSession.LeaderLeft) return true;
        __result = true;
        return false;
    }

    private static void ApplyLeaderRule(
        ref bool __result,
        ref ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason reason)
    {
        if (!TeamAnchorSession.AllowsPlacement)
        {
            __result = false;
            reason = ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason.NetplayNotRopeHead;
            return;
        }

        if (__result) return;
        if (reason == ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason.NetplayNotRopeHead)
        {
            __result = true;
            reason = ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason.Usable;
            return;
        }

        if (_loggedOtherRefusal) return;
        _loggedOtherRefusal = true;
        ModLog.Info("[Piton] The leader was refused a piton: " + reason);
    }
}
