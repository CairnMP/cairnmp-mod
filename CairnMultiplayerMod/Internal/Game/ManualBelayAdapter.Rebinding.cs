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
    private long[] _boundProtectionPins;
    private uint _localRouteRebinds;
    private string _localRouteObservation;
    private int _localRouteObservationCount;

    private void RememberLocalRoute()
    {
        if (_localStation == null || !LocalStationStatus.HasValue) return;
        _localRouteFixedLength = LocalStationStatus.Value.FixedLength;
        _boundStationProtections = _measuredStationProtections;
        _boundProtectionPins = ReadProtectionPins();
    }

    private static void BeforeAddingProtection(ClimbingV2PawnController __instance, LogicalQuickdraw __0)
    {
        var owner = _owner;
        if (owner?._lifeline?.pawn == null || Id(__instance) != Id(owner._lifeline.pawn)) return;
        owner.PrepareLocalProtection(__0, "Native pawn protection attachment");
    }

    private static void BeforeAddingRopeProtection(LogicalRope __instance, LogicalQuickdraw __0)
    {
        var owner = _owner;
        if (owner?._rope == null || Id(__instance) != Id(owner._rope)) return;
        owner.PrepareLocalProtection(__0, "Native rope protection attachment");
    }

    private void PrepareLocalProtection(LogicalQuickdraw quickdraw, string reason)
    {
        if (_ropeControl == null || _localStation == null || _stopping) return;
        try
        {
            if (quickdraw?.Piton == null) return;
            var id = unchecked((uint)quickdraw.Piton.GetInstanceID());
            if (_boundStationProtections != null)
                foreach (var point in _boundStationProtections)
                    if (point.Id == id) return; // Existing-anchor maintenance is not a new protection.
            if (!TryBeginLocalRouteChange(reason, true) && _control != null)
                EndLocalRope("A new protection cannot be rebound while falling or suspended, or after station ownership changed.");
        }
        catch (Exception ex) { Fail(ex); }
    }

    private bool TryBeginLocalRouteChange(string reason, bool refreshOldDistance = false)
    {
        if (_localRoutePending) return true;
        if (_ropeControl == null || _localStation == null || _stopping
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
        _localRouteObservation = null;
        _localRouteObservationCount = 0;
        _nextTopologyPoll = Now;
        _ropeControl.TrySetMotion(0);
        _lease.CancelMotion();
        ModLog.Info("[BelayRope] Route waiting; revision=" + _topology.Revision
            + ", total=" + (_localRouteFixedLength + _ropeControl.Released)
            + ", brake=" + _ropeControl.Braked + ", protections=" + ProtectionOrder(_boundStationProtections)
            + ", expectedParts=" + (_topology.Current.Parts.Length + 1)
            + ", expectedHolders=" + (_topology.Current.Holders.Length + 1)
            + ", originalHolderWalk=" + string.Join(",", _topology.Current.Holders)
            + ", originalPins=" + (_boundProtectionPins == null ? "unavailable" : string.Join(",", _boundProtectionPins))
            + ", reason=" + reason);
        LogRouteContext("Waiting: " + reason);
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
        if (!TryMeasureLocalStation(_lifeline, snapshot.Available, out var available, out reason))
        { _localRouteWaitReason = reason; return false; }
        ObserveLocalRouteChange(snapshot);
        var restoring = snapshot.ProtectionCount == previous.ProtectionCount;
        var upstreamRemoval = snapshot.ProtectionCount < previous.ProtectionCount;
        var change = upstreamRemoval
            ? _topology.ObserveUpstreamRemoval(snapshot, _boundStationProtections, _measuredStationProtections,
                _localStationId, _boundProtectionPins, ReadProtectionPins(), false, Now)
            : restoring
            ? _topology.ObserveProtectionRestore(snapshot, _boundStationProtections, _measuredStationProtections,
                SamePins(_boundProtectionPins, ReadProtectionPins()), Now)
            : _topology.ObserveProtectionAppend(snapshot, _boundStationProtections, _measuredStationProtections, Now);
        if (change == BelayTopologyChange.Invalid)
        { EndLocalRope("The protection route became invalid during rebinding."); return false; }
        if (change != BelayTopologyChange.Rebound)
        {
            _localRouteWaitReason = upstreamRemoval
                ? "Waiting for removal before the station with the entire station-to-harness holder, segment and pin walk unchanged."
                : restoring
                ? "Waiting for the original protection, holder and physical pin walks to return after personal-anchor maintenance."
                : "Waiting for a complete single-protection split and stable identities; temporary rope holders must be released.";
            return false;
        }
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
        ModLog.Info("[BelayRope] Route rebound; revision=" + _topology.Revision + ", maintenance=" + restoring
            + ", upstreamRemoval=" + upstreamRemoval + ", rebinds=" + _localRouteRebinds
            + ", fixedBefore=" + fixedBefore + ", releasedBefore=" + releasedBefore
            + ", totalBefore=" + (fixedBefore + releasedBefore)
            + ", fixedAfter=" + fixedAfter + ", releasedAfter=" + _ropeControl.Released
            + ", totalAfter=" + (fixedAfter + _ropeControl.Released)
            + ", brake=" + _ropeControl.Braked + ", pitons=" + snapshot.ProtectionCount);
        return true;
    }

    private long[] ReadProtectionPins()
    {
        var parts = _rope?.ropeParts;
        if (parts == null || parts.Count < 2 || parts.Count > 64) return null;
        var pins = new long[parts.Count * 2];
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (part?.obiRope == null || !part.obiRope.isLoaded || part.pinConstraintHandler == null) return null;
            pins[i * 2] = Id(part.pinConstraintHandler.GetAttachCollider(RopeSide.Begin));
            pins[i * 2 + 1] = Id(part.pinConstraintHandler.GetAttachCollider(RopeSide.End));
            if (pins[i * 2] == 0 || pins[i * 2 + 1] == 0) return null;
        }
        return pins;
    }

    private static bool SamePins(long[] before, long[] after)
    {
        if (before == null || after == null || before.Length != after.Length) return false;
        for (var i = 0; i < before.Length; i++) if (before[i] != after[i]) return false;
        return true;
    }

    private void RenewLocalIntent()
    {
        if (!_localExercise || _control == null || _stopping || Now < _nextLocalIntent) return;
        _nextLocalIntent = Now + .5;
        if (_localSequence == uint.MaxValue) { Stop(_lease.Session, "The local brake-command sequence was exhausted."); return; }
        SetBrake(_lease.Session, ++_localSequence, _control.Braked);
    }

    private void ObserveLocalRouteChange(BelayTopologySnapshot snapshot)
    {
        if (_localRouteObservationCount >= 8) return;
        var observation = "pitons=" + snapshot.ProtectionCount + ", parts=" + snapshot.Parts.Length
            + ", holders=" + snapshot.Holders.Length + ", lastChanged=" + (snapshot.LastPiton != _topology.Current.LastPiton)
            + ", protections=" + ProtectionOrder(_measuredStationProtections)
            + ", holderWalk=" + string.Join(",", snapshot.Holders)
            + ", holderTypes=" + DescribeLiveHolders()
            + ", originalPinsMatch=" + SamePins(_boundProtectionPins, ReadProtectionPins());
        if (observation == _localRouteObservation) return;
        _localRouteObservation = observation;
        _localRouteObservationCount++;
        ModLog.Info("[BelayRope] Route observed; " + observation);
    }

    private static string ProtectionOrder(BelayProtectionPoint[] protections)
        => protections == null ? "none" : string.Join(",", Array.ConvertAll(protections, point => point.Id));

    private string DescribeLiveHolders()
    {
        try { return ReadLiveHolderDescription(); }
        catch (Exception exception)
        {
            ModLog.SuppressedException("belay.describe-route-holders", exception);
            return "not ready";
        }
    }

    private string ReadLiveHolderDescription()
    {
        var holders = _rope?.ropeHolders;
        var count = holders?.Count ?? 0;
        if (count > 66) return "over limit";
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) result.Append(',');
            var holder = holders[i];
            result.Append(i).Append(':');
            if (holder == null) { result.Append("null"); continue; }
            result.Append(holder.TryCast<Il2CppSystem.Object>()?.GetIl2CppType().Name ?? "unknown")
                .Append(':').Append(holder.Pointer.ToInt64());
            var piton = holder.TryCast<LogicalQuickdraw>()?.Piton;
            if (piton != null) result.Append("/piton=").Append(unchecked((uint)piton.GetInstanceID()));
        }
        return result.ToString();
    }

    private void LogRouteContext(string reason)
    {
        try
        {
            if (_lifeline == null || _rope == null || !GameLifecycleService.TryGetRawGameState(out var lifecycle)
                || lifecycle != CairnGameLifecycleState.InGame) return;
            ModLog.Info("[BelayRope] Route context; reason=" + reason
                + ", falling=" + (_lifeline.pawn?.IsFalling == true)
                + ", swing=" + _lifeline.SwingHasPivotAndExtremity()
                + ", boundPivot=" + _pivot + ", livePivot=" + Id(_lifeline.SwingPivotInstance)
                + ", attached=" + (_control?.Attached == true) + ", pending=" + _localRoutePending
                + ", expectedParts=" + (_topology?.Current?.Parts.Length ?? 0)
                + ", parts=" + (_rope.ropeParts?.Count ?? 0)
                + ", expectedHolders=" + (_topology?.Current?.Holders.Length ?? 0)
                + ", holders=" + DescribeLiveHolders()
                + ", pins=" + (ReadProtectionPins() is { } pins ? string.Join(",", pins) : "not ready"));
        }
        catch (Exception exception) { ModLog.SuppressedException("belay.route-context", exception); }
    }
}
