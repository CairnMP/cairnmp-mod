using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Holds the bracing follower still. Cairn's spike-wall timer and its walking
/// blocked flag do not stop a step on a normal pitch, so the walk and the climb
/// body move are skipped instead. This does not move the pawn.
/// </summary>
internal static class BracePatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.RopeBrace");
    private static bool _installed;
    private static bool _deadZoneRaised;
    private static float _savedDeadZone;

    public static void Install()
    {
        if (_installed) return;
        try
        {
            Patch(typeof(WalkingPawnController), nameof(WalkingPawnController.Update),
                nameof(WidenDeadZone), nameof(RestoreDeadZone));
            Patch(typeof(WalkingPawnController), "MoveCapsule", nameof(KeepStepping), null);
            Patch(typeof(ClimbingV2PawnControllerModule), nameof(ClimbingV2PawnControllerModule.ShouldSolveBodyMove),
                null, nameof(HoldTheBody));
            Patch(typeof(ClimbingV2PawnControllerModuleLedgeWalking),
                nameof(ClimbingV2PawnControllerModuleLedgeWalking.ShouldSolveBodyMove),
                null, nameof(HoldTheBody));
            Patch(typeof(ClimbingV2PawnControllerModuleHangIdle),
                nameof(ClimbingV2PawnControllerModuleHangIdle.ShouldSolveBodyMove),
                null, nameof(HoldTheBody));
            Patch(typeof(ClimbingV2PawnControllerModuleLedgeWalking),
                nameof(ClimbingV2PawnControllerModuleLedgeWalking.UpdateBodyPosition),
                nameof(KeepStepping), null);
            Patch(typeof(LedgeWalkingStates.LedgeWalkingState), "UpdateBodyPosition", nameof(KeepStepping), null);
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[RopeTeam] Brace hook unavailable: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
        if (!_deadZoneRaised) return;
        _deadZoneRaised = false;
        WalkingPawnController.walkInputSqrDeadZone = _savedDeadZone;
    }

    private static void Patch(Type type, string name, string prefix, string postfix)
    {
        var method = AccessTools.Method(type, name)
                     ?? throw new MissingMethodException(type.Name, name);
        Harmony.Patch(method,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(BracePatch), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(BracePatch), postfix));
    }

    /// <summary>False skips the move while this climber is bracing.</summary>
    private static bool KeepStepping() => !RopeInterop.IsBracing;

    private static void WidenDeadZone(out float __state)
    {
        __state = WalkingPawnController.walkInputSqrDeadZone;
        if (!RopeInterop.IsBracing) return;
        if (!_deadZoneRaised) _savedDeadZone = __state;
        _deadZoneRaised = true;
        WalkingPawnController.walkInputSqrDeadZone = float.MaxValue;
    }

    private static void RestoreDeadZone(float __state)
    {
        if (!_deadZoneRaised) return;
        _deadZoneRaised = false;
        WalkingPawnController.walkInputSqrDeadZone = __state;
    }

    private static void HoldTheBody(ref bool __result)
    {
        if (RopeInterop.IsBracing) __result = false;
    }
}
