using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private double _ropeInputNoticeUntil, _nextRopeInputLog;

    public string InteractionNotice => _stationSession != Guid.Empty && HasStation
        ? "You are belaying from this station. Use L > End belay before climbing."
        : _control != null && Now < _ropeInputNoticeUntil
            ? "Your belayer controls the protection rope. Grab the wall to continue climbing." : null;

    private bool OwnsClimberRopeInput(ClimbingV2PawnController pawn, LogicalRope overrideRope = null)
        => _control != null && _ropeControl != null && _localStation != null && _lifeline?.pawn != null
            && Id(pawn) == Id(_lifeline.pawn) && _rope != null
            && Id(overrideRope ?? _lifeline.securingRope) == Id(_rope)
            && Id(_lifeline.securingRope) == Id(_rope);

    private bool RopeInputLoaded(ClimbingV2PawnController pawn)
        => pawn.IsFalling || _lifeline.SwingHasPivotAndExtremity()
            || _control.Attached || _ropeControl.Suspended;

    private bool BlockPersonalRopeAction(ClimbingV2PawnController pawn, LogicalRope overrideRope,
        BelayPersonalRopeAction action)
    {
        if (!OwnsClimberRopeInput(pawn, overrideRope) || !ContextValid) return false;
        var current = pawn.CurrentModule;
        var next = pawn.TransitioningToModule;
        var placing = current?.TryCast<ClimbingV2PawnControllerModulePitonExecution>() != null
            || next?.TryCast<ClimbingV2PawnControllerModulePitonExecution>() != null;
        var anchoring = current?.TryCast<ClimbingV2PawnControllerModuleOffBelay>() != null
            || next?.TryCast<ClimbingV2PawnControllerModuleOffBelay>() != null;
        var loaded = RopeInputLoaded(pawn);
        if (!BelayRopeInteractionGuard.ShouldBlock(action, true, _stopping, loaded, placing, anchoring)) return false;
        _ropeInputNoticeUntil = Now + 3;
        if (Now >= _nextRopeInputLog)
        {
            _nextRopeInputLog = Now + 2;
            ModLog.Info("[BelayInput] Blocked personal rope " + action + "; session=" + _lease.Session
                + ", loaded=" + loaded + ", placing=" + placing + ", anchoring=" + anchoring);
        }
        return true;
    }

    private static void BeforeHoldingRopeItem(ClimbingV2PawnController __instance,
        ClimbingV2PawnController.HeldItem item, ref ClimbingV2PawnController.HoldItemContext context)
    {
        if (item != ClimbingV2PawnController.HeldItem.Rope
            || context != ClimbingV2PawnController.HoldItemContext.OffBelay) return;
        var owner = _owner;
        if (owner == null) return;
        try
        {
            if (!owner.OwnsClimberRopeInput(__instance) || !ContextValid) return;
            if (owner._stopping || !owner.RopeInputLoaded(__instance)) return;
            // Native OffBelay item handling adds a physical hand to the securing
            // rope, bypassing GrabSecuringRope. Its Abseil item context retains
            // the same held-item animation without RequestAttach. Only change
            // this call's context: the anchor module and its transition still run.
            context = ClimbingV2PawnController.HoldItemContext.Abseil;
            ModLog.Info("[BelayInput] Anchor rope animation kept without a loaded hand attachment; session=" + owner._lease.Session);
        }
        catch (Exception exception) { owner.Fail(exception); }
    }

    private static bool BeforePersonalRopeGrab(ClimbingV2PawnController __instance, LogicalRope overrideRope)
    {
        var owner = _owner;
        if (owner == null) return true;
        try { return !owner.BlockPersonalRopeAction(__instance, overrideRope, BelayPersonalRopeAction.Grab); }
        catch (Exception exception) { owner.Fail(exception); return true; }
    }

    private static bool BeforePersonalRopeAscend(ClimbingV2PawnController __instance, ref bool __result)
    {
        var owner = _owner;
        if (owner == null) return true;
        try
        {
            if (!owner.BlockPersonalRopeAction(__instance, null, BelayPersonalRopeAction.Ascend)) return true;
            __result = false;
            return false;
        }
        catch (Exception exception) { owner.Fail(exception); return true; }
    }
}
