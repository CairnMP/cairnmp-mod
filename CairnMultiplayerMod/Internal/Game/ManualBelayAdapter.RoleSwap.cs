using System;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Roping;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private Guid _roleOldSession, _roleNextSession;
    private BelaySharedStation _roleStation;
    private Lifeline _roleLifeline;
    private Piton _rolePiton;
    private BelayStationAnchor _roleAnchor;
    private bool _rolePreparing, _roleConnectionReady;
    private string _roleFailure;
    private uint _rolePreparationSequence;
    private double _nextRolePreparationPulse;

    private bool ReadRoleAnchor(out Lifeline lifeline, out Piton piton, out string reason)
    {
        lifeline = null; piton = null;
        if (!Enabled || !TryGetActiveContext(out reason)) { reason = "Return to active Belay Team gameplay."; return false; }
        lifeline = LocalLifeline(); piton = PersonalAnchor(lifeline);
        reason = "Remain personally anchored on a healthy piton, stationary and outside a fall, before exchanging roles.";
        return piton != null && piton.pitonHp > 0 && lifeline.pawn != null && lifeline.harness != null
            && !lifeline.pawn.IsFalling && !lifeline.SwingHasPivotAndExtremity()
            && Vector3.Distance(lifeline.harness.GetAttachPosition(), piton.transform.position) <= 1.5f
            && TrySnapshot(lifeline, out _, out reason);
    }

    public bool CanPrepareRoleStation(Guid session, out string reason)
    {
        reason = "Wait for the current rope route to stabilize before defining the next station.";
        return _control != null && !_stopping && !_localExercise && _partnerStation.HasValue
            && _lease?.Session == session && !_localRoutePending && !_rebinding && _pivot == 0
            && !_control.Attached && _ropeControl?.Suspended == false && MappingMatches()
            && ReadRoleAnchor(out _, out _, out reason);
    }

    public bool PrepareRoleStation(Guid session, out BelaySharedStation station, out string reason)
    {
        station = default;
        if (!CanPrepareRoleStation(session, out reason) || !ReadRoleAnchor(out var lifeline, out var piton, out reason)) return false;
        reason = "The new station needs an exact synchronized piton identity.";
        if (!RopeInterop.TryGetPitonReference(lifeline, piton, _localPlayerId(), out var owner, out var source)) return false;
        CancelRoleSwap();
        var position = piton.transform.position;
        _roleOldSession = session; _roleLifeline = lifeline; _rolePiton = piton;
        _roleAnchor = new BelayStationAnchor(Id(lifeline), Id(lifeline.securingRope), Id(piton), ToNumerics(position));
        _roleStation = station = new BelaySharedStation(Guid.NewGuid(), new(position.x, position.y, position.z), owner, source);
        reason = "Upper station prepared. Remain personally anchored while your partner responds.";
        return true;
    }

    public bool RoleStationValid(Guid session, BelaySharedStation station)
        => _roleOldSession == session && _roleStation == station && _roleAnchor != null
            && CanPrepareRoleStation(session, out _) && _rolePiton != null && _roleLifeline != null
            && Id(PersonalAnchor(_roleLifeline)) == Id(_rolePiton)
            && _roleAnchor.Matches(Id(_roleLifeline), Id(_roleLifeline.securingRope), Id(_rolePiton),
                _rolePiton.pitonHp, ToNumerics(_rolePiton.transform.position));

    public bool CanReceiveRoleSwap(Guid session, BelaySharedStation station, out string reason)
    {
        reason = "Keep the original belayer station and personal anchor until the new route is ready.";
        if (_stationSession != session || !StationValid || !ReadRoleAnchor(out var lifeline, out _, out reason)) return false;
        if (!TryResolveSharedStation(station, out var piton, out var attached, out reason)) return false;
        return attached ? CanUseAttachedStation(lifeline, piton, out reason) : CanAttachStation(lifeline, piton, out reason);
    }

    public BelayRolePreparation PrepareRoleClimber(Guid session, Guid next, BelaySharedStation station, out string reason)
    {
        reason = _roleFailure;
        if (_roleFailure != null) return BelayRolePreparation.Failed;
        if (_rolePreparing)
        {
            if (_roleOldSession != session || _roleNextSession != next || _roleStation != station
                || _stationSession != session || !StationValid || !ReadRoleAnchor(out _, out _, out reason))
            { reason = _roleFailure = "The original protection changed during role preparation."; return BelayRolePreparation.Failed; }
            if (_attachment != null && Now >= _nextRolePreparationPulse)
            {
                _nextRolePreparationPulse = Now + .5;
                if (_rolePreparationSequence == uint.MaxValue || !_lease.TryAccept(next, ++_rolePreparationSequence, true, Now))
                { reason = _roleFailure = "Role preparation lost its local consent lease."; return BelayRolePreparation.Failed; }
            }
            if (_attachment == null && !CanReceiveRoleSwap(session, station, out reason))
            { _roleFailure = reason; return BelayRolePreparation.Failed; }
            reason = _roleConnectionReady ? "New route confirmed; keep your personal anchor attached." : "Connecting the new station; keep your original personal anchor attached.";
            return _roleConnectionReady ? BelayRolePreparation.Ready : BelayRolePreparation.Preparing;
        }
        if (next == Guid.Empty || !CanReceiveRoleSwap(session, station, out reason)
            || !TryResolveSharedStation(station, out var piton, out var attached, out reason)) return BelayRolePreparation.Failed;
        _roleOldSession = session; _roleNextSession = next; _roleStation = station;
        _rolePreparing = true; _roleConnectionReady = attached; _rolePreparationSequence = 0;
        if (attached) return BelayRolePreparation.Ready;
        if (BeginStationAttachment(next, station, piton, out reason)) return BelayRolePreparation.Preparing;
        _roleFailure = reason;
        return BelayRolePreparation.Failed;
    }

    public bool CommitRoleSwap(Guid session, Guid next, BelaySharedStation station, bool becomeBelayer, out string reason)
    {
        reason = "Both personal anchors and the prepared native route must still be valid.";
        if (next == Guid.Empty || !ReadRoleAnchor(out _, out _, out reason)) return false;
        if (becomeBelayer)
        {
            if (!RoleStationValid(session, station)) return false;
            var lifeline = _roleLifeline; var piton = _rolePiton; var anchor = _roleAnchor;
            Stop(session, "Anchored role exchange committed.");
            if (_control != null) { reason = "The old suspension still owns the rope."; return false; }
            _stationLifeline = lifeline; _stationPiton = Id(piton); _stationAnchor = anchor; _definedStation = station;
            CancelRoleSwap();
            if (ProtectStation(next)) return true;
            EndStation(); reason = "The new belayer station could not be protected."; return false;
        }
        if (!_rolePreparing || !_roleConnectionReady || _roleOldSession != session || _roleNextSession != next
            || _roleStation != station || _stationSession != session || !StationValid) return false;
        var prepared = _attachment;
        if (prepared != null)
        {
            // Readiness is not a substitute for a current physical route. Both
            // original personal protection and the new pins must survive consent.
            prepared.NextPoll = 0;
            TickStationAttachment();
            if (_attachment != prepared || _roleFailure != null || !_roleConnectionReady)
            { reason = _roleFailure ?? "The prepared station route changed before role commit."; return false; }
        }
        else if (!CanReceiveRoleSwap(session, station, out reason)) return false;
        EndStation();
        _rolePreparing = false;
        var success = prepared != null ? CompleteStationAttachment(prepared, new BelayInputLease(next, Now, true))
            : BeginClimber(next, station, out reason);
        if (success)
        {
            _lease = new BelayInputLease(next, Now, true);
            ApplyBrake(true);
        }
        CancelRoleSwap();
        return success;
    }

    public void CancelRoleSwap()
    {
        var receiving = _rolePreparing;
        _rolePreparing = false;
        if (receiving) CancelStationAttachment("Role exchange cancelled; original personal anchoring retained.");
        _roleOldSession = _roleNextSession = Guid.Empty; _roleStation = default;
        _roleLifeline = null; _rolePiton = null; _roleAnchor = null;
        _roleConnectionReady = false; _roleFailure = null;
    }
}
