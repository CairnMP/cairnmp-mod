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
internal sealed class ManualBelayFeature : MultiplayerFeature
{
    private sealed class SessionState
    {
        internal Guid Id;
        internal int Belayer, Climber, Initiator;
        internal bool Started, Braked;
        internal uint Sequence, AppliedSequence;
        internal uint TopologyRevision;
        internal float Until, NextIntent, NextReport;
        internal PlayerLocation Station;
    }

    private HostCommand<ManualBelayCommand> _command;
    private HostEvent<ManualBelayMessage> _changed;
    private SessionState _hostSession;
    private ManualBelayMessage _offer, _active, _telemetry;
    private Guid _accepted;
    private int _offeredTarget = -1;
    private bool _desiredBrake;
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
            var ready = _offer.Belayer == LocalPlayerId
                ? Game.Belay.BeginStation(out var reason) : Game.Belay.CanClimb(out reason);
            if (!ready) { Notice(reason); return; }
            _accepted = _offer.Session;
            Send(new ManualBelayCommand { Operation = ManualBelayOperation.Accept, Session = _accepted });
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
        var canSend = asking ? Game.Belay.CanClimb(out var failure) : Game.Belay.BeginStation(out failure);
        if (!canSend) { Notice(failure); return; }
        _offeredTarget = target;
        Send(new ManualBelayCommand { Operation = asking ? ManualBelayOperation.Request : ManualBelayOperation.Offer, Target = target });
    }

    private void Send(ManualBelayCommand command)
    {
        _command.Send(command, (accepted, reason) =>
        {
            if (accepted) return; // Acceptance by the host is not physical application.
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
            case BelayPanelAction.Request: HandleChat("request " + target); break;
            case BelayPanelAction.Offer: HandleChat("offer " + target); break;
            case BelayPanelAction.Accept: HandleChat("accept"); break;
            case BelayPanelAction.Decline:
            case BelayPanelAction.Cancel: RequestStop(); break;
            case BelayPanelAction.ToggleBrake:
                if (_active?.Belayer == LocalPlayerId) { _desiredBrake = !_desiredBrake; _nextIntent = 0; }
                break;
            case BelayPanelAction.Close: Game.BelayPanel.Close(); break;
        }
        RefreshPanel();
    }

    private void RefreshPanel()
    {
        if (_active != null)
        {
            var partner = _active.Belayer == LocalPlayerId ? _active.Climber : _active.Belayer;
            var updating = _telemetry?.Rebinding == true;
            var state = updating ? "Updating protection route" : _active.Braked ? "Brake requested" : "Rope free";
            Game.BelayPanel.SetView(new BelayPanelView { Phase = BelayPanelPhase.Active,
                PartnerName = GetPlayerName(partner), LocalBelayer = _active.Belayer == LocalPlayerId,
                Braked = _desiredBrake, Description = $"{state}. Protection pitons: {_telemetry?.ProtectionCount ?? 0}." });
            return;
        }
        if (_offer != null)
        {
            var incoming = _offer.Initiator != LocalPlayerId;
            var partner = _offer.Belayer == LocalPlayerId ? _offer.Climber : _offer.Belayer;
            var ready = _offer.Belayer == LocalPlayerId ? Game.Belay.CanBeginStation(out var reason) : Game.Belay.CanClimb(out reason);
            var description = !incoming ? "Waiting for your partner to accept."
                : _offer.Belayer == LocalPlayerId ? "Your partner asks you to belay. Remain anchored when you accept."
                : "Your partner offers to belay. Accept to give them brake control.";
            if (incoming && !ready) description += " " + reason;
            Game.BelayPanel.SetView(new BelayPanelView { Phase = incoming ? BelayPanelPhase.Invitation : BelayPanelPhase.Waiting,
                PartnerName = GetPlayerName(partner), Description = description, CanAccept = ready });
            return;
        }
        if (_offeredTarget >= 0)
        {
            Game.BelayPanel.SetView(new BelayPanelView { Phase = BelayPanelPhase.Waiting,
                PartnerName = GetPlayerName(_offeredTarget), Description = _cancelPending ? "Cancelling invitation..." : "Sending invitation..." });
            return;
        }
        var partners = new List<BelayPartnerRow>();
        foreach (var player in Players)
        {
            if (player.IsLocal || !TryLocations(LocalPlayerId, player.Id, out var self, out var other)) continue;
            var squared = DistanceSquared(self, other);
            if (squared <= 36) partners.Add(new BelayPartnerRow(player.Id, player.Name, MathF.Sqrt(squared)));
        }
        partners.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (partners.Count == 0)
        {
            Game.BelayPanel.SetView(new BelayPanelView { Phase = BelayPanelPhase.Partners,
                Partners = partners, Description = "Choose a nearby partner when they join this area." });
            return;
        }
        var canRequest = Game.Belay.CanClimb(out var protectionReason);
        var canOffer = Game.Belay.CanBeginStation(out var stationReason);
        var instructions = "Choose a nearby partner.";
        if (!canRequest) instructions += " To ask: " + protectionReason;
        if (!canOffer) instructions += " To offer: " + stationReason;
        Game.BelayPanel.SetView(new BelayPanelView { Phase = BelayPanelPhase.Partners, Partners = partners,
            CanRequest = canRequest, CanOffer = canOffer, Description = instructions });
    }

    private bool TryLocations(int belayer, int climber, out PlayerLocation station, out PlayerLocation target)
    {
        station = target = default;
        return belayer != climber && Game.Players.TryGetLocation(belayer, out station)
            && Game.Players.TryGetLocation(climber, out target)
            && ValidLocation(station) && ValidLocation(target)
            && station.State == PlayerState.InGame && target.State == PlayerState.InGame
            && station.Scene == target.Scene;
    }

    private static bool ValidLocation(PlayerLocation value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            && !string.IsNullOrEmpty(value.Scene);

    private static float DistanceSquared(PlayerLocation a, PlayerLocation b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);

    private void Judge(HostRequest<ManualBelayCommand> request)
    {
        var message = request.Message;
        var now = Game.Time.UnscaledTime;
        if (!IsBelayMode || !Enum.IsDefined(typeof(ManualBelayOperation), message.Operation))
        { request.Reject("Manual belay experiment unavailable."); return; }
        if (message.Operation is ManualBelayOperation.Offer or ManualBelayOperation.Request)
        {
            var belayer = message.Operation == ManualBelayOperation.Offer ? request.FromPlayerId : message.Target;
            var climberId = message.Operation == ManualBelayOperation.Offer ? message.Target : request.FromPlayerId;
            if (_hostSession != null || !TryLocations(belayer, climberId, out var station, out var target)
                || DistanceSquared(station, target) > 36)
            { request.Reject("Belay needs two nearby players in the same scene and no other session."); return; }
            var offered = new SessionState { Id = Guid.NewGuid(), Belayer = belayer,
                Climber = climberId, Initiator = request.FromPlayerId, Station = station, Until = now + 20 };
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
            || ((session.Started || session.Initiator == session.Belayer) && DistanceSquared(current, session.Station) > .25f)
            || DistanceSquared(current, climber) > 1600)
        { request.Reject("Belay station moved, location became stale, or protection context changed."); return; }
        if (message.Operation == ManualBelayOperation.Accept)
        {
            var recipient = session.Initiator == session.Belayer ? session.Climber : session.Belayer;
            if (session.Started || actor != recipient || DistanceSquared(current, climber) > 36)
            { request.Reject("Only the invited nearby partner may accept."); return; }
            request.AfterCommit(() =>
            {
                session.Started = true; session.Until = now + 3; session.Station = current;
                _changed.Send(Message(session, ManualBelayPhase.Started));
            });
            return;
        }
        if (!session.Started) { request.Reject("Climber has not accepted."); return; }
        if (message.Operation == ManualBelayOperation.Intent)
        {
            if (actor != session.Belayer || message.Sequence <= session.Sequence || now < session.NextIntent)
            { request.Reject("Unauthorized, repeated or excessive brake command."); return; }
            request.AfterCommit(() =>
            {
                session.Sequence = message.Sequence; session.Braked = message.Braked;
                session.Until = now + 3; session.NextIntent = now + .05f;
                _changed.Send(Message(session, ManualBelayPhase.Intent));
            });
            return;
        }
        if (actor != session.Climber || message.Sequence > session.Sequence || message.AppliedSequence > message.Sequence
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
            _changed.Send(report);
        });
    }

    private static ManualBelayMessage Message(SessionState session, ManualBelayPhase phase)
        => new() { Phase = phase, Session = session.Id, Belayer = session.Belayer, Climber = session.Climber, Initiator = session.Initiator,
            Sequence = session.Sequence, Braked = session.Braked };

    private void EndHost()
    {
        if (_hostSession == null) return;
        var ended = _hostSession; _hostSession = null;
        _changed.Send(Message(ended, ManualBelayPhase.Ended));
    }

    private void Receive(ManualBelayMessage message)
    {
        if (!IsBelayMode) { ClearLocal(); return; }
        if (message.Session == Guid.Empty || (message.Belayer != LocalPlayerId && message.Climber != LocalPlayerId)) return;
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
            _active = message; _offer = null; _sequence = 0; _desiredBrake = false;
            _nextIntent = _nextReport = _nextHint = 0; _stopSent = false; _telemetry = null;
            _localUntil = Game.Time.UnscaledTime + 3;
            if (message.Climber == LocalPlayerId && !Game.Belay.BeginClimber(message.Session, out var reason))
            { Notice(reason); RequestStop(); }
            if (message.Belayer == LocalPlayerId && !Game.Belay.StationValid) RequestStop();
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
        if (message.Phase == ManualBelayPhase.Intent)
        {
            if (message.Sequence <= _active.Sequence) return;
            _active = message; _localUntil = Game.Time.UnscaledTime + 3;
            if (message.Climber == LocalPlayerId && !Game.Belay.SetBrake(message.Session, message.Sequence, message.Braked)) RequestStop();
        }
        else if (message.Phase == ManualBelayPhase.Telemetry
            && (_telemetry == null || (message.TopologyRevision > _telemetry.TopologyRevision)
                || (message.TopologyRevision == _telemetry.TopologyRevision && message.Sequence >= _telemetry.Sequence
                    && message.AppliedSequence >= _telemetry.AppliedSequence))) _telemetry = message;
    }

    private void Tick()
    {
        var now = Game.Time.UnscaledTime;
        if (IsHost && _hostSession != null && (!IsBelayMode || now >= _hostSession.Until
            || !TryLocations(_hostSession.Belayer, _hostSession.Climber, out var hostStation, out _)
            || ((_hostSession.Started || _hostSession.Initiator == _hostSession.Belayer)
                && DistanceSquared(hostStation, _hostSession.Station) > .25f))) EndHost();
        if (!IsBelayMode || !IsConnected || !Game.State.IsLocalPlayerInGame)
        { Game.BelayPanel.Close(); if (_active != null || _offer != null) RequestStop(); return; }
        if (Game.Input.WasPressed(GameInputAction.Panic) || Game.Input.IsPauseMenuActive || Game.Chat.IsTyping) Game.BelayPanel.Close();
        else if ((!KeyboardCaptured || Game.BelayPanel.IsOpen) && Game.Input.WasKeyPressed(GameKey.L))
        {
            if (Game.BelayPanel.IsOpen) Game.BelayPanel.Close();
            else { RefreshPanel(); Game.BelayPanel.Show(); }
        }
        if (Game.BelayPanel.IsOpen && now >= _nextPanelRefresh) { _nextPanelRefresh = now + .2f; RefreshPanel(); }
        if (_offer != null && now >= _localUntil) { _offer = null; Game.Belay.EndStation(); _offeredTarget = -1; }
        if (_active == null) return;
        if (!IsBelayMode || !IsConnected || !Game.State.IsLocalPlayerInGame || now >= _localUntil)
        { RequestStop(); return; }
        if (_active.Belayer == LocalPlayerId)
        {
            if (!Game.Belay.StationValid) { RequestStop(); return; }
            if (!KeyboardCaptured && !Game.Input.IsPauseMenuActive
                && (Game.Input.WasKeyPressed(GameKey.Space) || Game.Input.WasKeyPressed(GameKey.F7)))
            { _desiredBrake = !_desiredBrake; _nextIntent = 0; }
            if (now >= _nextIntent)
            {
                _nextIntent = now + .5f;
                if (_sequence == uint.MaxValue) { RequestStop(); return; }
                Send(new ManualBelayCommand { Operation = ManualBelayOperation.Intent, Session = _active.Session,
                    Sequence = ++_sequence, Braked = _desiredBrake });
            }
        }
        else
        {
            var status = Game.Belay.Status;
            if (!status.Active) { RequestStop(); return; }
            if (now >= _nextReport)
            {
                _nextReport = now + .3f;
                Send(new ManualBelayCommand { Operation = ManualBelayOperation.Report, Session = _active.Session,
                    Sequence = status.RequestedSequence, AppliedSequence = status.AppliedSequence,
                    Braked = status.RequestedBrake, Suspended = status.Suspended, Limit = status.Limit, RealDistance = status.RealDistance,
                    ProtectionCount = status.ProtectionCount, TopologyRevision = status.TopologyRevision, Rebinding = status.Rebinding });
            }
        }
        if (_active == null || now < _nextHint) return;
        _nextHint = now + .5f;
        var desired = _active.Braked ? "BRAKED" : "FREE";
        var applied = _telemetry?.Rebinding == true ? "updating protection route" : _telemetry?.Suspended == true
            ? (_telemetry.AppliedSequence >= _active.Sequence ? (_telemetry.Braked ? "BRAKED" : "FREE") : "awaiting confirmation")
            : "ready for the next fall";
        Game.Hud.ShowMessage("belay", $"Belay — requested {desired}; climber {applied}. "
            + (_active.Belayer == LocalPlayerId ? "Space: brake/release" : "/belay stop: end"), 1.2f);
    }

    private void RequestStop()
    {
        var session = _active ?? _offer;
        if (session == null)
        {
            if (_offeredTarget >= 0) { _cancelPending = true; Game.Belay.EndStation(); }
            return;
        }
        if (_stopSent) return;
        _stopSent = true;
        Game.Belay.Stop(session.Session);
        Game.Belay.EndStation();
        Send(new ManualBelayCommand { Operation = ManualBelayOperation.Stop, Session = session.Session });
        ClearLocal();
    }

    private void ClearLocal()
    {
        if (_active != null) Game.Belay.Stop(_active.Session);
        Game.Belay.EndStation(); Game.Hud.HideMessage("belay");
        Game.BelayPanel.Close();
        _active = _offer = _telemetry = null; _accepted = Guid.Empty; _offeredTarget = -1; _stopSent = _cancelPending = false;
    }

    private void Notice(string message)
    {
        Game.Chat.AddSystemLine(message);
        Game.Hud.ShowMessage("notice", message, 5);
    }
}
internal enum ManualBelayOperation : byte { Offer, Accept, Intent, Report, Stop, Request }
internal enum ManualBelayPhase : byte { Offered, Started, Intent, Telemetry, Ended }

/// <summary>Fixed-size requests. Actor identity always comes from the transport.</summary>
internal sealed class ManualBelayCommand : IPacket
{
    public ManualBelayOperation Operation;
    public Guid Session;
    public int Target;
    public uint Sequence, AppliedSequence;
    public bool Braked, Suspended;
    public float Limit, RealDistance;
    public int ProtectionCount;
    public uint TopologyRevision;
    public bool Rebinding;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write((byte)Operation); writer.Write(Session.ToByteArray()); writer.Write(Target);
        writer.Write(Sequence); writer.Write(AppliedSequence); writer.Write(Braked); writer.Write(Suspended);
        writer.Write(Limit); writer.Write(RealDistance);
        writer.Write(ProtectionCount); writer.Write(TopologyRevision); writer.Write(Rebinding);
    }
    public void Deserialize(BinaryReader reader)
    {
        if (reader.BaseStream.Length - reader.BaseStream.Position != 48) throw new InvalidDataException("Invalid belay request size.");
        Operation = (ManualBelayOperation)reader.ReadByte(); Session = new Guid(reader.ReadBytes(16)); Target = reader.ReadInt32();
        Sequence = reader.ReadUInt32(); AppliedSequence = reader.ReadUInt32(); Braked = reader.ReadBoolean(); Suspended = reader.ReadBoolean();
        Limit = reader.ReadSingle(); RealDistance = reader.ReadSingle();
        ProtectionCount = reader.ReadInt32(); TopologyRevision = reader.ReadUInt32(); Rebinding = reader.ReadBoolean();
    }
}

/// <summary>Host accepted intent and climber-applied telemetry remain distinct.</summary>
internal sealed class ManualBelayMessage : IPacket
{
    public ManualBelayPhase Phase;
    public Guid Session;
    public int Belayer, Climber, Initiator;
    public uint Sequence, AppliedSequence;
    public bool Braked, Suspended;
    public float Limit, RealDistance;
    public int ProtectionCount;
    public uint TopologyRevision;
    public bool Rebinding;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write((byte)Phase); writer.Write(Session.ToByteArray()); writer.Write(Belayer); writer.Write(Climber);
        writer.Write(Sequence); writer.Write(AppliedSequence); writer.Write(Braked); writer.Write(Suspended);
        writer.Write(Limit); writer.Write(RealDistance);
        writer.Write(Initiator); writer.Write(ProtectionCount); writer.Write(TopologyRevision); writer.Write(Rebinding);
    }
    public void Deserialize(BinaryReader reader)
    {
        if (reader.BaseStream.Length - reader.BaseStream.Position != 56) throw new InvalidDataException("Invalid belay message size.");
        Phase = (ManualBelayPhase)reader.ReadByte(); Session = new Guid(reader.ReadBytes(16));
        Belayer = reader.ReadInt32(); Climber = reader.ReadInt32(); Sequence = reader.ReadUInt32(); AppliedSequence = reader.ReadUInt32();
        Braked = reader.ReadBoolean(); Suspended = reader.ReadBoolean(); Limit = reader.ReadSingle(); RealDistance = reader.ReadSingle();
        Initiator = reader.ReadInt32(); ProtectionCount = reader.ReadInt32(); TopologyRevision = reader.ReadUInt32(); Rebinding = reader.ReadBoolean();
    }
}
