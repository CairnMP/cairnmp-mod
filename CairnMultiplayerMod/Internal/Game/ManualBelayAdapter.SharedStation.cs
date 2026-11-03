using System;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Roping;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private BelaySharedStation _definedStation;
    private BelaySharedStation? _partnerStation;

    public float RopeCapacity
    {
        get
        {
            try
            {
                if (!ContextValid) return 0;
                var rope = LocalLifeline()?.securingRope;
                var length = rope?.IsInitialized == true ? rope.MaxLengthMeters : 0;
                return float.IsFinite(length) && length > 0 && length <= 200 ? length : 0;
            }
            catch (Exception exception)
            { ModLog.SuppressedException("belay.rope-capacity", exception); return 0; }
        }
    }

    public bool IsStationKnown(BelaySharedStation station) => station.IsValid && _stationKnown(station);

    public bool TryGetStation(out BelaySharedStation station)
    {
        station = default;
        if (!_definedStation.IsValid || !StationValid) return false;
        station = _definedStation;
        return true;
    }

    public bool CanClimb(BelaySharedStation station, out string reason)
    {
        if (!CanClimb(out reason) || !TryResolveSharedStation(station, out var piton, out var attached, out reason)) return false;
        return attached ? CanUseAttachedStation(LocalLifeline(), piton, out reason)
            : CanAttachStation(LocalLifeline(), piton, out reason);
    }

    private static bool CanUseAttachedStation(Lifeline lifeline, Piton station, out string reason)
    {
        reason = "The climber's rope cannot cover the station's ordered protection route.";
        var rope = lifeline.securingRope;
        var points = new System.Collections.Generic.List<BelayProtectionPoint>();
        foreach (var holder in rope.ropeHolders)
        {
            var quickdraw = holder?.TryCast<LogicalQuickdraw>();
            if (quickdraw == null) continue;
            if (quickdraw.Piton == null || quickdraw.Piton.pitonHp <= 0) return false;
            points.Add(new BelayProtectionPoint(unchecked((uint)quickdraw.Piton.GetInstanceID()), ToNumerics(holder.Position)));
        }
        if (!BelayRoutePlanner.TryPlanFromStation(unchecked((uint)station.GetInstanceID()), points,
            ToNumerics(lifeline.harness.GetAttachPosition()), rope.MaxLengthMeters, out var plan)
            || !BelayRoutePlanner.TryLimitFinalCapacity(plan, rope.GetRemainingSlackMeters(false), out var available)
            || plan.ClimberDistance > available) return false;
        reason = "";
        return true;
    }

    public bool BeginClimber(Guid session, BelaySharedStation station, out string reason)
    {
        if (!CanClimb(out reason) || !TryResolveSharedStation(station, out var piton, out var attached, out reason)) return false;
        return attached ? BindPartnerStation(session, station, piton, out reason) : BeginStationAttachment(session, station, piton, out reason);
    }

    private bool BindPartnerStation(Guid session, BelaySharedStation station, Piton piton, out string reason)
    {
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

    private bool TryResolveSharedStation(BelaySharedStation station, out Piton selected, out bool attached, out string reason)
    {
        selected = null; attached = false;
        reason = "The partner station reference is invalid.";
        if (!Enabled || !station.IsValid) return false;
        var lifeline = LocalLifeline();
        if (!TrySnapshot(lifeline, out _, out reason)) return false;
        reason = "The station's exact session piton is not synchronized. Wait for its native copy; position-only reload matches cannot start belay.";
        if (!RopeInterop.TryResolvePitonReference(lifeline, station.PitonOwner, station.PitonId, _localPlayerId(), out selected)) return false;
        reason = "The agreed station piton moved, broke or has no quickdraw.";
        if (!float.IsFinite(selected.pitonHp) || selected.pitonHp <= 0 || selected.Quickdraw == null
            || System.Numerics.Vector3.DistanceSquared(ToNumerics(selected.transform.position),
                new System.Numerics.Vector3(station.Position.X, station.Position.Y, station.Position.Z)) > .05f * .05f) return false;
        var order = ConnectionOrder(lifeline);
        var occurrences = 0;
        foreach (var id in order) if (id == Id(selected)) occurrences++;
        attached = lifeline.securingRope.IsAttached(selected.Quickdraw.Cast<IRopeHolder>());
        reason = "The station's logical attachment and protection route disagree or repeat the same piton.";
        if (occurrences != (attached ? 1 : 0)) return false;
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
