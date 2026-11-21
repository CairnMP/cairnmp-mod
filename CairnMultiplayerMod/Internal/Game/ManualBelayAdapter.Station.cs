using System;
using System.Collections.Generic;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using UnityEngine;
using NumericsVector3 = System.Numerics.Vector3;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private BelayStationAnchor _localStation;
    private Piton _localStationPiton;
    private uint _localStationId;
    private double _nextStationProtectionLog;
    private BelayProtectionPoint[] _measuredStationProtections;
    private readonly record struct StationRouteStatus(bool Valid, float FixedLength,
        float AvailableToClimber, int IntermediateProtections);
    private StationRouteStatus? _stationRoute;


    private bool LocalStationMatches(Lifeline lifeline, out string reason)
    {
        reason = "";
        if (_localStation == null) return true;
        reason = "The recorded station was lost, moved, broken or replaced. End belay and define a new station.";
        return lifeline != null && lifeline.securingRope != null && _localStationPiton != null
            && _localStation.Matches(Id(lifeline), Id(lifeline.securingRope), Id(_localStationPiton),
                _localStationPiton.pitonHp, ToNumerics(_localStationPiton.transform.position));
    }

    private bool ProtectsNativeStation(Lifeline lifeline, Piton piton)
    {
        if (lifeline == null || piton == null) return false;
        if (_stationSession != Guid.Empty && Id(lifeline) == Id(_stationLifeline)
            && Id(piton) == _stationPiton) return StationValid;
        return _control != null && !_stopping && _partnerStation.HasValue
            && Id(lifeline) == Id(_lifeline) && Id(piton) == Id(_localStationPiton)
            && ContextValid && LocalStationMatches(lifeline, out _);
    }

    private static void AfterStationCriticality(Lifeline __instance, Piton piton, ref bool __result)
    {
        if (__result) return;
        try
        {
            ManualBelayAdapter owner = null;
            if (_owner?.ProtectsNativeStation(__instance, piton) == true) owner = _owner;
            else if (_stationOwner?.ProtectsNativeStation(__instance, piton) == true) owner = _stationOwner;
            if (owner == null) return;
            // Native Climbot recovery checks this before taking an older piton.
            // The agreed station remains essential even after the climber clips
            // or personally anchors to a newer protection. Native breakage and
            // explicit session teardown retain their separate handling.
            __result = true;
            if (Now < owner._nextStationProtectionLog) return;
            owner._nextStationProtectionLog = Now + 2;
            var session = owner._stationSession != Guid.Empty ? owner._stationSession : owner._lease.Session;
            ModLog.Info("[BelayStation] Kept active station critical for native Climbot recovery; session=" + session);
        }
        catch (Exception exception) { ModLog.SuppressedException("belay.station-criticality", exception); }
    }

    private bool TryMeasureLocalStation(Lifeline lifeline, float nativeAvailable, out float available, out string reason)
    {
        available = nativeAvailable;
        if (_localStation == null) { reason = ""; return true; }
        if (!LocalStationMatches(lifeline, out reason)) return FailLocalStation(reason);
        var rope = lifeline.securingRope;
        var holderCount = rope.ropeHolders?.Count ?? 0;
        reason = "The station's native holder walk is not ready.";
        if (holderCount < 1 || holderCount > 66 || lifeline.harness == null) return FailLocalStation(reason);
        var protections = new List<BelayProtectionPoint>();
        long lastPiton = 0;
        for (var i = 0; i < holderCount; i++)
        {
            var holder = rope.ropeHolders[i];
            if (holder == null) return FailLocalStation(reason);
            var quickdraw = holder.TryCast<LogicalQuickdraw>();
            if (quickdraw == null) continue;
            var piton = quickdraw.Piton;
            reason = "A station-route protection is missing or broken.";
            if (piton == null || piton.pitonHp <= 0) return FailLocalStation(reason);
            protections.Add(new BelayProtectionPoint(unchecked((uint)piton.GetInstanceID()), ToNumerics(holder.Position)));
            lastPiton = Id(piton);
        }
        reason = "The station route must end at the climber's current securing piton.";
        if (lastPiton == 0 || lastPiton != Id(lifeline.GetLastSecuringPiton())) return FailLocalStation(reason);
        reason = "The recorded station must occur once on the native protection route, with enough rope for its ordered path.";
        if (!BelayRoutePlanner.TryPlanFromStation(_localStationId, protections,
            ToNumerics(lifeline.harness.GetAttachPosition()), rope.MaxLengthMeters, out var plan)
            || !BelayRoutePlanner.TryLimitFinalCapacity(plan, nativeAvailable, out available)) return FailLocalStation(reason);
        _stationRoute = new StationRouteStatus(true, plan.FixedLength, available, plan.ProtectionCount);
        _measuredStationProtections = protections.ToArray();
        reason = "";
        return true;
    }

    private bool FailLocalStation(string reason)
    {
        _stationRoute = new StationRouteStatus(false, 0, 0, 0);
        return false;
    }

    private void ClearLocalStation()
    {
        _localStation = null; _localStationPiton = null; _localStationId = 0;
        _measuredStationProtections = null;
        _stationRoute = null;
    }

    private static NumericsVector3 ToNumerics(Vector3 point) => new(point.x, point.y, point.z);
}
