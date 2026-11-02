using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private const double LocalRouteWaitSeconds = 3;
    private bool _localRoutePending;
    private double _localRouteStarted;
    private string _localRouteWaitReason;
    private float _localRouteFixedLength;
    private BelayProtectionPoint[] _boundStationProtections;
    private uint _localRouteRebinds;

    private void RememberLocalRoute()
    {
        if (_localStation == null || !LocalStationStatus.HasValue) return;
        _localRouteFixedLength = LocalStationStatus.Value.FixedLength;
        _boundStationProtections = _measuredStationProtections;
    }

    private static void BeforeAddingProtection(ClimbingV2PawnController __instance, LogicalQuickdraw __0)
    {
        var owner = _owner;
        if (owner?._ropeControl == null || owner._localStation == null || !owner._localExercise
            || owner._stopping || owner._lifeline?.pawn == null || Id(__instance) != Id(owner._lifeline.pawn)) return;
        try
        {
            if (__0?.Piton == null) return;
            var id = unchecked((uint)__0.Piton.GetInstanceID());
            if (owner._boundStationProtections != null)
                foreach (var point in owner._boundStationProtections)
                    if (point.Id == id) return; // Existing-anchor maintenance is not a new protection.
            if (!owner.TryBeginLocalRouteChange("Native protection attachment", true) && owner._control != null)
                owner.EndLocalRope("A new protection cannot be rebound while falling or suspended, or after station ownership changed.");
        }
        catch (Exception ex) { owner.Fail(ex); }
    }

    private bool TryBeginLocalRouteChange(string reason, bool refreshOldDistance = false)
    {
        if (_localRoutePending) return true;
        if (_ropeControl == null || !_localExercise || _localStation == null || _stopping
            || _boundStationProtections == null || _ropeControl.Suspended || _pivot != 0
            || _control.Attached || !RopeIdentityValid() || _lifeline.pawn == null
            || _lifeline.pawn.IsFalling || !LocalStationMatches(_lifeline, out _)) return false;
        if (refreshOldDistance && MappingMatches() && !StepLocalRope(0, false)) return false;
        // Release the OLD controller before native splitting can turn it into an
        // intermediate part. Never apply its final-segment target to that part again.
        ReleaseLocalRopeGoal();
        _localObiRope = 0;
        _localRoutePending = _rebinding = true;
        _localRouteStarted = Now;
        _localRouteWaitReason = reason;
        _nextTopologyPoll = Now;
        _ropeControl.TrySetMotion(0);
        ModLog.Info("[BelayRope] Route waiting; revision=" + _topology.Revision
            + ", total=" + (_localRouteFixedLength + _ropeControl.Released)
            + ", brake=" + _ropeControl.Braked + ", reason=" + reason);
        return true;
    }

    private bool TickLocalRouteChange()
    {
        if (_lifeline.pawn == null || _lifeline.pawn.IsFalling || _lifeline.SwingHasPivotAndExtremity()
            || _ropeControl.Suspended)
        { EndLocalRope("A fall began while the protection route was updating. Native protection resumed."); return false; }
        if (!LocalStationMatches(_lifeline, out var reason)) { EndLocalRope(reason); return false; }
        if (Now - _localRouteStarted > LocalRouteWaitSeconds)
        { EndLocalRope("The new protection route did not stabilize in time. " + _localRouteWaitReason); return false; }
        if (Now < _nextTopologyPoll) return false;
        _nextTopologyPoll = Now + .1;
        if (!TrySnapshot(_lifeline, out var snapshot, out var snapshotReason))
        { _localRouteWaitReason = snapshotReason; return false; }
        var previous = _topology.Current;
        if (previous.Matches(snapshot))
        {
            _topology.Observe(snapshot, false, Now);
            _localRouteWaitReason = "Waiting for the native protection request to change the holder walk.";
            return false;
        }
        if (!TryMeasureLocalStation(_lifeline, snapshot.Available, out var available, out reason))
        { _localRouteWaitReason = reason; return false; }
        var change = _topology.Observe(snapshot, false, Now);
        if (change == BelayTopologyChange.Invalid)
        { EndLocalRope("The protection route became invalid during rebinding."); return false; }
        if (change != BelayTopologyChange.Rebound)
        { _localRouteWaitReason = "Waiting for stable ordered protection identities."; return false; }
        if (!BelayRoutePlanner.IsSingleProtectionAppend(_boundStationProtections, _measuredStationProtections))
        { EndLocalRope("Only one new protection appended to the existing station route can be rebound. Removal or reordering ends this exercise."); return false; }
        if (!TryReadRopeDistance(_lifeline, out var distance))
        { EndLocalRope("The new protection distance is invalid."); return false; }
        var fixedBefore = _localRouteFixedLength;
        var releasedBefore = _ropeControl.Released;
        var fixedAfter = LocalStationStatus.Value.FixedLength;
        if (!_ropeControl.TryRebindRoute(fixedBefore, fixedAfter, available, distance))
        { EndLocalRope("The paid-out rope cannot cover the new protection with the current brake/capacity. Release and feed before clipping farther."); return false; }
        BindTopology(snapshot);
        _localObiRope = Id(_rope.ropeParts[_rope.ropeParts.Count - 1].obiRope);
        _measuredRopeCapacity = snapshot.Available;
        _restLength = float.NaN; _particleLength = 0;
        _localRoutePending = false;
        _lastValidTopology = Now;
        _nextRopeSample = Now;
        if (_localRouteRebinds < uint.MaxValue) _localRouteRebinds++;
        RememberLocalRoute();
        ModLog.Info("[BelayRope] Route rebound; revision=" + _topology.Revision + ", rebinds=" + _localRouteRebinds
            + ", fixedBefore=" + fixedBefore + ", releasedBefore=" + releasedBefore
            + ", totalBefore=" + (fixedBefore + releasedBefore)
            + ", fixedAfter=" + fixedAfter + ", releasedAfter=" + _ropeControl.Released
            + ", totalAfter=" + (fixedAfter + _ropeControl.Released)
            + ", brake=" + _ropeControl.Braked + ", pitons=" + snapshot.ProtectionCount);
        return true;
    }

    private void RenewLocalIntent()
    {
        if (!_localExercise || _control == null || _stopping || Now < _nextLocalIntent) return;
        _nextLocalIntent = Now + .5;
        if (_localSequence == uint.MaxValue) { Stop(_lease.Session); return; }
        SetBrake(_lease.Session, ++_localSequence, _control.Braked);
    }
}
