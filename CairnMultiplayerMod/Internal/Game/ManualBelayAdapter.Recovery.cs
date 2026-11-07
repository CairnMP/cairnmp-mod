using System;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Roping;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    // Identity proof only: no native wrappers, old parts, lengths or suspension
    // settings survive teardown. Scene reset invalidates even this proof.
    private BelaySharedStation _recoveryStation;
    private BelayStationAnchor _recoveryStationAnchor;
    private Guid _recoverySession;

    public bool RecoveryCharacterAlive
    {
        get
        {
            try
            {
                var survival = SurvivalManager.Instance;
                return survival != null && float.IsFinite(survival.CurrentHp) && survival.CurrentHp > 0;
            }
            catch (Exception exception)
            { ModLog.SuppressedException("belay.recovery-alive", exception); return false; }
        }
    }

    private void RememberRecoveryStation(BelaySharedStation station, BelayStationAnchor anchor, Guid session = default)
    {
        if (!station.IsValid || anchor == null) return;
        if (station != _recoveryStation || session != Guid.Empty) _recoverySession = session;
        _recoveryStation = station;
        _recoveryStationAnchor = anchor;
    }

    private void ForgetRecoveryStation()
    { _recoveryStation = default; _recoveryStationAnchor = null; _recoverySession = Guid.Empty; }

    private bool RecoveryStationMatches(BelaySharedStation station, Lifeline lifeline, Piton piton)
        => station == _recoveryStation && _recoverySession != Guid.Empty && _recoveryStationAnchor != null
            && lifeline?.securingRope != null && piton != null
            && _recoveryStationAnchor.Matches(Id(lifeline), Id(lifeline.securingRope), Id(piton),
                piton.pitonHp, ToNumerics(piton.transform.position));

    public bool TryResumeStation(BelaySharedStation station, out string reason)
    {
        reason = "Wait for the original belayer station to become available.";
        try
        {
            if (!station.IsValid || !RecoveryCharacterAlive) return false;
            reason = "End the previous native station protection before resuming.";
            if (_stationSession != Guid.Empty || _rolePreparing || _roleAnchor != null) return false;
            reason = "The defined station differs from the agreed station.";
            if (HasStation && _definedStation != station) return false;
            if (!CanBeginStation(out reason)) return false;
            var lifeline = LocalLifeline();
            reason = "Wait for the original station's initialized rope and personal anchor.";
            if (lifeline?.securingRope?.IsInitialized != true || lifeline.SwingHasPivotAndExtremity()) return false;
            var piton = PersonalAnchor(lifeline);
            reason = "The original station identity changed. A nearby or reloaded piton cannot resume this pair.";
            if (!RecoveryStationMatches(station, lifeline, piton)
                || !RopeInterop.TryResolvePitonReference(lifeline, station.PitonOwner, station.PitonId,
                    _localPlayerId(), out var resolved) || Id(resolved) != Id(piton)) return false;
            var position = station.Position;
            var anchor = new BelayStationAnchor(Id(lifeline), Id(lifeline.securingRope), Id(piton),
                new System.Numerics.Vector3(position.X, position.Y, position.Z));
            _stationLifeline = lifeline;
            _stationPiton = Id(piton);
            _stationAnchor = anchor;
            _definedStation = station;
            _stationInvalidReason = null;
            reason = "";
            return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("belay.recovery-station", exception);
            reason = "The native station is not ready to resume.";
            return false;
        }
    }

    public bool CanResumeClimber(BelaySharedStation station, out string reason)
    {
        reason = "Wait for the previous native rope control to finish releasing.";
        try
        {
            if (_stopping || _rolePreparing || _roleAnchor != null || !RecoveryCharacterAlive) return false;
            if (!CanClimb(out reason)
                || !TryResolveSharedStation(station, out var piton, out var attached, out reason)) return false;
            reason = "The original station must already be connected to the protection rope before resuming.";
            if (!attached) return false; // Recovery never queues another native station insertion.
            var lifeline = LocalLifeline();
            reason = "The original rope or station identity changed. Start a new invitation.";
            if (!RecoveryStationMatches(station, lifeline, piton)) return false;
            if (!CanUseAttachedStation(lifeline, piton, out reason)
                || !TryStartSnapshot(lifeline, out var snapshot, out reason)) return false;
            var final = lifeline.securingRope.ropeParts[snapshot.Parts.Length - 1];
            var controller = final?.obiRopeLengthController;
            reason = "Wait for the current final rope controller and physics to load.";
            if (controller?.obiRope == null || Id(controller.obiRope) != Id(final.obiRope)) return false;
            if (!snapshot.TryValidateControlResume(Id(lifeline.harness), lifeline.pawn.IsFalling,
                lifeline.SwingHasPivotAndExtremity(), controller.ropeLengthOverride, out reason)) return false;
            if (!TryReadPartnerRoute(lifeline, piton, out var physicalLength, out reason, trackRevision: false)) return false;
            reason = "The current native rope length exceeds its available capacity.";
            var rest = final.obiRope.restLength;
            if (!float.IsFinite(rest) || rest <= 0 || rest > snapshot.Available
                || physicalLength > lifeline.securingRope.MaxLengthMeters) return false;
            reason = "";
            return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("belay.recovery-climber", exception);
            reason = "The native protection route is not ready to resume.";
            return false;
        }
    }

    public bool BeginResumedClimber(Guid newSession, BelaySharedStation station, out string reason)
    {
        reason = "A resumed controller needs a fresh session identifier.";
        try
        {
            if (newSession == Guid.Empty || newSession == _recoverySession || !CanResumeClimber(station, out reason)) return false;
            if (!TryResolveSharedStation(station, out var piton, out var attached, out reason)) return false;
            reason = "The original station was detached before recovery committed.";
            return attached && BindPartnerStation(newSession, station, piton, out reason, initiallyBraked: true);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("belay.recovery-acquire", exception);
            ClearControl("The resumed native controller could not be acquired.");
            reason = "The native protection route changed before recovery committed.";
            return false;
        }
    }
}
