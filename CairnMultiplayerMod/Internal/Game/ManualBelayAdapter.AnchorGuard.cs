using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private readonly BelayStationInputGuard<InputAction> _stationClimbInput = new(
        action => action.Pointer.ToInt64(), action => action.enabled,
        action => action.Disable(), action => action.Enable(), action => action.Reset());

    private void InstallStationClimbGuard()
    {
        _harmony.Patch(AccessTools.Method(typeof(ClimbingV2PawnControllerModuleOffBelay),
                nameof(ClimbingV2PawnControllerModuleOffBelay.EarlyUpdate)),
            prefix: new HarmonyMethod(typeof(ManualBelayAdapter), nameof(BeforeStationEarlyUpdate)));
        _harmony.Patch(AccessTools.Method(typeof(ClimbingV2PawnController),
                nameof(ClimbingV2PawnController.TryBackToClimb)),
            prefix: new HarmonyMethod(typeof(ManualBelayAdapter), nameof(BeforeStationBackToClimb)));
    }

    private bool OwnsStationPawn(ClimbingV2PawnController pawn)
        => ReferenceEquals(_stationOwner, this) && _stationSession != Guid.Empty && HasStation
            && _stationLifeline?.pawn != null && Id(pawn) == Id(_stationLifeline.pawn);

    private bool RefreshStationClimbInput()
    {
        if (!ReferenceEquals(_stationOwner, this) || _stationSession == Guid.Empty || !HasStation)
        {
            ReleaseStationClimbInput();
            return true;
        }
        try
        {
            if (!StationValid) { EndStation(); return false; }
            // Native OffBelay.Idle reads grabHoldAction directly before entering
            // ExitOffBelay. Guard this action before the module updates; an E or
            // TryBackToClimb patch alone does not cover that native input path.
            var owned = _stationClimbInput.HasOwnership;
            if (_stationClimbInput.TrySuppress(_stationLifeline.pawn.ClimbingActions.grabHoldAction))
            {
                if (!owned && _stationClimbInput.HasOwnership)
                    ModLog.Info("[BelayStation] Native climb action locked; use L > End belay first; session=" + _stationSession);
                return true;
            }
            _stationInvalidReason = "The belayer's climb input could not be protected.";
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("belay.station-climb-input", exception);
            _stationInvalidReason = "The belayer's climb input guard failed.";
        }
        EndStation();
        return false;
    }

    private void ReleaseStationClimbInput(bool force = false)
    {
        if (!_stationClimbInput.HasOwnership) return;
        try
        {
            // A one-action Disable is not undone by UpdateInputContext alone.
            // Keep its restore pending while L/chat/radial still owns gameplay.
            if (_stationClimbInput.TryRelease(force || (!InputCaptureState.WantsGameplayBlocked && ContextValid)))
                InputInterop.FindInputManager()?.UpdateInputContext();
        }
        catch (Exception exception) { ModLog.SuppressedException("belay.station-climb-release", exception); }
        finally
        {
            // Scene/shutdown teardown cannot retry a destroyed input asset.
            // Restore once, then forget it even if native cleanup failed.
            if (force) _stationClimbInput.DiscardOwnership();
            // Preserve modal maps until their normal teardown.
            if (force && InputCaptureState.IsBelayPanelCaptured)
                InputInterop.ReconcileGameplayInput(true);
        }
    }

    private static void BeforeStationEarlyUpdate(ClimbingV2PawnControllerModuleOffBelay __instance)
    {
        var owner = _stationOwner;
        if (owner == null) return;
        try
        {
            if (owner.OwnsStationPawn(__instance.pawnController)) owner.RefreshStationClimbInput();
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("belay.station-climb-update", exception);
            owner.EndStation();
        }
    }

    private static bool BeforeStationBackToClimb(ClimbingV2PawnController __instance, ref bool __result)
    {
        var owner = _stationOwner;
        if (owner == null) return true;
        try
        {
            if (!owner.OwnsStationPawn(__instance)) return true;
            if (!owner.StationValid) { owner.EndStation(); return true; }
            __result = false;
            return false;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("belay.station-back-to-climb", exception);
            owner.EndStation();
            return true;
        }
    }
}
