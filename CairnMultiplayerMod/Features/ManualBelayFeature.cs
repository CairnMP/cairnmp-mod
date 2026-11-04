using System;
using System.IO;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

/// <summary>
/// Consensual remote brake pilot on the climber's existing protection route.
/// The host arbitrates roles/intent; only the climber touches their native suspension.
/// </summary>
internal sealed partial class ManualBelayFeature : MultiplayerFeature
{
    private const float InputInterval = .1f;
    private const string InputRateLimited = "Rope input arrived too quickly; retry the current input.";
    private const float MaximumRopeCapacity = 200;
    // Body points differ from native station/harness attachments. Native route
    // geometry is the final check after this broad-phase position margin.
    private const float BodyMargin = 3;
    private sealed class SessionState
    {
        internal Guid Id;
        internal int Belayer, Climber, Initiator;
        internal bool Started, Braked;
        internal uint Sequence, AppliedSequence;
        internal uint TopologyRevision;
        internal float Until, InputAt, NextReport;
        internal float InputCredits = 2;
        internal float RopeCapacity;
        internal sbyte Motion;
        internal PlayerLocation BelayerLocation;
        internal BelaySharedStation Station;
        internal RoleSwapState Swap;
    }

    private HostCommand<ManualBelayCommand> _command;
    private HostEvent<ManualBelayMessage> _changed;
    private SessionState _hostSession;
    private ManualBelayMessage _offer, _active, _telemetry;
    private string _lastPartnerBlock;
    private float _nextPartnerBlockLog;
    private Guid _accepted;
    private int _offeredTarget = -1;
    private bool _desiredBrake;
    private bool _sentBrake;
    private sbyte _sentMotion;
    private float _sendNotBefore;
    private uint _sequence;
    private float _nextIntent, _nextReport, _nextHint, _localUntil;
    private bool _stopSent;
    private float _nextPanelRefresh;
    private bool _cancelPending;
    public override string Id => "manual-belay";
    private bool IsBelayMode => Rules.UsesPartnerBelay && Game.Belay.Enabled;

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _command = feature.HostCommand<ManualBelayCommand>("request", Judge);
        _changed = feature.HostEvent<ManualBelayMessage>("changed", (_, message) => Receive(message));
        _ropeVisual = feature.Stream<BelayRopeVisualFrame>("rope-visual-v1", ReceiveRopeVisual);
        feature.Game.Chat.AddCommand("belay", "/belay offer <player> | request <player> | accept | stop",
            "Experimental manual brake: anchored belayer offers; climber accepts; Space brakes/releases.", HandleChat);
        feature.Game.BelayPanel.Register(HandlePanelAction);
        feature.OnDrawHud(feature.Game.BelayPanel.Draw);
        feature.EveryFrame(Tick, FeaturePhase.Always);
        feature.OnPlayerLeft((id, _) =>
        {
            if (IsHost && _hostSession != null && (id == _hostSession.Belayer || id == _hostSession.Climber)) EndHost();
            if (_active != null && (id == _active.Belayer || id == _active.Climber)) ClearLocal();
        });
        feature.OnSceneReset(() => { RequestStop(); Game.Belay.ResetScene(); ClearLocal(); });
        feature.OnSessionEnded(() => { ClearLocal(); _hostSession = null; _offer = null; _accepted = Guid.Empty; });
    }

    private void HandleChat(string args)
    {
        args = (args ?? "").Trim();
        if (!IsBelayMode || !IsConnected)
        { Notice("Join a Belay Team lobby before requesting partner belay."); return; }
        if (args.Equals("stop", StringComparison.OrdinalIgnoreCase)) { RequestStop(); return; }
        if (args.Equals("accept", StringComparison.OrdinalIgnoreCase))
        {
            if (_offer == null || _offer.Initiator == LocalPlayerId || Game.Time.UnscaledTime >= _localUntil)
            { Notice("No current belay offer to accept."); return; }
            var station = _offer.Station;
            var ready = _offer.Belayer == LocalPlayerId
                ? TryBeginStation(out station, out var reason) : Game.Belay.CanClimb(station, out reason);
            if (!ready) { Notice(reason); return; }
            _accepted = _offer.Session;
            Send(new ManualBelayCommand { Operation = ManualBelayOperation.Accept, Session = _accepted, Station = station,
                RopeCapacity = _offer.Climber == LocalPlayerId ? Game.Belay.RopeCapacity : 0 });
            return;
        }
        var asking = args.StartsWith("request ", StringComparison.OrdinalIgnoreCase);
        if (!asking && !args.StartsWith("offer ", StringComparison.OrdinalIgnoreCase))
        { Notice("Press L for partner belay, or use /belay offer|request <player>."); return; }
        if (_active != null || _offer != null || _offeredTarget >= 0) { Notice("End the current belay session first."); return; }
        var targetName = args[(asking ? 8 : 6)..].Trim();
        var target = -1;
        foreach (var player in Players)
            if (!player.IsLocal && (player.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase)
                || player.Id.ToString() == targetName)) { target = player.Id; break; }
        if (target < 0) { Notice("Player not found."); return; }
        var offeredStation = default(BelaySharedStation);
        var canSend = asking ? Game.Belay.CanClimb(out var failure) : TryBeginStation(out offeredStation, out failure);
        if (!canSend) { Notice(failure); return; }
        _offeredTarget = target;
        Send(new ManualBelayCommand { Operation = asking ? ManualBelayOperation.Request : ManualBelayOperation.Offer,
            Target = target, Station = offeredStation, RopeCapacity = asking ? Game.Belay.RopeCapacity : 0 });
    }

    private bool TryBeginStation(out BelaySharedStation station, out string reason)
    {
        station = default;
        if (!Game.Belay.BeginStation(out reason)) return false;
        if (Game.Belay.TryGetStation(out station)) return true;
        reason = "The native station is unavailable. Define your current anchor again.";
        return false;
    }

    private bool OwnStationMatches(BelaySharedStation station)
        => Game.Belay.TryGetStation(out var current) && current == station;

    private void Send(ManualBelayCommand command)
    {
        _command.Send(command, (accepted, reason) =>
        {
            if (accepted) return; // Acceptance by the host is not physical application.
            if (command.Operation == ManualBelayOperation.Intent)
            {
                if (_active?.Session != command.Session || _active.Sequence >= command.Sequence) return;
                if (_swap?.Phase == ManualBelayPhase.SwapPreparing && reason == "Belay session expired or replaced.")
                    return; // The role commit can arrive after an old command's response.
                if (reason == InputRateLimited)
                {
                    // A delivery burst must not end a valid pair or replay an
                    // old motion. Resample the current keys for the retry.
                    _nextIntent = Math.Min(_nextIntent, Game.Time.UnscaledTime + InputInterval);
                    return;
                }
            }
            Notice(reason);
            if (command.Operation is ManualBelayOperation.Offer or ManualBelayOperation.Request)
            { Game.Belay.EndStation(); _offeredTarget = -1; _cancelPending = false; }
            if (command.Operation == ManualBelayOperation.Accept) { _accepted = Guid.Empty; Game.Belay.EndStation(); }
            if (command.Operation == ManualBelayOperation.Intent) RequestStop();
        });
    }

    private void HandlePanelAction(BelayPanelAction action, int target)
    {
        switch (action)
        {
            case BelayPanelAction.Toggle:
                if (!IsBelayMode || !IsConnected || !Game.State.IsLocalPlayerInGame
                    || Game.Chat.IsTyping || Game.Input.IsPauseMenuActive) return;
                if (_swap?.Phase == ManualBelayPhase.SwapPreparing) return;
                if (Game.BelayPanel.IsOpen) Game.BelayPanel.Close();
                else if (!KeyboardCaptured) { RefreshPanel(); Game.BelayPanel.Show(); }
                return;
            case BelayPanelAction.Request: HandleChat("request " + target); break;
            case BelayPanelAction.ProposeSwap: ProposeRoleSwap(); break;
            case BelayPanelAction.AcceptSwap: AcceptRoleSwap(); break;
            case BelayPanelAction.CancelSwap: CancelRoleSwapRequest(); break;
            case BelayPanelAction.DefineStation:
                if (!IsBelayMode || !IsConnected || !Game.State.IsLocalPlayerInGame) return;
                if (_active != null || _offer != null || _offeredTarget >= 0)
                { Notice("End the current invitation or belay before changing the station."); break; }
                Game.Belay.BeginStation(out var stationReason);
                Notice(stationReason);
                break;
            case BelayPanelAction.RemoveStation:
                if (!IsBelayMode || !IsConnected || !Game.State.IsLocalPlayerInGame || !Game.Belay.HasStation) return;
                if (_active != null) { Notice("Station locked during belay. Use End belay before removing it."); break; }
                RequestStop();
                Game.Belay.EndStation();
                Notice("Station removed. Your personal piton anchor remains attached.");
                break;
            case BelayPanelAction.Offer: HandleChat("offer " + target); break;
            case BelayPanelAction.Accept: HandleChat("accept"); break;
            case BelayPanelAction.Decline:
            case BelayPanelAction.Cancel: RequestStop(); break;
            case BelayPanelAction.ToggleBrake:
                if (_active?.Belayer == LocalPlayerId) _desiredBrake = !_desiredBrake;
                break;
            case BelayPanelAction.Close:
                if (_swap?.Phase != ManualBelayPhase.SwapPreparing) Game.BelayPanel.Close();
                return;
        }
        RefreshPanel();
    }

    private void RefreshPanel()
    {
        if (RefreshRoleSwapPanel()) return;
        if (_active != null)
        {
            var partner = _active.Belayer == LocalPlayerId ? _active.Climber : _active.Belayer;
            var updating = _telemetry?.Rebinding == true;
            var state = updating ? (_telemetry.TopologyRevision == 0 ? "Connecting partner station" : "Updating protection route")
                : _active.Braked ? "Brake requested" : "Rope free";
            Game.BelayPanel.SetView(new BelayPanelView { Phase = BelayPanelPhase.Active,
                PartnerName = GetPlayerName(partner), LocalBelayer = _active.Belayer == LocalPlayerId,
                StationDefined = Game.Belay.HasStation,
                CanSwap = !_roleOfferPending && _active.Climber == LocalPlayerId && Game.Belay.CanPrepareRoleStation(_active.Session, out _),
                Braked = _desiredBrake, Description = $"{state}; {MotionLabel(_active.Motion)} requested. Pitons: {_telemetry?.ProtectionCount ?? 0}.\n"
                    + RopeReadback() + (_active.Belayer == LocalPlayerId ? "\nClose L; hold Up/Down: feed/take in."
                        : "\nPersonally anchor at the next station, then K proposes a role exchange.") });
            return;
        }
        if (_offer != null)
        {
            var incoming = _offer.Initiator != LocalPlayerId;
            var partner = _offer.Belayer == LocalPlayerId ? _offer.Climber : _offer.Belayer;
            var ready = _offer.Belayer == LocalPlayerId ? Game.Belay.CanBeginStation(out var reason)
                : Game.Belay.CanClimb(_offer.Station, out reason);
            var description = !incoming ? "Waiting for your partner to accept."
                : _offer.Belayer == LocalPlayerId ? "Your partner asks you to belay. Remain anchored when you accept."
                : "Your partner offers to belay. Accept to give them brake control.";
            if (incoming && !ready) description += " " + reason;
            Game.BelayPanel.SetView(new BelayPanelView { Phase = incoming ? BelayPanelPhase.Invitation : BelayPanelPhase.Waiting,
                StationDefined = Game.Belay.HasStation,
                PartnerName = GetPlayerName(partner), Description = description, CanAccept = ready });
            return;
        }
        if (_offeredTarget >= 0)
        {
            Game.BelayPanel.SetView(new BelayPanelView { Phase = BelayPanelPhase.Waiting,
                StationDefined = Game.Belay.HasStation,
                PartnerName = GetPlayerName(_offeredTarget), Description = _cancelPending ? "Cancelling invitation..." : "Sending invitation..." });
            return;
        }
        var partners = new List<BelayPartnerRow>();
        var capacity = Game.Belay.RopeCapacity;
        string blocked = null;
        foreach (var player in Players)
        {
            if (player.IsLocal) continue;
            if (!TryLocations(LocalPlayerId, player.Id, out var self, out var other, out var reason))
            { blocked ??= player.Name + ": " + reason; continue; }
            var squared = DistanceSquared(self, other);
            var distance = MathF.Sqrt(squared);
            if (distance <= MaximumRopeCapacity + BodyMargin)
                partners.Add(new BelayPartnerRow(player.Id, player.Name, distance,
                    ValidCapacity(capacity) && distance <= capacity + BodyMargin));
            else blocked ??= $"{player.Name}: synchronized body distance {distance:F1} m exceeds the supported rope range.";
        }
        partners.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (partners.Count == 0)
        {
            if (blocked != null && blocked != _lastPartnerBlock && Game.Time.UnscaledTime >= _nextPartnerBlockLog)
            {
                _lastPartnerBlock = blocked; _nextPartnerBlockLog = Game.Time.UnscaledTime + 2;
                FeatureLog.Info("[BelayPartner] No eligible partner; " + blocked);
            }
            Game.BelayPanel.SetView(new BelayPanelView { Phase = BelayPanelPhase.Partners,
                StationDefined = Game.Belay.HasStation,
                Partners = partners, Description = blocked ?? "You can define your current piton anchor as a station before a partner joins." });
            return;
        }
        _lastPartnerBlock = null;
        var canRequest = Game.Belay.CanClimb(out var protectionReason);
        var canOffer = Game.Belay.CanBeginStation(out var stationReason);
        var instructions = $"Choose a partner. Your rope: {capacity:F1} m; climber route checked before control.";
        if (!canRequest) instructions += " To ask: " + protectionReason;
        if (!canOffer) instructions += " To offer: " + stationReason;
        Game.BelayPanel.SetView(new BelayPanelView { Phase = BelayPanelPhase.Partners, Partners = partners,
            StationDefined = Game.Belay.HasStation,
            CanRequest = canRequest, CanOffer = canOffer, Description = instructions });
    }

    private bool TryLocations(int belayer, int climber, out PlayerLocation station, out PlayerLocation target)
        => TryLocations(belayer, climber, out station, out target, out _);

    private bool TryLocations(int belayer, int climber, out PlayerLocation station, out PlayerLocation target, out string reason)
    {
        station = target = default;
        reason = "Two different players are required.";
        if (belayer == climber) return false;
        if (!Game.Players.TryGetInteractionLocation(belayer, out station, out reason)
            || !Game.Players.TryGetInteractionLocation(climber, out target, out reason)) return false;
        reason = "A synchronized position or gameplay scene is not ready.";
        if (!ValidLocation(station) || !ValidLocation(target)) return false;
        if (station.State != PlayerState.InGame || target.State != PlayerState.InGame)
        { reason = $"Player states: {station.State}/{target.State}; both must be in active gameplay."; return false; }
        if (station.Scene != target.Scene)
        { reason = $"Different gameplay scenes: {station.Scene} / {target.Scene}."; return false; }
        reason = "";
        return true;
    }

    private static bool ValidLocation(PlayerLocation value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            && !string.IsNullOrEmpty(value.Scene);

    private static float DistanceSquared(PlayerLocation a, PlayerLocation b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);

    private static bool ValidCapacity(float capacity)
        => float.IsFinite(capacity) && capacity > 0 && capacity <= MaximumRopeCapacity;

    private static bool WithinRopeRange(PlayerLocation belayer, PlayerLocation climber, float capacity)
        => ValidCapacity(capacity) && DistanceSquared(belayer, climber) <= (capacity + BodyMargin) * (capacity + BodyMargin);

    private bool ValidStation(BelaySharedStation station, PlayerLocation belayer)
        => Game.Belay.IsStationKnown(station) && WithinStationArea(station, belayer);

    private static bool WithinStationArea(BelaySharedStation station, PlayerLocation belayer)
    {
        var position = station.Position;
        return station.IsValid && (position.X - belayer.X) * (position.X - belayer.X)
            + (position.Y - belayer.Y) * (position.Y - belayer.Y)
            + (position.Z - belayer.Z) * (position.Z - belayer.Z) <= 9;
    }

    private static string MotionLabel(int motion) => motion > 0 ? "feed" : motion < 0 ? "take-in" : "no motion";

    private string RopeReadback() => _telemetry == null ? "Awaiting climber rope readback."
        : _telemetry.Rebinding && _telemetry.TopologyRevision == 0
            ? "Connecting station: the climber must remain personally anchored and still. Rope control waits for native confirmation."
        : $"Slack {_telemetry.Slack:F1} m | reserve {_telemetry.Reserve:F1} m.\nClimber input: {MotionLabel(_telemetry.Motion)}; "
            + (_telemetry.LengthApplied ? "native length matches." : "awaiting native length update.");

    private static bool ValidRopeReport(ManualBelayCommand message)
        => (message.Rebinding && message.TopologyRevision == 0 && !message.LengthApplied && !message.Suspended
            && message.Motion == 0 && message.Released == 0 && message.Slack == 0 && message.Reserve == 0
            && message.Limit == 0 && message.RealDistance == 0 && message.AppliedSequence == 0)
            || (message.Motion is >= -1 and <= 1 && float.IsFinite(message.Released)
            && float.IsFinite(message.Slack) && float.IsFinite(message.Reserve)
            && message.Released > 0 && message.Slack >= 0 && message.Slack <= message.Released
            && message.Reserve >= 0 && message.Released + message.Reserve <= 200.001f
            && !(message.Rebinding && message.LengthApplied));

    private void Judge(HostRequest<ManualBelayCommand> request)
    {
        var message = request.Message;
        var now = Game.Time.UnscaledTime;
        if (!IsBelayMode || !float.IsFinite(now) || !Enum.IsDefined(typeof(ManualBelayOperation), message.Operation))
        { request.Reject("Manual belay experiment unavailable."); return; }
        if (!IsRoleSwapOperation(message.Operation) && message.Swap != Guid.Empty)
        { request.Reject("A role transaction cannot be attached to an ordinary rope command."); return; }
        if (message.Operation is ManualBelayOperation.Offer or ManualBelayOperation.Request)
        {
            var belayer = message.Operation == ManualBelayOperation.Offer ? request.FromPlayerId : message.Target;
            var climberId = message.Operation == ManualBelayOperation.Offer ? message.Target : request.FromPlayerId;
            if (_hostSession != null || !TryLocations(belayer, climberId, out var station, out var target)
                || !WithinRopeRange(station, target, message.Operation == ManualBelayOperation.Request
                    ? message.RopeCapacity : MaximumRopeCapacity))
            { request.Reject("Belay needs two players in the same world, within the climber's rope reach, and no other session."); return; }
            if (message.Operation == ManualBelayOperation.Offer && message.RopeCapacity != 0)
            { request.Reject("Only the climber may declare the protection rope capacity."); return; }
            if (message.Operation == ManualBelayOperation.Offer ? !ValidStation(message.Station, station) : message.Station != default)
            { request.Reject("The invitation needs the belayer's current native station."); return; }
            var offered = new SessionState { Id = Guid.NewGuid(), Belayer = belayer,
                Climber = climberId, Initiator = request.FromPlayerId, BelayerLocation = station,
                Station = message.Station, RopeCapacity = message.RopeCapacity, Until = now + 20 };
            request.AfterCommit(() => { _hostSession = offered; _changed.Send(Message(offered, ManualBelayPhase.Offered)); });
            return;
        }
        var session = _hostSession;
        if (session == null || message.Session != session.Id || now >= session.Until)
        { request.Reject("Belay session expired or replaced."); return; }
        var actor = request.FromPlayerId;
        if (message.Operation == ManualBelayOperation.Stop)
        {
            if (actor != session.Belayer && actor != session.Climber) { request.Reject("Not your belay session."); return; }
            request.AfterCommit(EndHost);
            return;
        }
        if (!TryLocations(session.Belayer, session.Climber, out var current, out var climber)
            || (session.Station.IsValid && !Game.Belay.IsStationKnown(session.Station))
            || ((session.Started || session.Initiator == session.Belayer) && !WithinStationArea(session.Station, current))
            || !WithinRopeRange(current, climber, session.RopeCapacity > 0 ? session.RopeCapacity : MaximumRopeCapacity))
        { request.Reject("Belay station moved, location became stale, or protection context changed."); return; }
        if (message.Operation == ManualBelayOperation.Accept)
        {
            var recipient = session.Initiator == session.Belayer ? session.Climber : session.Belayer;
            if (session.Started || actor != recipient)
            { request.Reject("Only the invited partner may accept."); return; }
            var capacity = actor == session.Climber ? message.RopeCapacity : session.RopeCapacity;
            if ((actor != session.Climber && message.RopeCapacity != 0) || !WithinRopeRange(current, climber, capacity))
            { request.Reject("The declared climber rope cannot cover the participant distance."); return; }
            if (!ValidStation(message.Station, current)
                || (session.Initiator == session.Belayer && message.Station != session.Station))
            { request.Reject("The agreed station is missing, replaced or outside the belayer's anchor area."); return; }
            request.AfterCommit(() =>
            {
                session.Started = true; session.Until = now + 3; session.BelayerLocation = current; session.InputAt = now;
                session.Station = message.Station;
                session.RopeCapacity = capacity;
                _changed.Send(Message(session, ManualBelayPhase.Started));
            });
            return;
        }
        if (!session.Started) { request.Reject("Climber has not accepted."); return; }
        if (IsRoleSwapOperation(message.Operation)) { JudgeRoleSwap(request, session, now, current, climber); return; }
        if (message.Operation == ManualBelayOperation.Intent)
        {
            // Two credits absorb a small delivery burst; sustained input is
            // bounded to 10 Hz, independently of frame rate and packet sequence.
            var credits = Math.Min(2, session.InputCredits + (now - session.InputAt) / InputInterval);
            if (actor != session.Belayer || message.Sequence <= session.Sequence
                || message.Motion is < -1 or > 1 || now < session.InputAt)
            { request.Reject("Unauthorized, repeated or excessive brake command."); return; }
            if (credits + .0001f < 1) { request.Reject(InputRateLimited); return; }
            if (session.Swap?.Preparing == true && (!message.Braked || message.Motion != 0))
            { request.Reject("Keep the brake closed and rope motion stopped during role preparation."); return; }
            request.AfterCommit(() =>
            {
                session.Sequence = message.Sequence; session.Braked = message.Braked;
                session.Motion = message.Motion; session.Until = now + 3;
                session.InputCredits = Math.Max(0, credits - 1); session.InputAt = now;
                _changed.Send(Message(session, ManualBelayPhase.Intent));
            });
            return;
        }
        if (message.Operation != ManualBelayOperation.Report || actor != session.Climber || !ValidRopeReport(message) || message.Sequence > session.Sequence || message.AppliedSequence > message.Sequence
            || message.TopologyRevision < session.TopologyRevision || message.TopologyRevision > 4096
            || (message.TopologyRevision == session.TopologyRevision && message.AppliedSequence < session.AppliedSequence)
            || message.ProtectionCount < 0 || message.ProtectionCount > 63 || now < session.NextReport
            || !float.IsFinite(message.Limit) || !float.IsFinite(message.RealDistance)
            || message.Limit < 0 || message.Limit > 200 || message.RealDistance < 0 || message.RealDistance > 250
            || (message.Suspended && message.Limit <= 0))
        { request.Reject("Invalid climber application report."); return; }
        request.AfterCommit(() =>
        {
            session.AppliedSequence = message.AppliedSequence; session.TopologyRevision = message.TopologyRevision; session.NextReport = now + .2f;
            var report = Message(session, ManualBelayPhase.Telemetry);
            report.Sequence = message.Sequence; report.AppliedSequence = message.AppliedSequence;
            report.Braked = message.Braked; report.Suspended = message.Suspended;
            report.Limit = message.Limit; report.RealDistance = message.RealDistance;
            report.TopologyRevision = message.TopologyRevision; report.ProtectionCount = message.ProtectionCount; report.Rebinding = message.Rebinding;
            report.Motion = message.Motion; report.Released = message.Released; report.Slack = message.Slack;
            report.Reserve = message.Reserve; report.LengthApplied = message.LengthApplied;
            _changed.Send(report);
        });
    }

    private static ManualBelayMessage Message(SessionState session, ManualBelayPhase phase)
        => new() { Phase = phase, Session = session.Id, Belayer = session.Belayer, Climber = session.Climber, Initiator = session.Initiator,
            Sequence = session.Sequence, Braked = session.Braked, Station = session.Station,
            Motion = session.Motion, RopeCapacity = session.RopeCapacity };

    private void EndHost() => EndHost("A participant ended the session.");

    private void EndHost(string reason)
    {
        if (_hostSession == null) return;
        var ended = _hostSession; _hostSession = null;
        FeatureLog.Info("[BelaySession] Host ended; session=" + ended.Id + ", reason=" + reason);
        _changed.Send(Message(ended, ManualBelayPhase.Ended));
    }

    private void Receive(ManualBelayMessage message)
    {
        if (!IsBelayMode) { ClearLocal(); return; }
        if (message.Session == Guid.Empty || (message.Belayer != LocalPlayerId && message.Climber != LocalPlayerId)) return;
        if (ReceiveRoleSwap(message)) return;
        if (message.Phase == ManualBelayPhase.Offered)
        {
            var partner = message.Belayer == LocalPlayerId ? message.Climber : message.Belayer;
            if (message.Initiator == LocalPlayerId && _offeredTarget != partner) return;
            _offer = message; _localUntil = Game.Time.UnscaledTime + 20;
            if (_cancelPending && message.Initiator == LocalPlayerId) { RequestStop(); return; }
            Notice(message.Initiator == LocalPlayerId ? "Invitation sent. Waiting for your partner."
                : $"{GetPlayerName(message.Initiator)} invites you to partner belay. Press L to respond.");
            RefreshPanel();
            if (message.Initiator != LocalPlayerId && !KeyboardCaptured && !Game.Players.IsLocalPlayerFalling) Game.BelayPanel.Show();
            return;
        }
        if (message.Phase == ManualBelayPhase.Started)
        {
            if (_offer?.Session != message.Session
                || (message.Initiator != LocalPlayerId && _accepted != message.Session)) return;
            var stationMatchesOffer = !_offer.Station.IsValid || _offer.Station == message.Station;
            _active = message; _offer = null; _sequence = 0; _desiredBrake = false;
            ResetRopeVisual();
            _sentBrake = false; _sentMotion = 0; _sendNotBefore = 0;
            _nextIntent = _nextReport = _nextHint = 0; _stopSent = false; _telemetry = null;
            _localUntil = Game.Time.UnscaledTime + 3;
            if (!message.Station.IsValid || !stationMatchesOffer || !ValidCapacity(message.RopeCapacity))
            { Notice("The accepted station changed. Send a new invitation."); RequestStop(); return; }
            if (message.Climber == LocalPlayerId)
            {
                if (!Game.Belay.BeginClimber(message.Session, message.Station, out var reason))
                { Notice(reason); RequestStop(); return; }
                if (Game.Belay.Status.Rebinding)
                    Notice("Connecting the partner station. Remain personally anchored and still until the rope readback is ready.");
            }
            if (message.Belayer == LocalPlayerId
                && (!OwnStationMatches(message.Station) || !Game.Belay.ProtectStation(message.Session)))
                RequestStop("The belayer's native station could not be protected.");
            Game.BelayPanel.Close();
            return;
        }
        if (_active?.Session != message.Session)
        {
            if (message.Phase == ManualBelayPhase.Ended && _offer?.Session == message.Session)
                ClearLocal();
            return;
        }
        if (message.Phase == ManualBelayPhase.Ended) { ClearLocal(); Notice("Belay ended."); return; }
        if (message.Station != _active.Station || message.RopeCapacity != _active.RopeCapacity) { RequestStop(); return; }
        if (message.Phase == ManualBelayPhase.Intent)
        {
            if (message.Sequence <= _active.Sequence) return;
            _active = message; _localUntil = Game.Time.UnscaledTime + 3;
            if (message.Climber == LocalPlayerId
                && !Game.Belay.SetRopeInput(message.Session, message.Sequence, message.Braked, message.Motion)) RequestStop();
        }
        else if (message.Phase == ManualBelayPhase.Telemetry
            && (_telemetry == null || (message.TopologyRevision > _telemetry.TopologyRevision)
                || (message.TopologyRevision == _telemetry.TopologyRevision && message.Sequence >= _telemetry.Sequence
                    && message.AppliedSequence >= _telemetry.AppliedSequence))) _telemetry = message;
    }

    private void Tick()
    {
        var now = Game.Time.UnscaledTime;
        if (!IsBelayMode || !IsConnected)
        {
            if (IsHost) EndHost();
            if (_active != null || _offer != null || _offeredTarget >= 0) RequestStop();
            Game.Belay.EndStation(); Game.BelayPanel.Close();
            return;
        }
        if (!Game.Input.IsWindowFocused || Game.Input.IsPauseMenuActive || Game.Time.IsPaused || Game.State.IsLocalPlayerInCutscene)
        {
            if (_active != null || _offer != null || _offeredTarget >= 0)
            {
                RequestStop("Pause, cutscene or lost window focus.");
                Notice("Belay ended for pause, cutscene or lost focus. Re-anchor and send a new invitation when ready; automatic resume is not enabled.");
            }
            Game.Belay.EndStation(); Game.BelayPanel.Close();
            return;
        }
        if (IsHost && _hostSession != null)
        {
            if (_hostSession.Swap != null && now >= _hostSession.Swap.Until) CancelHostRoleSwap(_hostSession);
            if (now >= _hostSession.Until) EndHost("Fresh partner input timed out.");
            else if (_hostSession.Station.IsValid && !Game.Belay.IsStationKnown(_hostSession.Station)) EndHost("The agreed station is no longer synchronized.");
            else if (!TryLocations(_hostSession.Belayer, _hostSession.Climber, out var hostStation, out _, out var locationReason)) EndHost("Participant location unavailable: " + locationReason);
            else if ((_hostSession.Started || _hostSession.Initiator == _hostSession.Belayer)
                && !WithinStationArea(_hostSession.Station, hostStation)) EndHost("The belayer left the station area.");
        }
        if (!IsBelayMode || !IsConnected || !Game.State.IsLocalPlayerInGame)
        { Game.BelayPanel.Close(); if (_active != null || _offer != null) RequestStop(); Game.Belay.EndStation(); return; }
        if (Game.Belay.HasStation && !Game.Belay.StationValid)
        { RequestStop("The belayer's personal piton anchor is no longer valid."); Game.Belay.EndStation(); }
        if (_offer?.Belayer == LocalPlayerId
            && _offer.Initiator == LocalPlayerId && !OwnStationMatches(_offer.Station))
        { RequestStop(); return; }
        if (Game.Input.WasPressed(GameInputAction.Panic) || Game.Input.IsPauseMenuActive || Game.Chat.IsTyping) Game.BelayPanel.Close();
        if (Game.BelayPanel.IsOpen && now >= _nextPanelRefresh) { _nextPanelRefresh = now + .2f; RefreshPanel(); }
        if (_offer != null && now >= _localUntil) { _offer = null; Game.Belay.EndStation(); _offeredTarget = -1; }
        if (_active == null) return;
        if (!IsBelayMode || !IsConnected || !Game.State.IsLocalPlayerInGame || now >= _localUntil)
        { RequestStop(); return; }
        TickRoleSwap(now);
        if (_active == null) return;
        if (_active.Belayer == LocalPlayerId)
        {
            if (!OwnStationMatches(_active.Station)) { RequestStop("The active station identity or personal anchor changed."); return; }
            if (_swap?.Phase == ManualBelayPhase.SwapPreparing) _desiredBrake = true;
            if (_swap?.Phase != ManualBelayPhase.SwapPreparing && !KeyboardCaptured && !Game.BelayPanel.IsOpen && !Game.Input.IsPauseMenuActive && !Game.Chat.IsTyping
                && (Game.Input.WasKeyPressed(GameKey.Space) || Game.Input.WasKeyPressed(GameKey.F7)))
                _desiredBrake = !_desiredBrake;
            var motion = (sbyte)0;
            if (_swap?.Phase != ManualBelayPhase.SwapPreparing && !KeyboardCaptured && !Game.BelayPanel.IsOpen && !Game.Input.IsPauseMenuActive && !Game.Chat.IsTyping && !Game.Time.IsPaused)
            {
                var up = Game.Input.IsKeyHeld(GameKey.UpArrow);
                var down = Game.Input.IsKeyHeld(GameKey.DownArrow);
                motion = (sbyte)(up == down ? 0 : down ? -1 : _desiredBrake ? 0 : 1);
            }
            var changed = _desiredBrake != _sentBrake || motion != _sentMotion;
            if (now >= _nextIntent || (changed && now >= _sendNotBefore))
            {
                _sendNotBefore = now + InputInterval;
                _nextIntent = motion == 0 ? now + .5f : _sendNotBefore;
                if (_sequence == uint.MaxValue) { RequestStop(); return; }
                _sentBrake = _desiredBrake; _sentMotion = motion;
                Send(new ManualBelayCommand { Operation = ManualBelayOperation.Intent, Session = _active.Session,
                    Sequence = ++_sequence, Braked = _desiredBrake, Motion = motion });
            }
        }
        else
        {
            var status = Game.Belay.Status;
            if (!status.Active || !status.Rope.HasValue)
            { var reason = Game.Belay.LocalExerciseNotice ?? "The native station or rope control is no longer available."; Notice(reason); RequestStop(reason); return; }
            if (now >= _nextReport)
            {
                _nextReport = now + .3f;
                var rope = status.Rope.Value;
                Send(new ManualBelayCommand { Operation = ManualBelayOperation.Report, Session = _active.Session,
                    Sequence = status.RequestedSequence, AppliedSequence = status.AppliedSequence,
                    Braked = status.RequestedBrake, Suspended = status.Suspended, Limit = status.Limit, RealDistance = status.RealDistance,
                    ProtectionCount = status.ProtectionCount, TopologyRevision = status.TopologyRevision, Rebinding = status.Rebinding,
                    Motion = (sbyte)rope.Motion, Released = rope.Released, Slack = rope.Slack,
                    Reserve = rope.Reserve, LengthApplied = rope.LengthApplied });
            }
        }
        TickRopeVisual(now);
        if (_active == null || now < _nextHint) return;
        _nextHint = now + .5f;
        var desired = _active.Braked ? "BRAKED" : "FREE";
        var applied = _telemetry?.Rebinding == true ? (_telemetry.TopologyRevision == 0 ? "connecting station; keep personal backup attached" : "updating protection route") : _telemetry?.Suspended == true
            ? (_telemetry.AppliedSequence >= _active.Sequence ? (_telemetry.Braked ? "BRAKED" : "FREE") : "awaiting confirmation")
            : "ready for the next fall";
        Game.Hud.ShowMessage("belay", $"Belay — requested {desired}; climber {applied}. "
            + RopeReadback() + (_active.Belayer == LocalPlayerId ? " Hold Up/Down: feed/take in; Space: brake/release." : " /belay stop: end")
            + (Game.Belay.InteractionNotice is { } notice ? "\n" + notice : ""), 1.2f);
    }

    private void RequestStop(string reason = "Belay ended by local request.")
    {
        var session = _active ?? _offer;
        if (session == null)
        {
            if (_offeredTarget >= 0) { _cancelPending = true; Game.Belay.EndStation(); }
            return;
        }
        if (_stopSent) return;
        FeatureLog.Info("[BelaySession] Local stop; session=" + session.Session + ", reason=" + reason);
        _stopSent = true;
        Game.Belay.Stop(session.Session);
        Game.Belay.EndStation();
        Send(new ManualBelayCommand { Operation = ManualBelayOperation.Stop, Session = session.Session });
        ClearLocal();
    }

    private void ClearLocal()
    {
        ClearRoleSwap();
        ResetRopeVisual();
        if (_active != null) Game.Belay.Stop(_active.Session);
        Game.Belay.EndStation(); Game.Hud.HideMessage("belay");
        Game.BelayPanel.Close();
        _active = _offer = _telemetry = null; _accepted = Guid.Empty; _offeredTarget = -1; _stopSent = _cancelPending = false;
    }

    private Stream<BelayRopeVisualFrame> _ropeVisual;
    private uint _visualSequence, _receivedVisualSequence;
    private float _nextVisual, _visualUntil, _visualAcceptAt;

    private void TickRopeVisual(float now)
    {
        if (_active?.Climber == LocalPlayerId && now >= _nextVisual && _visualSequence < uint.MaxValue)
        {
            _nextVisual = now + .1f;
            var ready = Game.Belay.TryCaptureRopeVisual(_active.Session, out var points);
            _ropeVisual.Send(new BelayRopeVisualFrame { Session = _active.Session, Sequence = ++_visualSequence,
                Revision = Game.Belay.Status.TopologyRevision, Points = ready ? points : Array.Empty<WorldPosition>() });
        }
        if (_visualUntil > 0 && now >= _visualUntil)
        {
            Game.Belay.ClearRopeVisual();
            _visualUntil = 0;
        }
    }

    private void ReceiveRopeVisual(int sender, BelayRopeVisualFrame frame)
    {
        var now = Game.Time.UnscaledTime;
        // This stream never grants consent, renews a brake lease or changes physics.
        if (!IsBelayMode || !IsConnected || _active?.Belayer != LocalPlayerId || sender != _active.Climber
            || frame.Session != _active.Session || frame.Sequence <= _receivedVisualSequence
            || !Game.State.IsLocalPlayerInGame || Game.State.IsLocalPlayerInCutscene
            || !Game.Input.IsWindowFocused || Game.Input.IsPauseMenuActive || Game.Time.IsPaused
            || !float.IsFinite(now) || now < _visualAcceptAt
            || !TryLocations(_active.Belayer, _active.Climber, out _, out var climber, out _)
            || !frame.MatchesRoute(_active.Station.Position, new(climber.X, climber.Y, climber.Z), _active.RopeCapacity)) return;
        _receivedVisualSequence = frame.Sequence;
        _visualAcceptAt = now + .04f;
        if (frame.Points.Length == 0) { Game.Belay.ClearRopeVisual(); _visualUntil = 0; return; }
        Game.Belay.ShowRopeVisual(frame.Session, sender, frame.Revision, frame.Points);
        _visualUntil = now + .75f;
    }

    private void ResetRopeVisual()
    {
        Game.Belay.ClearRopeVisual();
        _visualSequence = _receivedVisualSequence = 0;
        _nextVisual = _visualUntil = _visualAcceptAt = 0;
    }

    private void Notice(string message)
    {
        Game.Chat.AddSystemLine(message);
        Game.Hud.ShowMessage("notice", message, 5);
    }
}
internal enum ManualBelayOperation : byte { Offer, Accept, Intent, Report, Stop, Request, ProposeSwap, AcceptSwap, CancelSwap, ReadySwap }
internal enum ManualBelayPhase : byte { Offered, Started, Intent, Telemetry, Ended, SwapOffered, SwapPreparing, SwapCancelled, SwapCommitted }

/// <summary>Fixed-size requests. Actor identity always comes from the transport.</summary>
internal sealed class ManualBelayCommand : IPacket
{
    public ManualBelayOperation Operation;
    public Guid Session;
    public Guid Swap;
    public int Target;
    public uint Sequence, AppliedSequence;
    public bool Braked, Suspended;
    public float Limit, RealDistance;
    public int ProtectionCount;
    public uint TopologyRevision;
    public bool Rebinding;
    public BelaySharedStation Station;
    public sbyte Motion;
    public float Released, Slack, Reserve;
    public float RopeCapacity;
    public bool LengthApplied;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write((byte)Operation); writer.Write(Session.ToByteArray()); writer.Write(Target);
        writer.Write(Sequence); writer.Write(AppliedSequence); writer.Write(Braked); writer.Write(Suspended);
        writer.Write(Limit); writer.Write(RealDistance);
        writer.Write(ProtectionCount); writer.Write(TopologyRevision); writer.Write(Rebinding);
        writer.Write(Station.Id.ToByteArray()); writer.Write(Station.Position.X); writer.Write(Station.Position.Y); writer.Write(Station.Position.Z);
        writer.Write(Station.PitonOwner); writer.Write(Station.PitonId);
        writer.Write(Motion); writer.Write(Released); writer.Write(Slack); writer.Write(Reserve); writer.Write(LengthApplied);
        writer.Write(RopeCapacity);
        writer.Write(Swap.ToByteArray());
    }
    public void Deserialize(BinaryReader reader)
    {
        if (reader.BaseStream.Length - reader.BaseStream.Position != 118) throw new InvalidDataException("Invalid belay request size.");
        Operation = (ManualBelayOperation)reader.ReadByte(); Session = new Guid(reader.ReadBytes(16)); Target = reader.ReadInt32();
        Sequence = reader.ReadUInt32(); AppliedSequence = reader.ReadUInt32(); Braked = reader.ReadBoolean(); Suspended = reader.ReadBoolean();
        Limit = reader.ReadSingle(); RealDistance = reader.ReadSingle();
        ProtectionCount = reader.ReadInt32(); TopologyRevision = reader.ReadUInt32(); Rebinding = reader.ReadBoolean();
        Station = new BelaySharedStation(new Guid(reader.ReadBytes(16)), new WorldPosition(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), reader.ReadInt32(), reader.ReadUInt32());
        Motion = reader.ReadSByte(); Released = reader.ReadSingle(); Slack = reader.ReadSingle(); Reserve = reader.ReadSingle(); LengthApplied = reader.ReadBoolean();
        RopeCapacity = reader.ReadSingle();
        Swap = new Guid(reader.ReadBytes(16));
    }
}

/// <summary>Host accepted intent and climber-applied telemetry remain distinct.</summary>
internal sealed class ManualBelayMessage : IPacket
{
    public ManualBelayPhase Phase;
    public Guid Session;
    public Guid Swap;
    public int Belayer, Climber, Initiator;
    public uint Sequence, AppliedSequence;
    public bool Braked, Suspended;
    public float Limit, RealDistance;
    public int ProtectionCount;
    public uint TopologyRevision;
    public bool Rebinding;
    public BelaySharedStation Station;
    public sbyte Motion;
    public float Released, Slack, Reserve;
    public float RopeCapacity;
    public bool LengthApplied;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write((byte)Phase); writer.Write(Session.ToByteArray()); writer.Write(Belayer); writer.Write(Climber);
        writer.Write(Sequence); writer.Write(AppliedSequence); writer.Write(Braked); writer.Write(Suspended);
        writer.Write(Limit); writer.Write(RealDistance);
        writer.Write(Initiator); writer.Write(ProtectionCount); writer.Write(TopologyRevision); writer.Write(Rebinding);
        writer.Write(Station.Id.ToByteArray()); writer.Write(Station.Position.X); writer.Write(Station.Position.Y); writer.Write(Station.Position.Z);
        writer.Write(Station.PitonOwner); writer.Write(Station.PitonId);
        writer.Write(Motion); writer.Write(Released); writer.Write(Slack); writer.Write(Reserve); writer.Write(LengthApplied);
        writer.Write(RopeCapacity);
        writer.Write(Swap.ToByteArray());
    }
    public void Deserialize(BinaryReader reader)
    {
        if (reader.BaseStream.Length - reader.BaseStream.Position != 126) throw new InvalidDataException("Invalid belay message size.");
        Phase = (ManualBelayPhase)reader.ReadByte(); Session = new Guid(reader.ReadBytes(16));
        Belayer = reader.ReadInt32(); Climber = reader.ReadInt32(); Sequence = reader.ReadUInt32(); AppliedSequence = reader.ReadUInt32();
        Braked = reader.ReadBoolean(); Suspended = reader.ReadBoolean(); Limit = reader.ReadSingle(); RealDistance = reader.ReadSingle();
        Initiator = reader.ReadInt32(); ProtectionCount = reader.ReadInt32(); TopologyRevision = reader.ReadUInt32(); Rebinding = reader.ReadBoolean();
        Station = new BelaySharedStation(new Guid(reader.ReadBytes(16)), new WorldPosition(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), reader.ReadInt32(), reader.ReadUInt32());
        Motion = reader.ReadSByte(); Released = reader.ReadSingle(); Slack = reader.ReadSingle(); Reserve = reader.ReadSingle(); LengthApplied = reader.ReadBoolean();
        RopeCapacity = reader.ReadSingle();
        Swap = new Guid(reader.ReadBytes(16));
    }
}

/// <summary>Optional, bounded drawing samples; unknown stream channels are ignored by older peers.</summary>
internal sealed class BelayRopeVisualFrame : IPacket
{
    internal const int MaximumPoints = 192;
    public Guid Session;
    public uint Sequence, Revision;
    public WorldPosition[] Points = Array.Empty<WorldPosition>();

    public void Serialize(BinaryWriter writer)
    {
        if (Points == null || Points.Length == 1 || Points.Length > MaximumPoints)
            throw new InvalidDataException("Invalid belay rope drawing size.");
        writer.Write(Session.ToByteArray()); writer.Write(Sequence); writer.Write(Revision);
        writer.Write((ushort)Points.Length);
        foreach (var point in Points) { writer.Write(point.X); writer.Write(point.Y); writer.Write(point.Z); }
    }

    public void Deserialize(BinaryReader reader)
    {
        if (reader.BaseStream.Length - reader.BaseStream.Position < 26) throw new InvalidDataException("Incomplete rope drawing.");
        Session = new Guid(reader.ReadBytes(16)); Sequence = reader.ReadUInt32(); Revision = reader.ReadUInt32();
        var count = reader.ReadUInt16();
        if (count == 1 || count > MaximumPoints || reader.BaseStream.Length - reader.BaseStream.Position != count * 12)
            throw new InvalidDataException("Invalid rope drawing payload length.");
        Points = new WorldPosition[count];
        for (var i = 0; i < count; i++) Points[i] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    internal bool MatchesRoute(WorldPosition station, WorldPosition climber, float capacity)
    {
        if (Session == Guid.Empty || Sequence == 0 || !float.IsFinite(capacity) || capacity <= 0 || capacity > 200
            || Points == null || Points.Length == 1 || Points.Length > MaximumPoints) return false;
        if (Points.Length == 0) return true;
        if (!Finite(station) || !Finite(climber) || !Finite(Points[0]) || !Finite(Points[^1])
            || Distance(station, Points[0]) > 1 || Distance(climber, Points[^1]) > 5) return false;
        var maximum = capacity * 2 + 3; // Native rope can stretch during a catch.
        var length = 0f;
        for (var i = 0; i < Points.Length; i++)
        {
            if (!Finite(Points[i]) || Distance(station, Points[i]) > maximum) return false;
            if (i > 0) length += Distance(Points[i - 1], Points[i]);
            if (!float.IsFinite(length) || length > maximum) return false;
        }
        return true;
    }

    private static bool Finite(WorldPosition p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static float Distance(WorldPosition a, WorldPosition b)
        => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
}
