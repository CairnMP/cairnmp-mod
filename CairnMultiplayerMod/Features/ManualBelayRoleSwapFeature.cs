using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

/// <summary>Consensual anchored exchange; the old controller stays active until both peers prepare.</summary>
internal sealed partial class ManualBelayFeature
{
    private sealed class RoleSwapState
    {
        internal Guid Id;
        internal BelaySharedStation Station;
        internal float Until, Capacity;
        internal bool Requested, Preparing, BelayerReady, ClimberReady;
    }

    private ManualBelayMessage _swap;
    private bool _swapReadySent, _swapCancelSent;
    private bool _roleOfferPending, _swapAcceptSent;
    private ManualBelayCommand _roleOfferCommand;
    private float _swapUntil;
    private float _roleOfferUntil;
    private string _roleCommitFailure;
    private string _swapWaitReason;
    private readonly HashSet<Guid> _retiredRoleSwaps = new();
    private readonly Queue<Guid> _retiredRoleSwapOrder = new();

    private static bool IsRoleSwapOperation(ManualBelayOperation operation)
        => operation is ManualBelayOperation.ProposeSwap or ManualBelayOperation.AcceptSwap
            or ManualBelayOperation.CancelSwap or ManualBelayOperation.ReadySwap or ManualBelayOperation.RequestSwap;

    private bool CanInitiateRoleSwap(out string reason)
    {
        reason = "Wait for active gameplay and the current role transaction to finish.";
        if (_active == null || _swap != null || _roleOfferPending || !IsBelayMode || !IsConnected
            || !InvitationGameplayReady || !Game.Belay.RecoveryCharacterAlive) return false;
        if (_active.Climber == LocalPlayerId) return Game.Belay.CanPrepareRoleStation(_active.Session, out reason);
        reason = "Wait for the current native rope control to be confirmed before requesting an exchange.";
        if (_confirmedSession != _active.Session) return false;
        reason = "Keep your current healthy station and personal anchor before requesting an exchange.";
        return _active.Belayer == LocalPlayerId && Game.Belay.StationValid && OwnStationMatches(_active.Station);
    }

    private void RequestRoleSwap()
    {
        if (_active?.Belayer != LocalPlayerId) return;
        if (!CanInitiateRoleSwap(out var reason)) { Notice(reason); return; }
        _roleOfferPending = true; _roleOfferUntil = Game.Time.UnscaledTime + 5;
        _roleOfferCommand = new() { Operation = ManualBelayOperation.RequestSwap, Session = _active.Session, Station = _active.Station };
        SendRoleSwap(_roleOfferCommand);
    }

    private void ProposeRoleSwap()
    {
        if (_active?.Climber != LocalPlayerId) return;
        if (!CanInitiateRoleSwap(out var failure)) { Notice(failure); return; }
        if (!Game.Belay.PrepareRoleStation(_active.Session, out var station, out var reason)) { Notice(reason); return; }
        _roleOfferPending = true; _roleOfferUntil = Game.Time.UnscaledTime + 5;
        _roleOfferCommand = new() { Operation = ManualBelayOperation.ProposeSwap, Session = _active.Session, Station = station };
        SendRoleSwap(_roleOfferCommand);
    }

    private void AcceptRoleSwap()
    {
        if (_active?.Climber == LocalPlayerId && _swap?.Phase == ManualBelayPhase.SwapRequested)
        {
            if (_swapCancelSent || _swapAcceptSent) return;
            if (!InvitationGameplayReady) { Notice("Return to active gameplay before preparing the new station."); return; }
            if (!Game.Belay.PrepareRoleStation(_active.Session, out var station, out var failure))
            { Notice(failure); return; }
            _swapAcceptSent = true;
            SendRoleSwap(new() { Operation = ManualBelayOperation.ProposeSwap, Session = _active.Session,
                Swap = _swap.Swap, Station = station });
            return;
        }
        if (_active?.Belayer != LocalPlayerId || _swap?.Phase != ManualBelayPhase.SwapOffered || _swapCancelSent || _swapAcceptSent) return;
        if (!InvitationGameplayReady) { Notice("Return to active gameplay before confirming the new station."); return; }
        if (!Game.Belay.CanReceiveRoleSwap(_active.Session, _swap.Station, out var reason)) { Notice(reason); return; }
        _swapAcceptSent = true;
        SendRoleSwap(new() { Operation = ManualBelayOperation.AcceptSwap, Session = _active.Session,
            Swap = _swap.Swap, Station = _swap.Station, RopeCapacity = Game.Belay.RopeCapacity });
    }

    private void SendRoleSwap(ManualBelayCommand command)
    {
        _command.Send(command, (accepted, reason) =>
        {
            if (accepted || _active?.Session != command.Session) return;
            // A competing proposal or a later local attempt can supersede an
            // initial command before its rejection arrives. Only its exact
            // still-pending attempt may clear local preparation.
            if (command.Swap == Guid.Empty
                && (command.Operation is ManualBelayOperation.RequestSwap or ManualBelayOperation.ProposeSwap)
                && (!_roleOfferPending || !ReferenceEquals(_roleOfferCommand, command))) return;
            if (command.Swap != Guid.Empty && _swap?.Swap != command.Swap) return;
            Notice(reason);
            if (_swap != null && command.Operation != ManualBelayOperation.CancelSwap)
                CancelRoleSwapRequest("The host rejected " + command.Operation + ": " + reason);
            else ClearRoleSwap();
            RefreshPanel();
        });
    }

    private void CancelRoleSwapRequest(string reason)
    {
        if (_active == null || _swap == null || _swapCancelSent) return;
        _swapCancelSent = true;
        FeatureLog.Info("[BelaySwap] Local cancellation requested; session=" + _active.Session + ", swap=" + _swap.Swap + ", reason=" + reason);
        SendRoleSwap(new() { Operation = ManualBelayOperation.CancelSwap, Session = _active.Session,
            Swap = _swap.Swap, Station = _swap.Station });
    }

    private void JudgeRoleSwap(HostRequest<ManualBelayCommand> request, SessionState session, float now,
        PlayerLocation belayer, PlayerLocation climber)
    {
        var command = request.Message;
        var actor = request.FromPlayerId;
        if (actor != session.Climber && actor != session.Belayer)
        { request.Reject("Only the current pair may exchange roles."); return; }
        if (command.Operation == ManualBelayOperation.RequestSwap)
        {
            if (HasPendingSharedDamage(session.Id))
            { request.Reject("Wait for the current fall-damage receipt before exchanging roles."); return; }
            if (actor != session.Belayer || !session.ConfirmedControl || session.Swap != null || command.Swap != Guid.Empty
                || command.RopeCapacity != 0 || command.Station != session.Station)
            { request.Reject("Only the current belayer may request an exchange from the agreed station."); return; }
            var requested = new RoleSwapState { Id = Guid.NewGuid(), Requested = true, Until = now + 20 };
            request.AfterCommit(() =>
            {
                session.Swap = requested;
                FeatureLog.Info("[BelaySwap] Host requested; session=" + session.Id + ", swap=" + requested.Id);
                _changed.Send(RoleSwapMessage(session, ManualBelayPhase.SwapRequested));
            });
            return;
        }
        if (command.Operation == ManualBelayOperation.ProposeSwap)
        {
            if (HasPendingSharedDamage(session.Id))
            { request.Reject("Wait for the current fall-damage receipt before exchanging roles."); return; }
            var requested = session.Swap?.Requested == true;
            if (actor != session.Climber || command.RopeCapacity != 0
                || (requested ? command.Swap != session.Swap.Id || now >= session.Swap.Until
                    : session.Swap != null || command.Swap != Guid.Empty)
                || command.Station.Id == session.Station.Id
                || (command.Station.PitonOwner == session.Station.PitonOwner && command.Station.PitonId == session.Station.PitonId)
                || !ValidStation(command.Station, climber))
            { request.Reject("The current climber must propose a new healthy station at their personal anchor."); return; }
            var proposed = new RoleSwapState { Id = requested ? session.Swap.Id : Guid.NewGuid(),
                Station = command.Station, Until = now + 20 };
            request.AfterCommit(() =>
            {
                session.Swap = proposed;
                FeatureLog.Info("[BelaySwap] Host proposed; session=" + session.Id + ", swap=" + proposed.Id);
                _changed.Send(RoleSwapMessage(session, ManualBelayPhase.SwapOffered));
            });
            return;
        }
        var swap = session.Swap;
        if (swap == null || now >= swap.Until || command.Swap != swap.Id || command.Station != swap.Station)
        { request.Reject("Role proposal expired, changed or was already completed."); return; }
        if (command.Operation == ManualBelayOperation.CancelSwap)
        { request.AfterCommit(() => CancelHostRoleSwap(session, "A participant cancelled the role exchange.")); return; }
        if (swap.Requested)
        { request.Reject("The climber must confirm the request and propose their real station first."); return; }
        if (!ValidStation(swap.Station, climber))
        { request.Reject("The new station is no longer synchronized at the current climber."); return; }
        if (command.Operation == ManualBelayOperation.AcceptSwap)
        {
            if (actor != session.Belayer || swap.Preparing || !WithinRopeRange(climber, belayer, command.RopeCapacity))
            { request.Reject("The current belayer must accept with their own future climbing rope capacity."); return; }
            request.AfterCommit(() =>
            {
                swap.Preparing = true; swap.Capacity = command.RopeCapacity; swap.Until = now + 20;
                FeatureLog.Info("[BelaySwap] Host preparing; session=" + session.Id + ", swap=" + swap.Id);
                _changed.Send(RoleSwapMessage(session, ManualBelayPhase.SwapPreparing));
            });
            return;
        }
        if (!swap.Preparing || command.RopeCapacity != 0
            || (actor == session.Climber ? swap.ClimberReady : swap.BelayerReady))
        { request.Reject("Role readiness needs the accepted transaction and one confirmation from each participant."); return; }
        request.AfterCommit(() =>
        {
            if (actor == session.Climber) swap.ClimberReady = true; else swap.BelayerReady = true;
            FeatureLog.Info("[BelaySwap] Host received readiness; session=" + session.Id + ", swap=" + swap.Id
                + ", from=" + (actor == session.Climber ? "current climber" : "current belayer"));
            if (!swap.BelayerReady || !swap.ClimberReady) return;
            var committed = RoleSwapMessage(session, ManualBelayPhase.SwapCommitted);
            committed.Belayer = session.Climber; committed.Climber = session.Belayer;
            committed.Braked = true;
            // Replace authority before publication: callbacks may immediately send commands.
            _hostSession = new SessionState { Id = swap.Id, Belayer = session.Climber, Climber = session.Belayer,
                Initiator = session.Climber, Started = true, Braked = true, Station = swap.Station,
                RopeCapacity = swap.Capacity, BelayerLocation = climber, Until = now + 3, InputAt = now };
            FeatureLog.Info("[BelaySwap] Host committed; previous=" + session.Id + ", next=" + swap.Id);
            _changed.Send(committed);
        });
    }

    private static ManualBelayMessage RoleSwapMessage(SessionState session, ManualBelayPhase phase)
        => new() { Phase = phase, Session = session.Id, Swap = session.Swap.Id, Station = session.Swap.Station,
            Belayer = session.Belayer, Climber = session.Climber,
            Initiator = session.Swap.Requested ? session.Belayer : session.Climber, RopeCapacity = session.Swap.Capacity };

    private void CancelHostRoleSwap(SessionState session, string reason)
    {
        if (session.Swap == null) return;
        var cancelled = RoleSwapMessage(session, ManualBelayPhase.SwapCancelled);
        FeatureLog.Info("[BelaySwap] Host cancelled; session=" + session.Id + ", swap=" + session.Swap.Id + ", reason=" + reason);
        session.Swap = null;
        _changed.Send(cancelled);
    }

    private bool ReceiveRoleSwap(ManualBelayMessage message)
    {
        if (message.Phase is not (ManualBelayPhase.SwapRequested or ManualBelayPhase.SwapOffered or ManualBelayPhase.SwapPreparing
            or ManualBelayPhase.SwapCancelled or ManualBelayPhase.SwapCommitted)) return false;
        if (_active?.Session != message.Session || message.Swap == Guid.Empty) return true;
        if (_retiredRoleSwaps.Contains(message.Swap)) return true;
        if (message.Phase == ManualBelayPhase.SwapCommitted)
        {
            if (_swap?.Phase != ManualBelayPhase.SwapPreparing || _swap.Swap != message.Swap || _swap.Station != message.Station
                || message.Belayer != _active.Climber || message.Climber != _active.Belayer
                || !message.Braked || message.RopeCapacity != _swap.RopeCapacity) return true;
            var previous = _active.Session;
            var ready = message.Climber != LocalPlayerId || Game.Belay.RopeCapacity == message.RopeCapacity;
            var reason = "The future climber's rope capacity changed after consent.";
            if (!ready || !Game.Belay.CommitRoleSwap(previous, message.Swap, message.Station, message.Belayer == LocalPlayerId, out reason))
            {
                // Host authority has moved already. Stop that NEW session; retain personal anchors.
                Game.Belay.Stop(previous);
                _active = new() { Session = message.Swap, Station = message.Station,
                    Belayer = message.Belayer, Climber = message.Climber, RopeCapacity = message.RopeCapacity };
                ClearRoleHud();
                _roleCommitFailure = reason;
                Notice("Role exchange stopped: " + reason); return true;
            }
            _active = new() { Phase = ManualBelayPhase.Started, Session = message.Swap, Station = message.Station,
                Belayer = message.Belayer, Climber = message.Climber, Initiator = message.Initiator,
                RopeCapacity = message.RopeCapacity, Braked = true };
            _desiredBrake = _sentBrake = true; _sentMotion = 0; _sequence = 0;
            _sendNotBefore = _nextIntent = _nextReport = _nextHint = 0;
            _localUntil = Game.Time.UnscaledTime + 3;
            _telemetry = null; _stopSent = false;
            ClearRoleSwap(); ResetRopeVisual();
            // The host committed only after both native routes were ready, and
            // this client's native commit above succeeded. Keep old roles until here.
            RefreshRoleHud(committedSwap: true);
            Notice(message.Belayer == LocalPlayerId ? "Roles exchanged. You are the belayer; brake is closed. Close L to take in slack."
                : "Roles exchanged. You are the climber. Keep your personal anchor until the new rope readback is ready.");
            RefreshPanel(); return true;
        }
        if (message.Belayer != _active.Belayer || message.Climber != _active.Climber) return true;
        if (message.Phase == ManualBelayPhase.SwapCancelled)
        {
            RetireRoleSwap(message.Swap);
            if (_swap?.Swap == message.Swap)
            { ClearRoleSwap(); Notice("Role exchange cancelled. The original pair remains active."); RefreshPanel(); }
            return true;
        }
        if (message.Phase == ManualBelayPhase.SwapRequested)
        {
            if (_swap != null || message.Station != default || message.RopeCapacity != 0 || message.Initiator != _active.Belayer) return true;
            if (message.Initiator == LocalPlayerId && !_roleOfferPending)
            { CancelUnconfirmedRoleSwap(message); return true; }
            _roleOfferPending = false; _roleOfferCommand = null; _swapAcceptSent = false;
            _swap = message; _swapUntil = Game.Time.UnscaledTime + 22;
            Notice(_active.Climber == LocalPlayerId ? "Your belayer requests a role exchange. Anchor at the next station, then accept or decline in L."
                : "Role exchange requested. Your climber must confirm and prepare the new station; current roles remain active.");
        }
        else if (message.Phase == ManualBelayPhase.SwapOffered)
        {
            if ((_swap != null && (_swap.Phase != ManualBelayPhase.SwapRequested || _swap.Swap != message.Swap))
                || !message.Station.IsValid) return true;
            if (_swap == null && message.Initiator == LocalPlayerId && !_roleOfferPending)
            { CancelUnconfirmedRoleSwap(message); return true; }
            _roleOfferPending = false; _roleOfferCommand = null; _swapAcceptSent = false;
            _swap = message; _swapUntil = Game.Time.UnscaledTime + 22;
            Notice("Role exchange proposed. Remain personally anchored; press L to accept or cancel.");
        }
        else
        {
            if (_swap?.Swap != message.Swap || _swap.Station != message.Station) return true;
            if (_swap.Phase != ManualBelayPhase.SwapOffered || !ValidCapacity(message.RopeCapacity)) return true;
            _swap = message; _swapUntil = Game.Time.UnscaledTime + 22;
            if (_active.Belayer == LocalPlayerId) { _desiredBrake = true; _nextIntent = 0; }
        }
        RefreshPanel();
        if (message.Phase == ManualBelayPhase.SwapPreparing || !KeyboardCaptured) Game.BelayPanel.Show();
        return true;
    }

    private void TickRoleSwap(float now)
    {
        if (_roleOfferPending && now >= _roleOfferUntil)
        { ClearRoleSwap(); Notice("No role proposal confirmation arrived. Original roles retained."); RefreshPanel(); }
        if (_roleCommitFailure != null)
        {
            var failure = _roleCommitFailure; _roleCommitFailure = null;
            RequestStop(failure); return;
        }
        if (_swap == null || _active == null) return;
        if (now >= _swapUntil) { CancelRoleSwapRequest("The role proposal or preparation timed out locally."); return; }
        if (_swap.Phase == ManualBelayPhase.SwapRequested) return;
        if (_active.Climber == LocalPlayerId && !Game.Belay.RoleStationValid(_active.Session, _swap.Station))
        { Notice("Keep your personal anchor at the proposed station. Original roles retained."); CancelRoleSwapRequest("The proposed personal station changed."); return; }
        if (_swap.Phase != ManualBelayPhase.SwapPreparing || _swapCancelSent) return;
        Game.BelayPanel.Show();
        var reason = "";
        var preparation = _active.Climber == LocalPlayerId ? BelayRolePreparation.Ready
            : Game.Belay.PrepareRoleClimber(_active.Session, _swap.Swap, _swap.Station, out reason);
        _swapWaitReason = reason;
        if (preparation == BelayRolePreparation.Failed)
        { Notice("The new native route could not be prepared: " + reason + " Keep both personal anchors; original roles retained."); CancelRoleSwapRequest("The new native route could not be prepared: " + reason); return; }
        if (preparation != BelayRolePreparation.Ready || _swapReadySent) return;
        _swapReadySent = true;
        FeatureLog.Info("[BelaySwap] Local route ready; session=" + _active.Session + ", swap=" + _swap.Swap
            + ", role=" + (_active.Climber == LocalPlayerId ? "current climber" : "current belayer"));
        SendRoleSwap(new() { Operation = ManualBelayOperation.ReadySwap, Session = _active.Session,
            Swap = _swap.Swap, Station = _swap.Station });
    }

    private bool RefreshRoleSwapPanel()
    {
        if (_swap == null || _active == null) return false;
        var incoming = _active.Belayer == LocalPlayerId;
        if (_swap.Phase == ManualBelayPhase.SwapRequested)
        {
            var reason = "Return to active gameplay before preparing the new station.";
            var ready = !incoming && !_swapAcceptSent && !_swapCancelSent && InvitationGameplayReady
                && Game.Belay.CanPrepareRoleStation(_active.Session, out reason);
            Game.BelayPanel.SetView(new() { Phase = BelayPanelPhase.SwapRequest,
                PartnerName = GetPlayerName(incoming ? _active.Climber : _active.Belayer), LocalBelayer = incoming,
                CanAccept = ready, Description = _swapCancelSent ? "Cancelling the request; original roles remain active."
                    : incoming ? "You requested an exchange. Wait for your climber to anchor and propose the new station. You will confirm that station before exchanging."
                    : _swapAcceptSent ? "Station proposal sent; waiting for host validation. Original roles remain active."
                    : ready ? "Your belayer requests an exchange. Accept to propose your current healthy anchor as the new station; your partner must then confirm it."
                    : "Your belayer requests an exchange. " + RoleSwapReason(reason) });
            return true;
        }
        var preparing = _swap.Phase == ManualBelayPhase.SwapPreparing;
        var readinessReason = "Return to active gameplay before confirming the new station.";
        var canAccept = !preparing && incoming && !_swapAcceptSent && !_swapCancelSent && InvitationGameplayReady
            && Game.Belay.CanReceiveRoleSwap(_active.Session, _swap.Station, out readinessReason);
        Game.BelayPanel.SetView(new() { Phase = preparing ? BelayPanelPhase.SwapPreparing : BelayPanelPhase.SwapInvitation,
            PartnerName = GetPlayerName(incoming ? _active.Climber : _active.Belayer), CanAccept = canAccept,
            LocalBelayer = incoming,
            Description = _swapCancelSent ? "Cancelling role exchange; keep both personal anchors attached."
                : preparing ? "Preparing the new station. Keep BOTH personal anchors attached and remain still. "
                    + (_swapReadySent ? "Local route ready; waiting for your partner." : RoleSwapReason(_swapWaitReason))
                : _swapAcceptSent ? "Station confirmation sent; waiting for the host. Keep your personal anchor attached."
                : incoming ? canAccept ? "Your climber proposes to become the belayer at their current anchor. Confirm this station to become the climber, or decline to keep current roles."
                    : "Waiting to confirm the proposed station. " + RoleSwapReason(readinessReason)
                : "Upper station proposed. Keep your personal anchor attached while your partner responds." });
        return true;
    }

    private void ClearRoleSwap()
    {
        if (_swap != null) RetireRoleSwap(_swap.Swap);
        Game.Belay.CancelRoleSwap(); _swap = null;
        _swapReadySent = _swapCancelSent = _roleOfferPending = _swapAcceptSent = false; _swapUntil = _roleOfferUntil = 0;
        _roleOfferCommand = null;
        _roleCommitFailure = _swapWaitReason = null;
    }

    private static string RoleSwapReason(string reason)
        => string.IsNullOrWhiteSpace(reason) ? "Waiting for native route confirmation."
            : reason.Length <= 200 ? reason : reason[..200];

    private void RetireRoleSwap(Guid id)
    {
        if (id == Guid.Empty || !_retiredRoleSwaps.Add(id)) return;
        _retiredRoleSwapOrder.Enqueue(id);
        if (_retiredRoleSwapOrder.Count > 128) _retiredRoleSwaps.Remove(_retiredRoleSwapOrder.Dequeue());
    }

    private void CancelUnconfirmedRoleSwap(ManualBelayMessage message)
    {
        RetireRoleSwap(message.Swap);
        SendRoleSwap(new() { Operation = ManualBelayOperation.CancelSwap, Session = message.Session,
            Swap = message.Swap, Station = message.Station });
    }
}
