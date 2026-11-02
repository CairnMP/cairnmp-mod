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
    public LocalBelayStationStatus? LocalStationStatus { get; private set; }

    public bool ToggleLocalStation(out string reason)
    {
        reason = "Enable the local rope-budget exercise first.";
        if (ModConfig.BelayRopeExperiment?.Value != true) return false;
        reason = "End the current rope exercise before changing its station.";
        if (_control != null || _stationLifeline != null) return false;
        if (_localStation != null)
        {
            ClearLocalStation();
            reason = "Local station cleared. The ordinary rope-budget exercise is available.";
            ModLog.Info("[BelayStation] Cleared by local input.");
            return true;
        }
        if (!TryGetActiveContext(out reason)) return false;
        reason = "Disconnect the cooperative harness rope before recording a station.";
        if (Roping.RopeInterop.IsNativeBelayEngaged) return false;
        var lifeline = LocalLifeline();
        var piton = lifeline != null ? lifeline.OffBelayPiton : null;
        reason = "Anchor yourself on a healthy piton before recording the station.";
        if (piton == null || piton.pitonHp <= 0 || lifeline.harness == null || lifeline.pawn == null
            || lifeline.pawn.IsFalling || lifeline.SwingHasPivotAndExtremity()
            || Vector3.Distance(lifeline.harness.GetAttachPosition(), piton.transform.position) > 1.5f) return false;
        var rope = lifeline.securingRope;
        reason = "Wait for the native protection rope to initialize.";
        if (rope == null || !rope.IsInitialized) return false;
        var position = piton.transform.position;
        _localStation = new BelayStationAnchor(Id(lifeline), Id(rope), Id(piton), ToNumerics(position));
        _localStationPiton = piton;
        _localStationId = unchecked((uint)piton.GetInstanceID());
        if (!TryMeasureLocalStation(lifeline, rope.GetRemainingSlackMeters(false), out _, out reason))
        { ClearLocalStation(); return false; }
        reason = "Station recorded on a real piton. Release your personal anchor, keep this piton connected, then Ctrl+F11 starts the local rehearsal.";
        ModLog.Info("[BelayStation] Captured; fixed=" + LocalStationStatus.Value.FixedLength
            + ", finalCapacity=" + LocalStationStatus.Value.AvailableToClimber
            + ", protections=" + LocalStationStatus.Value.IntermediateProtections);
        return true;
    }

    private bool LocalStationMatches(Lifeline lifeline, out string reason)
    {
        reason = "";
        if (_localStation == null) return true;
        reason = "The recorded station was lost, moved, broken or replaced. End the exercise and record a new station.";
        return lifeline != null && lifeline.securingRope != null && _localStationPiton != null
            && _localStation.Matches(Id(lifeline), Id(lifeline.securingRope), Id(_localStationPiton),
                _localStationPiton.pitonHp, ToNumerics(_localStationPiton.transform.position));
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
        LocalStationStatus = new LocalBelayStationStatus(true, plan.FixedLength, available,
            plan.ProtectionCount, "Real station piton on the existing native rope; local input stands in for the absent belayer.");
        reason = "";
        return true;
    }

    private bool FailLocalStation(string reason)
    {
        LocalStationStatus = new LocalBelayStationStatus(false, 0, 0, 0, reason);
        return false;
    }

    private void ClearLocalStation()
    {
        _localStation = null; _localStationPiton = null; _localStationId = 0;
        LocalStationStatus = null;
    }

    private static NumericsVector3 ToNumerics(Vector3 point) => new(point.x, point.y, point.z);
}
