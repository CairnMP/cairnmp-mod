using System;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private BelayRopeControl _ropeControl;
    private long _localObiRope;
    private float _restLength, _particleLength;
    private double _nextRopeSample;
    private string _localRopeNotice;
    private ObiRopeLengthController _localLengthController;
    private float _lastLocalRopeGoal = float.NaN, _measuredRopeCapacity;
    private uint _ropeRefreshCount;
    private static bool LocalExerciseEnabled => ModConfig.BelayRouteExperiment?.Value == true
        || ModConfig.BelayRopeExperiment?.Value == true;

    public string LocalExerciseNotice => _localRopeNotice;
    public LocalBelayRopeStatus? LocalRopeStatus => _ropeControl == null || _localRoutePending ? null
        : new(_ropeControl.Capacity, _ropeControl.Released, _ropeControl.Distance,
            _ropeControl.Slack, _ropeControl.Reserve, _restLength, _particleLength,
            _ropeControl.Motion, _ropeControl.Braked,
            float.IsFinite(_restLength) && Math.Abs(_restLength - _ropeControl.Released) <= .05f);

    public void SetLocalRopeMotion(int motion)
    {
        if (_ropeControl == null || !IsLocalExercise || _stopping || _localRoutePending) return;
        if (!_ropeControl.TrySetMotion(motion)) return;
    }

    private bool TryPrepareLocalRope(Lifeline lifeline, BelayTopologySnapshot route, bool local,
        out BelayRopeControl control, out string reason)
    {
        control = null;
        reason = "";
        if (local ? ModConfig.BelayRopeExperiment?.Value != true : !_partnerStation.HasValue) return true;
        if (!TryMeasureLocalStation(lifeline, route.Available, out var available, out reason)) return false;
        reason = "Start the rope-budget exercise while stable on the wall, before a fall.";
        if (lifeline.pawn.IsFalling || lifeline.SwingHasPivotAndExtremity()) return false;
        if (!TryReadRopeDistance(lifeline, out var distance)) { reason = "The piton-to-harness distance is not ready."; return false; }
        var parts = lifeline.securingRope.ropeParts;
        var rest = parts[parts.Count - 1].obiRope.restLength;
        reason = "The final segment exceeds the current rope capacity or has an invalid rest length.";
        if (!float.IsFinite(rest) || rest <= 0 || rest > available || distance > available) return false;
        control = new BelayRopeControl(available, rest, distance);
        reason = "";
        return true;
    }

    private static bool TryReadRopeDistance(Lifeline lifeline, out float distance)
    {
        distance = Vector3.Distance(lifeline.GetSecuringPitonPosition(true), lifeline.harness.GetAttachPosition());
        return float.IsFinite(distance) && distance > 0;
    }

    private bool ObserveLocalRopeCapacity(float available)
    {
        if (_ropeControl == null) return true;
        _measuredRopeCapacity = available;
        if (!TryMeasureLocalStation(_lifeline, available, out available, out var reason))
        { EndLocalRope(reason); return false; }
        if (_ropeControl.TryObserveCapacity(available))
        { if (!_localRoutePending) RememberLocalRoute(); return true; }
        EndLocalRope("The available rope no longer covers the paid-out segment. Check protection before restarting.");
        return false;
    }

    private bool StepLocalRope(float elapsed, bool suspended, float swingReal = 0)
    {
        if (_ropeControl == null) return false;
        RefreshRemoteRopeMotion();
        if (!TryReadRopeDistance(_lifeline, out var distance))
        { EndLocalRope("The native protection distance is no longer valid."); return false; }
        var minimum = distance;
        if (suspended && _ropeControl.Suspended)
        {
            // The native suspension and the drawn rope use different origins.
            // Keep the initial translation fixed for this suspension and use
            // both measurements to prevent take-in from shortening loaded rope.
            if (!(swingReal > 0)) swingReal = _lifeline.SwingPivotInstance.SwingRealDistance;
            minimum = Math.Max(minimum, swingReal + _ropeControl.SwingOffset);
        }
        if (!_ropeControl.TryStep(distance, minimum, elapsed))
        { EndLocalRope("The rope-budget measurement is invalid."); return false; }
        // Native fall/anchor transitions can move the body before IsFalling or
        // the swing pivot catches up. Keep the locked budget and the outward
        // movement restriction; retreat and feeding remain available. A brief
        // overrun must not silently release the belayer's control.
        return true;
    }

    private void RefreshRemoteRopeMotion()
    {
        if (_ropeControl == null || _localExercise || !_partnerStation.HasValue) return;
        var motion = _lease.MotionAt(Now);
        if (_localRoutePending || (_ropeControl.Braked && motion > 0)) motion = 0;
        _ropeControl.TrySetMotion(motion);
    }

    private bool TryBeginRopeSuspension(float real, out float target)
    {
        target = real;
        if (!StepLocalRope(0, false)) return false;
        if (!_ropeControl.TryAttachSuspension(real, out target))
        {
            EndLocalRope("Rope length could not be mapped to the native suspension. Native control resumed.");
            return false;
        }
        ModLog.Info("[BelayRope] Suspension; offset=" + _ropeControl.SwingOffset
            + ", geometry=" + _ropeControl.Distance + ", real=" + real + ", target=" + target);
        return true;
    }

    private bool TryStepRopeSuspension(float real, out float target)
    {
        target = real;
        if (!_ropeControl.Suspended)
        {
            if (!TryBeginRopeSuspension(real, out target)) return false;
        }
        return StepLocalRope(Time.fixedDeltaTime, true, real) && TryRopeSuspensionTarget(out target);
    }

    private bool TryRopeSuspensionTarget(out float target)
    {
        if (_ropeControl.TryGetSuspensionTarget(out target)) return true;
        EndLocalRope("Rope length could not be mapped to the native suspension. Native control resumed.");
        return false;
    }

    private static void BeforeLocalRopeLength(ObiRopeLengthController __instance, ref float __0)
    {
        var owner = _owner;
        if (owner?._ropeControl == null || owner._stopping) return;
        try
        {
            if (!owner.CanWriteLocalRopeGoal(__instance)) return;
            __0 = owner._ropeControl.Released;
        }
        catch (Exception ex) { owner.Fail(ex); }
    }

    private bool CanWriteLocalRopeGoal(ObiRopeLengthController controller)
    {
        if (_localRoutePending) return false;
        if (controller.obiRope == null || Id(controller.obiRope) != _localObiRope) return false;
        if (!MappingMatches(out var reason))
        {
            if (_pivot != 0 && _lifeline.pawn != null && !_lifeline.pawn.IsFalling
                && !_lifeline.SwingHasPivotAndExtremity()) return false; // Tick rearms the completed suspension first.
            if (!TryBeginLocalRouteChange(reason)) EndLocalRope("Protection route changed during a native length update. " + reason);
            return false;
        }
        if (!TryGetActiveContext(out reason)) { Stop(_lease.Session, reason); return false; }
        if (_lease.IsExpired(Now)) { Stop(_lease.Session, "The brake-command lease expired during a native length update."); return false; }
        if (controller.ropeLengthOverride > 0)
        { EndLocalRope("The native rope has a fixed-length override. End that interaction before starting."); return false; }
        return true;
    }

    private static void BeforeLocalRopeRefresh(ObiRopeLengthController __instance)
    {
        var owner = _owner;
        if (owner?._ropeControl == null || owner._stopping) return;
        try
        {
            if (!owner.CanWriteLocalRopeGoal(__instance)) return;
            // RefreshRopeLength is called by the native Update/FixedUpdate at
            // the solver's selected phase. It bypasses SetRopeLengthInstant and
            // otherwise returns early when the old native goal is unchanged.
            // Supply the goal here; native code remains responsible for particles.
            owner._localLengthController = __instance;
            owner._lastLocalRopeGoal = owner._ropeControl.Released;
            __instance.ropeLength = owner._lastLocalRopeGoal;
            if (owner._ropeRefreshCount < uint.MaxValue) owner._ropeRefreshCount++;
        }
        catch (Exception ex) { owner.Fail(ex); }
    }

    private static void AfterLocalRopeLength(ObiRopeLengthController __instance)
    {
        var owner = _owner;
        if (owner?._ropeControl == null || owner._stopping) return;
        try
        {
            if (__instance.obiRope != null && Id(__instance.obiRope) == owner._localObiRope)
                owner._restLength = __instance.obiRope.restLength;
        }
        catch (Exception ex) { owner.Fail(ex); }
    }

    private void ReleaseLocalRopeGoal()
    {
        try
        {
            if (!GameLifecycleService.TryGetRawGameState(out var lifecycle) || lifecycle != CairnGameLifecycleState.InGame) return;
            var controller = _localLengthController;
            if (controller == null || controller.obiRope == null || Id(controller.obiRope) != _localObiRope) return;
            if (Math.Abs(controller.ropeLength - _lastLocalRopeGoal) > .001f) return;
            var current = controller.obiRope.restLength;
            if (float.IsFinite(current) && current > 0 && current <= 200) controller.ropeLength = current;
        }
        catch (Exception ex) { ModLog.Warning("[BelayRope] Could not release native length goal: " + ex.Message); }
        finally { _localLengthController = null; _lastLocalRopeGoal = float.NaN; _ropeRefreshCount = 0; }
    }

    private void SampleLocalRope()
    {
        if (_localRoutePending || Now < _nextRopeSample) return;
        _nextRopeSample = Now + .5;
        var part = _rope.ropeParts[_rope.ropeParts.Count - 1];
        _restLength = part.obiRope.restLength;
        _particleLength = _rope.GetLength(true, true);
        var stretchScale = part.obiRope.stretchingScale;
        var compression = part.obiRope.maxCompression;
        var body = _lifeline.harness.GetAttachPosition();
        var anchor = _lifeline.GetSecuringPitonPosition(true);
        ModLog.Info("[BelayRope] Sample; brake=" + _ropeControl.Braked + ", motion=" + _ropeControl.Motion
            + ", catchPending=" + _ropeControl.CaptureBrakeDistance
            + ", released=" + _ropeControl.Released + ", geometry=" + _ropeControl.Distance
            + ", slack=" + _ropeControl.Slack + ", reserve=" + _ropeControl.Reserve
            + ", rest=" + _restLength + ", particles=" + _particleLength
            + ", stretchScale=" + stretchScale + ", maxCompression=" + compression
            + ", capacity=" + _ropeControl.Capacity + ", measuredCapacity=" + _measuredRopeCapacity + ", refreshes=" + _ropeRefreshCount
            + ", rebinds=" + _localRouteRebinds + ", topologyRevision=" + _topology.Revision
            + ", climbQueries=" + _climbLimitQueries + ", climbBlocks=" + _climbLimitBlocks + ", climbBlocked=" + _climbMovementBlocked
            + ", limit=" + _actual + ", swingReal=" + _real + ", suspended=" + (_pivot != 0)
            + ", x=" + body.x + ", y=" + body.y + ", z=" + body.z
            + ", anchorX=" + anchor.x + ", anchorY=" + anchor.y + ", anchorZ=" + anchor.z);
        if (LocalStationStatus.HasValue)
        {
            var station = LocalStationStatus.Value;
            ModLog.Info("[BelayStation] Sample; valid=" + station.Valid + ", fixed=" + station.FixedLength
                + ", finalCapacity=" + station.AvailableToClimber + ", protections=" + station.IntermediateProtections);
        }
    }

    private void EndLocalRope(string reason)
    {
        if (_control == null || _lease == null) return;
        _localRopeNotice = reason;
        ModLog.Info("[BelayRope] Ended; " + reason);
        Stop(_lease.Session, reason);
    }
}
