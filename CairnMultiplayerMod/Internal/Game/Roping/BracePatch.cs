using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Holds the bracing follower still. Cairn's spike-wall timer and its walking
/// blocked flag do not stop a step on a normal pitch, so the walk, the ledge
/// step, a new reach, and a dyno are skipped instead. A reach already going
/// is left to finish. This does not move the pawn.
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
            // The climb module's virtual body-solve, and the same method on the ledge and
            // hang modules, overflowed the stack at boot. The call came back through
            // il2cpp_runtime_invoke into the same hook, before the menu existed. The walk and
            // the ledge step are still skipped while she is bracing.
            Patch(typeof(ClimbingV2PawnControllerModuleLedgeWalking),
                nameof(ClimbingV2PawnControllerModuleLedgeWalking.UpdateBodyPosition),
                nameof(KeepStepping), null);
            Patch(typeof(LedgeWalkingStates.LedgeWalkingState), "UpdateBodyPosition", nameof(KeepStepping), null);
            // These four sit on the climber, not on the shared climb module. The shared
            // body-solve is virtual and the hook called itself at startup, so a new hold
            // is refused here instead: no reach starts, and a dyno does not start.
            Patch(typeof(ClimbingV2PawnController), nameof(ClimbingV2PawnController.StartReachingWithLimb),
                nameof(KeepStepping), null);
            Patch(typeof(ClimbingV2PawnController), nameof(ClimbingV2PawnController.CanStartReaching),
                nameof(KeepStepping), null);
            Patch(typeof(ClimbingV2PawnController), nameof(ClimbingV2PawnController.InputRequestDynoJump),
                nameof(KeepStepping), null);
            Patch(typeof(ClimbingV2PawnControllerModuleReaching),
                nameof(ClimbingV2PawnControllerModuleReaching.UpdateReachSmoothedInput),
                nameof(KeepStepping), null);
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
}
