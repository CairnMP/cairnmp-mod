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
    private static bool LocalExerciseEnabled => ModConfig.BelayRouteExperiment?.Value == true
        || ModConfig.BelayRopeExperiment?.Value == true;

    public string LocalExerciseNotice => _localRopeNotice;
    public LocalBelayRopeStatus? LocalRopeStatus => _ropeControl == null ? null
        : new(_ropeControl.Capacity, _ropeControl.Released, _ropeControl.Distance,
            _ropeControl.Slack, _ropeControl.Reserve, _restLength, _particleLength,
            _ropeControl.Motion, _ropeControl.Braked);

    public void SetLocalRopeMotion(int motion)
    {
        if (_ropeControl == null || !IsLocalExercise || _stopping) return;
        if (!_ropeControl.TrySetMotion(motion)) return;
    }

    private static bool TryPrepareLocalRope(Lifeline lifeline, BelayTopologySnapshot route, bool local,
        out BelayRopeControl control, out string reason)
    {
        control = null;
        reason = "";
        if (!local || ModConfig.BelayRopeExperiment?.Value != true) return true;
        reason = "Start the rope-budget exercise while stable on the wall, before a fall.";
        if (lifeline.pawn.IsFalling || lifeline.SwingHasPivotAndExtremity()) return false;
        if (!TryReadRopeDistance(lifeline, out var distance)) { reason = "The piton-to-harness distance is not ready."; return false; }
        var parts = lifeline.securingRope.ropeParts;
        var rest = parts[parts.Count - 1].obiRope.restLength;
        reason = "The final segment exceeds the current rope capacity or has an invalid rest length.";
        if (!float.IsFinite(rest) || rest <= 0 || rest > route.Available || distance > route.Available) return false;
        control = new BelayRopeControl(route.Available, rest, distance);
        reason = "";
        return true;
    }

    private static bool TryReadRopeDistance(Lifeline lifeline, out float distance)
    {
        distance = Vector3.Distance(lifeline.GetSecuringPitonPosition(true), lifeline.harness.GetAttachPosition());
        return float.IsFinite(distance) && distance > 0;
    }

    private bool StepLocalRope(float elapsed, bool suspended, float swingReal = 0)
    {
        if (_ropeControl == null) return false;
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
        if (!suspended && !_lifeline.pawn.IsFalling && distance > _ropeControl.Released + .15f)
        {
            // Wall climbing is kinematic. Do not fake a brake by snapping the
            // character back after they climb beyond an already locked budget.
            EndLocalRope("Climbing exceeded the locked rope length. Feed rope before moving farther from the piton.");
            return false;
        }
        return true;
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
        if (owner?._ropeControl == null || owner._stopping || __instance.obiRope == null
            || Id(__instance.obiRope) != owner._localObiRope) return;
        try
        {
            if (!owner.MappingMatches()) { owner.EndLocalRope("Protection route changed during a native length update."); return; }
            if (!ContextValid || owner._lease.IsExpired(Now)) { owner.Stop(owner._lease.Session); return; }
            // Reuse the already-observed native setter phase. Do not enqueue
            // extra solver writes or derive the target from its own readback.
            __0 = owner._ropeControl.Released;
        }
        catch (Exception ex) { owner.Fail(ex); }
    }

    private void SampleLocalRope()
    {
        if (Now < _nextRopeSample) return;
        _nextRopeSample = Now + .5;
        var part = _rope.ropeParts[_rope.ropeParts.Count - 1];
        _restLength = part.obiRope.restLength;
        _particleLength = _rope.GetLength(true, true);
        var body = _lifeline.harness.GetAttachPosition();
        ModLog.Info("[BelayRope] Sample; brake=" + _ropeControl.Braked + ", motion=" + _ropeControl.Motion
            + ", released=" + _ropeControl.Released + ", geometry=" + _ropeControl.Distance
            + ", slack=" + _ropeControl.Slack + ", reserve=" + _ropeControl.Reserve
            + ", rest=" + _restLength + ", particles=" + _particleLength
            + ", limit=" + _actual + ", swingReal=" + _real + ", suspended=" + (_pivot != 0)
            + ", x=" + body.x + ", y=" + body.y + ", z=" + body.z);
    }

    private void EndLocalRope(string reason)
    {
        _localRopeNotice = reason;
        ModLog.Info("[BelayRope] Ended; " + reason);
        Stop(_lease.Session);
    }
}
