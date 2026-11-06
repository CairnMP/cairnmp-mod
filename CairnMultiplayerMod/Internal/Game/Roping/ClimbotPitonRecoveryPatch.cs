using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// In a Rope Team session, Climbot no longer walks the route to collect pitons. Recall still
/// brings the bot back; the rope sweep that recall starts does not. A player's take calls
/// <see cref="RobotPawnController.RecoveringQuickdraw.RecoverQuickDraw(Il2Cpp.LogicalQuickdraw, bool)"/>,
/// so that one method stays open. Repairing pitons in the bivouac is <see cref="RobotPawnController.Repair"/>
/// and the repair submenu; neither is patched, and neither is <see cref="RobotPawnController.OnEnterBivouac"/>.
/// </summary>
internal static class ClimbotPitonRecoveryPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.ClimbotPitonRecovery");
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        try
        {
            var robot = typeof(RobotPawnController);
            Skip(robot, nameof(RobotPawnController.RecoverQuickDraw));
            Skip(robot, nameof(RobotPawnController.RecoverOldestPiton_Performed));
            Skip(robot, nameof(RobotPawnController.UpdateAutoRecoverQuickDraw));
            Skip(robot, nameof(RobotPawnController.RecoverPendingQuickDraw));
            Skip(robot, nameof(RobotPawnController.UpdateAutoRecoverPendingQuickDraw));
            Skip(robot, nameof(RobotPawnController.ForceRecoverPendingQuickdraw));
            Skip(robot, nameof(RobotPawnController.ClearQuickDrawInstant));
            Skip(robot, nameof(RobotPawnController.TriggerQuickDrawRecoverFromDC));
            var sweep = typeof(RobotPawnController.RecoveringQuickdraw);
            Skip(sweep, nameof(RobotPawnController.RecoveringQuickdraw.RecoverQuickDrawOnRope));
            Skip(sweep, nameof(RobotPawnController.RecoveringQuickdraw.RecoverPendingQuickdraw));
            Skip(sweep, nameof(RobotPawnController.RecoveringQuickdraw.RecoverRope));
            Skip(sweep, nameof(RobotPawnController.RecoveringQuickdraw.RecoverQuickDrawOnRopeCoroutine));
            Skip(sweep, nameof(RobotPawnController.RecoveringQuickdraw.RecoverPendingQuickdrawCoroutine));
            var auto = AccessTools.Method(robot, nameof(RobotPawnController.ShouldTriggerAutoRecover))
                       ?? throw new MissingMethodException(robot.Name, nameof(RobotPawnController.ShouldTriggerAutoRecover));
            Harmony.Patch(auto, prefix: new HarmonyMethod(typeof(ClimbotPitonRecoveryPatch), nameof(RefuseAutoRecover)));
            var status = AccessTools.Method(robot, nameof(RobotPawnController.GetClimbotStatus))
                         ?? throw new MissingMethodException(robot.Name, nameof(RobotPawnController.GetClimbotStatus));
            Harmony.Patch(status, postfix: new HarmonyMethod(typeof(ClimbotPitonRecoveryPatch), nameof(HideOldestPitonPrompt)));
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[Piton] Climbot recovery hook unavailable: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
        PitonRecoverySession.Hide();
    }

    private static void Skip(Type type, string method)
    {
        var target = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.Name, method);
        Harmony.Patch(target, prefix: new HarmonyMethod(typeof(ClimbotPitonRecoveryPatch), nameof(SkipWhenRopeTeam)));
    }

    private static bool SkipWhenRopeTeam() => !PitonRecoverySession.SuppressesClimbot;

    private static bool RefuseAutoRecover(ref bool __result)
    {
        if (!PitonRecoverySession.SuppressesClimbot) return true;
        __result = false;
        return false;
    }

    /// <summary>The oldest-piton prompt asks Climbot to fetch. With collection refused, leave
    /// the bot on an ordinary status so the prompt does not sit there doing nothing.</summary>
    private static void HideOldestPitonPrompt(ref ClimbotStatus __result)
    {
        if (PitonRecoverySession.SuppressesClimbot && __result == ClimbotStatus.ReadyToRecoverOldestPiton)
            __result = ClimbotStatus.Ready;
    }
}
