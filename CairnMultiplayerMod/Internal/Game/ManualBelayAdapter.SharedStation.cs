using System;
using System.Collections.Generic;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private BelaySharedStation _definedStation;
    private BelaySharedStation? _partnerStation;

    public bool TryGetStation(out BelaySharedStation station)
    {
        station = default;
        if (!_definedStation.IsValid || !StationValid) return false;
        station = _definedStation;
        return true;
    }

    public bool CanClimb(BelaySharedStation station, out string reason)
        => CanClimb(out reason) && TryResolveSharedStation(station, out _, out reason);

    public bool BeginClimber(Guid session, BelaySharedStation station, out string reason)
    {
        if (!CanClimb(out reason) || !TryResolveSharedStation(station, out var piton, out reason)) return false;
        var lifeline = LocalLifeline();
        var anchor = new BelayStationAnchor(Id(lifeline), Id(lifeline.securingRope), Id(piton), ToNumerics(piton.transform.position));
        _partnerStation = station;
        _localStation = anchor;
        _localStationPiton = piton;
        _localStationId = unchecked((uint)piton.GetInstanceID());
        try
        {
            if (BeginClimber(session, false, out reason))
            {
                ModLog.Info("[BelayStation] Partner station bound to the climber's existing native protection route; station=" + station.Id);
                return true;
            }
            ClearPartnerStation();
            return false;
        }
        catch { ClearPartnerStation(); throw; }
    }

    private bool TryResolveSharedStation(BelaySharedStation station, out Piton selected, out string reason)
    {
        selected = null;
        reason = "The partner station reference is invalid.";
        if (!Enabled || !station.IsValid) return false;
        var lifeline = LocalLifeline();
        if (!TrySnapshot(lifeline, out _, out reason)) return false;
        var holders = lifeline.securingRope.ropeHolders;
        var candidates = new List<BelayProtectionPoint>();
        var native = new Dictionary<uint, Piton>();
        for (var i = 0; i < holders.Count; i++)
        {
            var piton = holders[i]?.TryCast<LogicalQuickdraw>()?.Piton;
            if (piton == null) continue;
            reason = "A protection on the station route is broken.";
            if (!float.IsFinite(piton.pitonHp) || !(piton.pitonHp > 0)) return false;
            var id = unchecked((uint)piton.GetInstanceID());
            candidates.Add(new BelayProtectionPoint(id, ToNumerics(piton.transform.position)));
            native[id] = piton;
        }
        reason = "Connect the climber's protection rope to the belayer's station piton first. Missing or ambiguous stations cannot start belay.";
        var position = station.Position;
        if (!BelayRoutePlanner.TryFindStationPiton(new System.Numerics.Vector3(position.X, position.Y, position.Z), candidates, out var stationId))
            return false;
        selected = native[stationId];
        reason = "";
        return true;
    }

    private void ClearPartnerStation()
    {
        if (!_partnerStation.HasValue) return;
        _partnerStation = null;
        ClearLocalStation();
    }
}
