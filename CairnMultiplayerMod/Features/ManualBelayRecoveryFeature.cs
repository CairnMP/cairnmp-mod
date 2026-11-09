using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

internal sealed partial class ManualBelayFeature
{
    private const float RecoveryLifetime = 120;
    private const float RecoveryReadyLifetime = 1.5f;
    private const int MaximumRecoveries = 5;
    private const string RecoveryRateLimited = "Recovery readiness arrived too quickly.";
    private readonly record struct RecoveryAlias(Guid Session, Guid Nonce);
    private sealed class RecoveryState
    {
        internal ManualBelayMessage Pair;
        internal Guid Nonce;
        internal string Scene;
        internal float Until, StartedAt, NextCheck, NextHud, NextPatchRetention;
        internal float BelayerReadyAt = float.NegativeInfinity, ClimberReadyAt = float.NegativeInfinity;
        internal float BelayerCheckAt = float.NegativeInfinity, ClimberCheckAt = float.NegativeInfinity;
        internal float LocalReadyAt = float.NegativeInfinity;
        internal int Attempts;
        internal List<RecoveryAlias> Aliases = new();
        internal string LastBlockReason;
        internal float NextBlockLog;
    }
    private RecoveryState _recovery, _hostRecovery;
    private Guid _confirmedSession;
    private string _confirmedScene;
    private bool _inputReleaseRequired;
    private readonly HashSet<Guid> _retiredBelaySessions = new(), _revokedRecoveryOrigins = new(), _usedRecoveryNonces = new();
    private readonly Queue<Guid> _retiredBelayOrder = new(), _revokedRecoveryOrder = new(), _usedRecoveryOrder = new();

    private static void RememberRecoveryId(HashSet<Guid> ids, Queue<Guid> order, Guid id)
    {
        if (id == Guid.Empty || !ids.Add(id)) return;
        order.Enqueue(id);
        if (order.Count > 256) ids.Remove(order.Dequeue());
    }

    private void ConfirmLocalControl()
    {
        if (_active == null || _confirmedSession == _active.Session
            || !TryLocations(_active.Belayer, _active.Climber, out var station, out _)) return;
        _confirmedSession = _active.Session;
        _confirmedScene = station.Scene;
    }

    private void ResetRecoveryLifecycle()
    {
        Game.Belay.ReleaseRecoveryPatches();
        _recovery = _hostRecovery = null;
        _confirmedSession = Guid.Empty; _confirmedScene = null; _inputReleaseRequired = false;
        _retiredBelaySessions.Clear(); _retiredBelayOrder.Clear();
        _revokedRecoveryOrigins.Clear(); _revokedRecoveryOrder.Clear();
        _usedRecoveryNonces.Clear(); _usedRecoveryOrder.Clear();
    }

    private void ClearRecovery(bool revoke)
    {
        Game.Belay.ReleaseRecoveryPatches();
        if (_recovery != null)
        {
            if (revoke) RememberRecoveryId(_revokedRecoveryOrigins, _revokedRecoveryOrder, _recovery.Pair.Session);
            RememberRecoveryId(_usedRecoveryNonces, _usedRecoveryOrder, _recovery.Nonce);
        }
        _recovery = null;
    }

    private void RequestInterruption(string reason)
    {
        if (_active == null) return;
        if (_confirmedSession != _active.Session || !Game.Belay.RecoveryCharacterAlive || string.IsNullOrEmpty(_confirmedScene))
        { RequestStop(reason); return; }
        var pair = _active;
        var scene = _confirmedScene;
        Game.Belay.RetainRecoveryPatches(pair.Session);
        ClearLocal(preserveRecovery: true);
        _recovery = new RecoveryState { Pair = pair, Scene = scene, Until = Game.Time.UnscaledTime + RecoveryLifetime };
        RefreshRecoveryHud();
        Send(new ManualBelayCommand { Operation = ManualBelayOperation.Interrupt, Session = pair.Session });
        FeatureLog.Info("[BelayRecovery] Interruption requested; session=" + pair.Session + "; " + reason);
    }

    private void InterruptHost(string reason)
    {
        var session = _hostSession;
        if (session == null) return;
        if (!session.ConfirmedControl || session.RecoveryAttempts >= MaximumRecoveries)
        { EndHost(reason); return; }
        var recovery = new RecoveryState { Pair = Message(session, ManualBelayPhase.Interrupted), Nonce = Guid.NewGuid(),
            Scene = session.BelayerLocation.Scene, Until = Game.Time.UnscaledTime + RecoveryLifetime,
            StartedAt = Game.Time.UnscaledTime, Attempts = session.RecoveryAttempts };
        if (session.RecoveryAliases != null) recovery.Aliases.AddRange(session.RecoveryAliases);
        recovery.Aliases.Add(new RecoveryAlias(session.Id, recovery.Nonce));
        recovery.Pair.Recovery = recovery.Nonce;
        _hostSession = null; _hostRecovery = recovery; _pendingSharedDamage = null;
        FeatureLog.Info("[BelayRecovery] Host waiting; previous=" + recovery.Pair.Session + ", recovery=" + recovery.Nonce + "; " + reason);
        _changed.Send(recovery.Pair);
    }

    private void EndHostRecovery(string reason)
    {
        if (_hostRecovery == null) return;
        var recovery = _hostRecovery; _hostRecovery = null;
        RememberRecoveryId(_usedRecoveryNonces, _usedRecoveryOrder, recovery.Nonce);
        var ended = RecoveryMessage(recovery, ManualBelayPhase.Ended);
        FeatureLog.Info("[BelayRecovery] Host ended recovery; recovery=" + recovery.Nonce + "; " + reason);
        _changed.Send(ended);
    }

    private static ManualBelayMessage RecoveryMessage(RecoveryState recovery, ManualBelayPhase phase)
        => new() { Phase = phase, Session = recovery.Pair.Session, Recovery = recovery.Nonce,
            Belayer = recovery.Pair.Belayer, Climber = recovery.Pair.Climber, Initiator = recovery.Pair.Initiator,
            Station = recovery.Pair.Station, RopeCapacity = recovery.Pair.RopeCapacity, Braked = true };

    private bool JudgeRecovery(HostRequest<ManualBelayCommand> request, float now)
    {
        var command = request.Message;
        var recovery = _hostRecovery;
        if (command.Operation == ManualBelayOperation.Stop)
        {
            var pair = _hostSession != null ? Message(_hostSession, ManualBelayPhase.Started) : recovery?.Pair;
            var aliases = _hostSession?.RecoveryAliases ?? recovery?.Aliases;
            if (pair != null && (request.FromPlayerId == pair.Belayer || request.FromPlayerId == pair.Climber) && aliases != null)
                foreach (var alias in aliases)
                    if (alias.Session == command.Session && (command.Recovery == Guid.Empty || command.Recovery == alias.Nonce))
                    {
                        request.AfterCommit(() =>
                        {
                            if (_hostSession != null) EndHost("A participant cancelled a recovery lineage.");
                            else EndHostRecovery("A participant cancelled a recovery lineage.");
                        });
                        return true;
                    }
        }
        if (recovery != null && command.Session == recovery.Pair.Session
            && command.Operation is ManualBelayOperation.Stop or ManualBelayOperation.Interrupt or ManualBelayOperation.ResumeReady)
        {
            var actor = request.FromPlayerId;
            if (actor != recovery.Pair.Belayer && actor != recovery.Pair.Climber)
            { request.Reject("Not your recovery episode."); return true; }
            if (command.Operation == ManualBelayOperation.Stop)
            {
                if (command.Recovery != Guid.Empty && command.Recovery != recovery.Nonce)
                { request.Reject("The recovery cancellation is stale."); return true; }
                request.AfterCommit(() => EndHostRecovery("A participant ended belay.")); return true;
            }
            if (command.Operation == ManualBelayOperation.Interrupt)
            { request.AfterCommit(() => _changed.Send(RecoveryMessage(recovery, ManualBelayPhase.Interrupted))); return true; }
            if (command.Recovery != recovery.Nonce || now >= recovery.Until || command.Station != recovery.Pair.Station
                || command.RopeCapacity != recovery.Pair.RopeCapacity || command.Swap != Guid.Empty)
            { request.Reject("The recovery identity, station, capacity or deadline changed."); return true; }
            var belayer = actor == recovery.Pair.Belayer;
            var lastCheck = belayer ? recovery.BelayerCheckAt : recovery.ClimberCheckAt;
            // A withdrawal must never be delayed by the positive-readiness rate gate.
            if (command.LengthApplied && now - lastCheck < .4f)
            { request.Reject(RecoveryRateLimited); return true; }
            request.AfterCommit(() =>
            {
                if (_hostRecovery != recovery) return;
                if (belayer) { recovery.BelayerCheckAt = now; recovery.BelayerReadyAt = command.LengthApplied ? now : float.NegativeInfinity; }
                else { recovery.ClimberCheckAt = now; recovery.ClimberReadyAt = command.LengthApplied ? now : float.NegativeInfinity; }
                TryCommitRecovery(recovery, now);
            });
            return true;
        }
        if (command.Operation == ManualBelayOperation.ResumeReady || command.Recovery != Guid.Empty)
        { request.Reject("No matching recovery episode exists."); return true; }
        if (command.Operation != ManualBelayOperation.Interrupt) return false;
        if (_hostSession == null || _hostSession.Id != command.Session
            || (request.FromPlayerId != _hostSession.Belayer && request.FromPlayerId != _hostSession.Climber))
        { request.Reject("No consenting active pair matches this interruption."); return true; }
        request.AfterCommit(() => InterruptHost("A participant lost the active native interaction."));
        return true;
    }

    private void TryCommitRecovery(RecoveryState recovery, float now)
    {
        if (_hostRecovery != recovery || now >= recovery.Until || now - recovery.StartedAt < .5f) return;
        if (now - recovery.BelayerReadyAt > RecoveryReadyLifetime || now - recovery.ClimberReadyAt > RecoveryReadyLifetime) return;
        if (!TryLocations(recovery.Pair.Belayer, recovery.Pair.Climber, out var station, out var climber)
            || station.Scene != recovery.Scene || climber.Scene != recovery.Scene
            || !ValidStation(recovery.Pair.Station, station) || !WithinRopeRange(station, climber, recovery.Pair.RopeCapacity)) return;
        var resumed = RecoveryMessage(recovery, ManualBelayPhase.Resumed);
        resumed.Session = Guid.NewGuid();
        _hostRecovery = null;
        _hostSession = new SessionState { Id = resumed.Session, Belayer = resumed.Belayer, Climber = resumed.Climber,
            Initiator = resumed.Initiator, Station = resumed.Station, RopeCapacity = resumed.RopeCapacity,
            Braked = true, Started = true, ConfirmedControl = false, RecoveryAttempts = recovery.Attempts + 1,
            ResumeNonce = recovery.Nonce, RecoveryAliases = recovery.Aliases,
            BelayerLocation = station, Until = now + 3, InputAt = now };
        FeatureLog.Info("[BelayRecovery] Host resumed; previous=" + recovery.Pair.Session + ", next=" + resumed.Session
            + ", recovery=" + recovery.Nonce + ", attempt=" + (recovery.Attempts + 1));
        _changed.Send(resumed);
        RememberRecoveryId(_usedRecoveryNonces, _usedRecoveryOrder, recovery.Nonce);
    }

    private bool ReceiveRecovery(ManualBelayMessage message)
    {
        if (message.Phase == ManualBelayPhase.Ended && _active?.Session == message.Session && message.Recovery != Guid.Empty)
        { ClearLocal(); Notice("Belay ended."); return true; }
        if (message.Phase == ManualBelayPhase.Ended && _recovery != null
            && (_recovery.Pair.Session == message.Session || (_recovery.Nonce != Guid.Empty && message.Recovery == _recovery.Nonce)))
        {
            if (message.Recovery != Guid.Empty && _recovery.Nonce != Guid.Empty && message.Recovery != _recovery.Nonce) return true;
            ClearLocal(); Notice("Belay recovery ended. A new invitation is required."); return true;
        }
        if (message.Phase is not (ManualBelayPhase.Interrupted or ManualBelayPhase.Resumed)) return false;
        if (message.Recovery == Guid.Empty || !message.Station.IsValid || !ValidCapacity(message.RopeCapacity)) return true;
        if (message.Phase == ManualBelayPhase.Interrupted)
        {
            if (_usedRecoveryNonces.Contains(message.Recovery) || _revokedRecoveryOrigins.Contains(message.Session)) return true;
            if (_recovery != null)
            {
                if (_recovery.Pair.Session != message.Session || (_recovery.Nonce != Guid.Empty && _recovery.Nonce != message.Recovery)) return true;
                if (_recovery.Nonce == message.Recovery && SameRecoveryPair(_recovery.Pair, message)) return true;
            }
            else if (_active?.Session != message.Session || _confirmedSession != message.Session) return true;
            var pair = _recovery?.Pair ?? _active;
            if (!SameRecoveryPair(pair, message)) return true;
            var until = _recovery?.Until ?? Game.Time.UnscaledTime + RecoveryLifetime;
            var scene = _recovery?.Scene ?? _confirmedScene;
            if (string.IsNullOrEmpty(scene)) return true;
            Game.Belay.RetainRecoveryPatches(pair.Session);
            ClearLocal(preserveRecovery: true);
            _recovery = new RecoveryState { Pair = message, Nonce = message.Recovery, Scene = scene, Until = until };
            RefreshRecoveryHud(); RefreshPanel();
            return true;
        }
        var recovery = _recovery;
        if (recovery == null || recovery.Nonce != message.Recovery || _usedRecoveryNonces.Contains(message.Recovery)
            || _revokedRecoveryOrigins.Contains(recovery.Pair.Session) || _retiredBelaySessions.Contains(message.Session)
            || !SameRecoveryPair(recovery.Pair, message) || !message.Braked || message.Motion != 0) return true;
        var hadFreshReady = Game.Time.UnscaledTime - recovery.LocalReadyAt <= RecoveryReadyLifetime;
        RememberRecoveryId(_usedRecoveryNonces, _usedRecoveryOrder, recovery.Nonce);
        _recovery = null;
        _active = message; _offer = _telemetry = null; _accepted = Guid.Empty;
        _sequence = 0; _desiredBrake = _sentBrake = true; _sentMotion = 0;
        _sendNotBefore = _nextIntent = _nextReport = _nextHint = 0;
        _localUntil = Game.Time.UnscaledTime + 3; _stopSent = false; _inputReleaseRequired = true;
        _confirmedSession = Guid.Empty; _confirmedScene = null;
        ClearRoleHud(); ResetRopeVisual();
        var reason = "Fresh native recovery readiness was lost before the host commit.";
        if (!hadFreshReady || !RecoveryContextReady() || !RecoveryLocationsMatch(recovery))
        { RequestStop(reason); return true; }
        Game.Belay.ReleaseRecoveryPatches();
        var ready = message.Belayer == LocalPlayerId
            ? Game.Belay.TryResumeStation(message.Station, out reason) && Game.Belay.ProtectStation(message.Session)
            : Game.Belay.BeginResumedClimber(message.Session, message.Station, out reason);
        if (!ready) { RequestStop("Native recovery refused: " + reason); return true; }
        RefreshRoleHud(); Game.BelayPanel.Close();
        Notice("Belay resumed with the brake closed. Release rope-control keys before using them again.");
        return true;
    }

    private static bool SameRecoveryPair(ManualBelayMessage first, ManualBelayMessage second)
        => first != null && first.Belayer == second.Belayer && first.Climber == second.Climber
            && first.Station == second.Station && first.RopeCapacity == second.RopeCapacity;

    private bool RecoveryContextReady()
        => IsBelayMode && IsConnected && Game.Belay.RecoveryCharacterAlive && Game.State.IsLocalPlayerInGame
            && !Game.State.IsLocalPlayerInCutscene && Game.Input.IsWindowFocused
            && !Game.Input.IsPauseMenuActive && !Game.Time.IsPaused;

    private bool RecoveryLocationsMatch(RecoveryState recovery)
        => TryLocations(recovery.Pair.Belayer, recovery.Pair.Climber, out var station, out var climber)
            && station.Scene == recovery.Scene && climber.Scene == recovery.Scene
            && WithinStationArea(recovery.Pair.Station, station) && (!IsHost || Game.Belay.IsStationKnown(recovery.Pair.Station))
            && WithinRopeRange(station, climber, recovery.Pair.RopeCapacity);

    private bool TickRecovery(float now)
    {
        if (IsHost && _hostRecovery != null && (now >= _hostRecovery.Until || !IsBelayMode || !IsConnected
            || ((_hostRecovery.Pair.Belayer == LocalPlayerId || _hostRecovery.Pair.Climber == LocalPlayerId) && !Game.Belay.RecoveryCharacterAlive)))
            EndHostRecovery("The recovery deadline, live character or room context ended.");
        var recovery = _recovery;
        if (recovery == null) return false;
        if (!IsBelayMode || !IsConnected || !Game.Belay.RecoveryCharacterAlive || now >= recovery.Until)
        { RequestStop("Recovery ended for death, session change or timeout."); return true; }
        if (now >= recovery.NextPatchRetention)
        {
            recovery.NextPatchRetention = now + .5f;
            Game.Belay.RetainRecoveryPatches(recovery.Pair.Session);
        }
        if (RecoveryContextReady() && recovery.Pair.Climber == LocalPlayerId)
        {
            var capacity = Game.Belay.RopeCapacity;
            if (ValidCapacity(capacity) && capacity != recovery.Pair.RopeCapacity)
            { RequestStop("The climber rope capacity changed; send a new invitation."); return true; }
        }
        if (recovery.Nonce != Guid.Empty && now >= recovery.NextCheck)
        {
            recovery.NextCheck = now + .5f;
            var reason = "Waiting for active gameplay and the original station.";
            var ready = RecoveryContextReady() && RecoveryLocationsMatch(recovery);
            if (ready)
                ready = recovery.Pair.Belayer == LocalPlayerId
                    ? Game.Belay.TryResumeStation(recovery.Pair.Station, out reason)
                    : Game.Belay.CanResumeClimber(recovery.Pair.Station, out reason);
            if (ready && recovery.Pair.Climber == LocalPlayerId && Game.Belay.RopeCapacity != recovery.Pair.RopeCapacity)
            { RequestStop("The climber rope capacity changed; send a new invitation."); return true; }
            recovery.LocalReadyAt = ready ? now : float.NegativeInfinity;
            if (!ready) LogRecoveryBlock(recovery, reason, now);
            // ResumeReady.LengthApplied is the operation-specific fresh-preflight
            // flag, not a claim that the interrupted character is protected.
            Send(new ManualBelayCommand { Operation = ManualBelayOperation.ResumeReady, Session = recovery.Pair.Session,
                Recovery = recovery.Nonce, Station = recovery.Pair.Station, RopeCapacity = recovery.Pair.RopeCapacity,
                LengthApplied = ready });
        }
        if (_recovery != null && now >= recovery.NextHud)
        { recovery.NextHud = now + .5f; RefreshRecoveryHud(); }
        if (_recovery != null && Game.BelayPanel.IsOpen && now >= _nextPanelRefresh)
        { _nextPanelRefresh = now + .2f; RefreshPanel(); }
        return true;
    }

    private void RefreshRecoveryHud()
    {
        if (_recovery == null) return;
        Game.Hud.ShowMessage("belay-role", "RETOMADA PENDENTE / WAITING TO RESUME — FREIO DA DUPLA INATIVO / PARTNER BRAKE OFF", 1.2f);
        Game.Hud.ShowMessage("belay", "Waiting for both players and the same healthy native station. L > End belay cancels permanently.", 1.2f);
    }

    private bool RefreshRecoveryPanel()
    {
        if (_recovery == null) return false;
        var pair = _recovery.Pair;
        Game.BelayPanel.SetView(new BelayPanelView { Phase = BelayPanelPhase.Recovering,
            LocalBelayer = pair.Belayer == LocalPlayerId,
            PartnerName = GetPlayerName(pair.Belayer == LocalPlayerId ? pair.Climber : pair.Belayer),
            Description = "RETOMADA PENDENTE / WAITING TO RESUME\nFREIO DA DUPLA INATIVO / PARTNER BRAKE OFF\nWaiting for both players and the same station.\nEnd belay cancels permanently." });
        return true;
    }

    private void LogRecoveryBlock(RecoveryState recovery, string reason, float now)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? "Waiting for fresh native readiness." : reason;
        if (reason.Length > 240) reason = reason[..240];
        if (recovery.LastBlockReason == reason || now < recovery.NextBlockLog) return;
        recovery.LastBlockReason = reason; recovery.NextBlockLog = now + 2;
        FeatureLog.Info("[BelayRecovery] Waiting; session=" + recovery.Pair.Session + ", recovery=" + recovery.Nonce + "; " + reason);
    }

    private bool ArmRecoveredInput()
    {
        if (!_inputReleaseRequired) return true;
        if (!Game.Input.IsKeyHeld(GameKey.Space) && !Game.Input.IsKeyHeld(GameKey.F7)
            && !Game.Input.IsKeyHeld(GameKey.UpArrow) && !Game.Input.IsKeyHeld(GameKey.DownArrow)) _inputReleaseRequired = false;
        return false;
    }
}
