using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Extensions;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class ManualBelayFeatureTests
{
    [Theory]
    [InlineData(MultiplayerMode.RopeTeam)]
    [InlineData(MultiplayerMode.FreeSolo)]
    [InlineData(MultiplayerMode.Race)]
    public void OtherModesCannotOpenThePanelOrStartPartnerBelay(MultiplayerMode mode)
    {
        using var world = new World { Mode = mode };
        var game = world.Nodes[1].Game;
        game.Keys.Key = GameKey.L; game.Keys.Pressed = true;
        world.Tick();
        Assert.False(game.PanelImpl.IsOpen);
        game.ChatImpl.Run("offer 2");
        Assert.Empty(world.Messages);
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Offer, Target = 2 }).Committed);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.BeginCalls);
    }

    [Fact]
    public void LeavingBelayTeamEndsTheControllerAndRejectsOldSessionEvents()
    {
        using var world = World.Started();
        var started = world.Events.Last(e => Decode(e).Phase == ManualBelayPhase.Started);
        world.Mode = MultiplayerMode.RopeTeam;
        world.Tick();
        var game = world.Nodes[2].Game;
        Assert.False(game.BelayImpl.Active);
        Assert.False(game.PanelImpl.IsOpen);
        var calls = game.BelayImpl.BeginCalls;
        world.Nodes[2].Runtime.ApplyEvent(started);
        Assert.Equal(calls, game.BelayImpl.BeginCalls);
    }

    [Fact]
    public void AnEmptyLobbyPanelDoesNotQueryNativeRopeOrStationReadiness()
    {
        using var world = new World(participants: 1);
        var game = world.Nodes[1].Game;
        game.Keys.Key = GameKey.L; game.Keys.Pressed = true;
        world.Tick();
        Assert.True(game.PanelImpl.IsOpen);
        Assert.Empty(game.PanelImpl.View.Partners);
        Assert.False(game.PanelImpl.View.CanOffer);
        Assert.False(game.PanelImpl.View.CanRequest);
        Assert.Equal(0, game.BelayImpl.ReadinessChecks);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void ConsentAndRoleReversalApplyOnlyOnTheClimber(int belayer, int climber)
    {
        using var world = new World();
        world.Nodes[belayer].Game.ChatImpl.Run("offer " + climber);
        Assert.Equal(0, world.Nodes[climber].Game.BelayImpl.BeginCalls);
        Assert.Equal(0, world.Nodes[belayer].Game.BelayImpl.BeginCalls);
        world.Nodes[climber].Game.ChatImpl.Run("accept");
        world.Tick();
        Assert.Equal(1, world.Nodes[climber].Game.BelayImpl.BeginCalls);
        Assert.Equal(0, world.Nodes[belayer].Game.BelayImpl.BeginCalls);
        Assert.False(world.Nodes[climber].Game.BelayImpl.LastBrake);
        world.Time += .1f;
        world.Nodes[belayer].Game.Keys.Pressed = true;
        world.Tick();
        Assert.True(world.Nodes[climber].Game.BelayImpl.LastBrake);
        Assert.Equal(0, world.Nodes[belayer].Game.BelayImpl.SetCalls);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void AThirdPlayerCannotAcceptOperateStopOrReportForThePair()
    {
        using var world = new World();
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        var id = world.Last(ManualBelayPhase.Offered).Session;
        Assert.False(world.Execute(3, new() { Operation = ManualBelayOperation.Accept, Session = id }).Committed);
        world.Nodes[2].Game.ChatImpl.Run("accept");
        world.Tick();
        foreach (var operation in new[] { ManualBelayOperation.Intent, ManualBelayOperation.Report, ManualBelayOperation.Stop })
            Assert.False(world.Execute(3, new() { Operation = operation, Session = id, Sequence = 50 }).Committed);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.False(world.Nodes[2].Game.BelayImpl.LastBrake);
    }

    [Fact]
    public void NeitherOldNorDuplicateIntentCanReleaseARecordedBrake()
    {
        using var world = World.Started();
        var id = world.Last(ManualBelayPhase.Started).Session;
        world.Time += .1f;
        Assert.True(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = id, Sequence = 9, Braked = true }).Committed);
        world.Time += .1f;
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = id, Sequence = 9 }).Committed);
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = id, Sequence = 8 }).Committed);
        Assert.True(world.Nodes[2].Game.BelayImpl.LastBrake);
    }

    [Fact]
    public void IntentBeforeConsentAndRequestsFromTheWrongSessionAreRejected()
    {
        using var world = new World();
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        var id = world.Last(ManualBelayPhase.Offered).Session;
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = id, Sequence = 1 }).Committed);
        world.Nodes[2].Game.ChatImpl.Run("accept");
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = Guid.NewGuid(), Sequence = 1 }).Committed);
    }

    [Fact]
    public void LostInputExpiresAndAReplayCannotStartAnotherFall()
    {
        using var world = World.Started();
        var id = world.Last(ManualBelayPhase.Started).Session;
        world.Time += 3.1f;
        world.Tick();
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = id, Sequence = 999 }).Committed);
    }

    [Fact]
    public void LeavingTheStationOrLosingNativeAnchoringStopsThePair()
    {
        using (var world = World.Started())
        {
            world.Locations[1] = new PlayerLocation(.6f, 0, 0, "Wall", PlayerState.InGame);
            world.Tick();
            Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        }
        using (var world = World.Started())
        {
            world.Nodes[1].Game.BelayImpl.Anchored = false;
            world.Tick();
            Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        }
    }

    [Fact]
    public void SceneChangesNeedFreshConsentAndCannotReuseAStartedEvent()
    {
        using var world = World.Started();
        var started = world.Events.Last(e => Decode(e).Phase == ManualBelayPhase.Started);
        world.Nodes[2].Host.NotifySceneReset();
        world.Nodes[2].Runtime.ApplyEvent(started);
        Assert.Equal(1, world.Nodes[2].Game.BelayImpl.BeginCalls);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
    }

    [Fact]
    public void RemovingEitherParticipantDropsTheNativeController()
    {
        using var world = World.Started();
        world.Nodes[1].Runtime.NotifyPlayerLeft(new MultiplayerPlayer(2, "P2", false, false));
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
    }

    [Fact]
    public void TypingDoesNotToggleTheBrakeAndAHostVerdictDoesNotClaimPhysicsApplied()
    {
        using var world = World.Started();
        world.Time += .1f;
        world.Nodes[1].Game.Keys.Captured = world.Nodes[1].Game.Keys.Pressed = true;
        world.Tick();
        Assert.False(world.Nodes[2].Game.BelayImpl.LastBrake);
        Assert.DoesNotContain(world.Messages, m => m.AppliedSequence > 0);
    }

    [Fact]
    public void NativeAcquisitionFailureEndsTheSessionWithoutAssigningAnotherClimber()
    {
        using var world = new World();
        world.Nodes[2].Game.BelayImpl.CanBegin = false;
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        world.Nodes[2].Game.ChatImpl.Run("accept");
        world.Tick();
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
        Assert.Equal(0, world.Nodes[3].Game.BelayImpl.BeginCalls);
    }

    [Theory]
    [InlineData(float.NaN, 1f, 1u)]
    [InlineData(201f, 1f, 1u)]
    [InlineData(1f, float.PositiveInfinity, 1u)]
    [InlineData(1f, -1f, 1u)]
    [InlineData(1f, 1f, 10u)]
    public void InvalidApplicationReportsCannotBecomeConfirmation(float limit, float real, uint applied)
    {
        using var world = World.Started();
        world.Time += .3f;
        var id = world.Last(ManualBelayPhase.Started).Session;
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.Report, Session = id,
            Sequence = 1, AppliedSequence = applied, Limit = limit, RealDistance = real, Suspended = true }).Committed);
    }

    [Fact]
    public void StaleFarAwayOrDifferentScenePlayersCannotBePaired()
    {
        using var world = new World();
        foreach (var location in new[] {
            new PlayerLocation(50, 0, 0, "Wall", PlayerState.InGame),
            new PlayerLocation(1, 0, 0, "Other", PlayerState.InGame),
            new PlayerLocation(float.NaN, 0, 0, "Wall", PlayerState.InGame) })
        {
            world.Locations[2] = location;
            Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Offer, Target = 2 }).Committed);
        }
        world.Locations.Remove(2);
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Offer, Target = 2 }).Committed);
    }

    [Fact]
    public void DisabledExperimentAndMissingStationCannotSendAnOffer()
    {
        using var world = new World();
        world.Nodes[1].Game.BelayImpl.OptIn = false;
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        Assert.Empty(world.Messages);
        world.Nodes[1].Game.BelayImpl.OptIn = true;
        world.Nodes[1].Game.BelayImpl.Anchored = false;
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        Assert.Empty(world.Messages);
    }

    [Fact]
    public void HeartbeatsAreBoundedAndClimberReportsCannotRenewLostBelayerPermission()
    {
        using var world = World.Started();
        var id = world.Last(ManualBelayPhase.Started).Session;
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = id, Sequence = 2 }).Committed);
        world.Time += 2.9f;
        Assert.True(world.Execute(2, new() { Operation = ManualBelayOperation.Report, Session = id, Sequence = 1 }).Committed);
        world.Time += .11f;
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = id, Sequence = 2 }).Committed);
        world.Tick();
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
    }

    [Fact]
    public void ExpiredInvitationAndSecondPairCannotBypassConsent()
    {
        using var world = new World();
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        var first = world.Last(ManualBelayPhase.Offered).Session;
        Assert.False(world.Execute(3, new() { Operation = ManualBelayOperation.Offer, Target = 2 }).Committed);
        world.Time += 20.1f;
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.Accept, Session = first }).Committed);
        world.Tick();
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        Assert.NotEqual(first, world.Last(ManualBelayPhase.Offered).Session);
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.Accept, Session = first }).Committed);
    }

    [Fact]
    public void PacketRoundTripsRetainIntentAndApplicationSeparately()
    {
        var request = new ManualBelayCommand { Operation = ManualBelayOperation.Report, Session = Guid.NewGuid(),
            Target = 2, Sequence = 8, AppliedSequence = 7, Braked = true, Suspended = true, Limit = 1.5f, RealDistance = 1.4f };
        var decoded = new ManualBelayCommand();
        using (var reader = new BinaryReader(new MemoryStream(Bytes(request)))) decoded.Deserialize(reader);
        Assert.Equal(request.Session, decoded.Session);
        Assert.Equal(8u, decoded.Sequence);
        Assert.Equal(7u, decoded.AppliedSequence);
        Assert.Equal(1.5f, decoded.Limit);
        Assert.Equal(1.4f, decoded.RealDistance);
        var message = new ManualBelayMessage { Phase = ManualBelayPhase.Telemetry, Session = request.Session,
            Belayer = 1, Climber = 2, Sequence = 8, AppliedSequence = 7, Braked = true, Suspended = true, Limit = 1.5f, RealDistance = 1.4f };
        var eventCopy = new ManualBelayMessage();
        using (var reader = new BinaryReader(new MemoryStream(Bytes(message)))) eventCopy.Deserialize(reader);
        Assert.Equal(message.Session, eventCopy.Session);
        Assert.Equal(1, eventCopy.Belayer);
        Assert.Equal(2, eventCopy.Climber);
        Assert.Equal(7u, eventCopy.AppliedSequence);
        Assert.True(eventCopy.Braked);
    }

    [Fact]
    public void ClimberCanRequestBelayAndTheInvitedBelayerAcceptsThroughThePanel()
    {
        using var world = new World();
        var climber = world.Nodes[2].Game;
        climber.Keys.Key = GameKey.L; climber.Keys.Pressed = true; world.Tick();
        Assert.True(climber.PanelImpl.IsOpen);
        Assert.True(climber.PanelImpl.View.CanRequest);
        Assert.Contains(climber.PanelImpl.View.Partners, row => row.Id == 1);
        climber.PanelImpl.Act(BelayPanelAction.Request, 1);
        var belayer = world.Nodes[1].Game;
        Assert.True(belayer.PanelImpl.IsOpen);
        Assert.Equal(BelayPanelPhase.Invitation, belayer.PanelImpl.View.Phase);
        Assert.Equal(0, climber.BelayImpl.BeginCalls);
        belayer.PanelImpl.Act(BelayPanelAction.Accept);
        world.Tick();
        Assert.True(climber.BelayImpl.Active);
        Assert.False(climber.PanelImpl.IsOpen);
        Assert.False(belayer.PanelImpl.IsOpen);
        Assert.Equal(0, belayer.BelayImpl.BeginCalls);
    }

    [Fact]
    public void PanelOfferDeclineAndCloseDoNotRequireChatCommandsOrImplicitConsent()
    {
        using var world = new World();
        world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.Offer, 2);
        Assert.Equal(BelayPanelPhase.Invitation, world.Nodes[2].Game.PanelImpl.View.Phase);
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.Close);
        Assert.False(world.Nodes[2].Game.PanelImpl.IsOpen);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.BeginCalls);
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.Decline);
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.BeginCalls);
    }

    [Fact]
    public void PanelEligibilityUsesNativeCapabilitiesAndEmptyListsStayEmpty()
    {
        using var world = new World();
        var game = world.Nodes[2].Game;
        game.BelayImpl.Anchored = game.BelayImpl.ProtectionReady = false;
        game.Keys.Key = GameKey.L; game.Keys.Pressed = true; world.Tick();
        Assert.False(game.PanelImpl.View.CanOffer);
        Assert.False(game.PanelImpl.View.CanRequest);
        game.PanelImpl.Act(BelayPanelAction.Request, 1);
        Assert.Empty(world.Messages);
        world.Locations.Remove(1); world.Locations.Remove(3);
        world.Time += .3f; world.Tick();
        Assert.Empty(game.PanelImpl.View.Partners);
    }

    [Fact]
    public void InvitedBelayerCanReachTheirStationBeforeAcceptingAClimberRequest()
    {
        using var world = new World();
        world.Nodes[2].Game.ChatImpl.Run("request 1");
        var id = world.Last(ManualBelayPhase.Offered).Session;
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.Accept, Session = id }).Committed);
        world.Locations[1] = new PlayerLocation(.8f, 0, 0, "Wall", PlayerState.InGame);
        world.Time += .2f; world.Tick();
        world.Nodes[1].Game.ChatImpl.Run("accept");
        world.Tick();
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
    }

    [Fact]
    public void NewTopologyRevisionAllowsFreshApplicationButRejectsOldRouteTelemetry()
    {
        using var world = World.Started();
        var id = world.Last(ManualBelayPhase.Started).Session;
        world.Time += .3f;
        Assert.True(world.Execute(2, new() { Operation = ManualBelayOperation.Report, Session = id,
            Sequence = 1, AppliedSequence = 1, TopologyRevision = 1, ProtectionCount = 2, Suspended = true, Limit = 2, RealDistance = 2 }).Committed);
        world.Time += .3f;
        Assert.True(world.Execute(2, new() { Operation = ManualBelayOperation.Report, Session = id,
            Sequence = 1, AppliedSequence = 0, TopologyRevision = 2, ProtectionCount = 3, Rebinding = true }).Committed);
        world.Time += .3f;
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.Report, Session = id,
            Sequence = 1, AppliedSequence = 1, TopologyRevision = 1, ProtectionCount = 2, Suspended = true, Limit = 2, RealDistance = 2 }).Committed);
        Assert.Equal(2u, world.Last(ManualBelayPhase.Telemetry).TopologyRevision);
    }

    [Fact]
    public void CancellingBeforeTheInvitationArrivesCannotStartProtectionLater()
    {
        using var world = new World();
        world.DeferCommands = true;
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.Request, 1);
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.Cancel);
        world.FlushCommands();
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.BeginCalls);
        Assert.False(world.Nodes[1].Game.PanelImpl.IsOpen);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void MalformedPacketSizeCannotAcquireControl(int delta)
    {
        using var world = new World();
        var packet = Bytes(new ManualBelayCommand { Operation = ManualBelayOperation.Offer, Target = 2 });
        Array.Resize(ref packet, packet.Length + delta);
        Assert.False(world.Nodes[1].Runtime.ExecuteHostCommand(1, new ClientExtensionCommand {
            ExtensionId = FeatureHost.CoreExtensionId, CommandId = "manual-belay.request", Payload = packet }).Committed);
        Assert.Empty(world.Messages);
    }

    private static byte[] Bytes(IPacket value)
    {
        using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer);
        value.Serialize(writer); return buffer.ToArray();
    }
    private static ManualBelayMessage Decode(ServerExtensionEvent value)
    {
        var message = new ManualBelayMessage();
        using var reader = new BinaryReader(new MemoryStream(value.Payload)); message.Deserialize(reader); return message;
    }

    private sealed class World : IDisposable
    {
        internal MultiplayerMode Mode = MultiplayerMode.BelayTeam;
        internal float Time = 1;
        internal readonly Dictionary<int, Node> Nodes = new();
        internal readonly Dictionary<int, PlayerLocation> Locations = new();
        internal readonly List<ServerExtensionEvent> Events = new();
        internal IEnumerable<ManualBelayMessage> Messages => Events.Select(Decode);
        internal readonly List<string> Failures = new();
        internal bool DeferCommands;
        internal readonly Queue<(int Sender, ClientExtensionCommand Packet)> Pending = new();
        internal World(int participants = 3)
        {
            for (var id = 1; id <= participants; id++) { Locations[id] = new PlayerLocation(id - 1, 0, 0, "Wall", PlayerState.InGame); Nodes[id] = new Node(this, id); }
        }
        internal static World Started()
        { var world = new World(); world.Nodes[1].Game.ChatImpl.Run("offer 2"); world.Nodes[2].Game.ChatImpl.Run("accept"); world.Tick(); return world; }
        internal ManualBelayMessage Last(ManualBelayPhase phase) => Messages.Last(m => m.Phase == phase);
        internal CommandResult Execute(int sender, ManualBelayCommand command)
            => Nodes[1].Runtime.ExecuteHostCommand(sender, new ClientExtensionCommand {
                RequestId = 999, ExtensionId = FeatureHost.CoreExtensionId, CommandId = "manual-belay.request", Payload = Bytes(command) });
        internal void Tick()
        {
            foreach (var node in Nodes.Values) { node.Runtime.Tick(); node.Host.Tick(FeaturePhase.Always); }
            foreach (var node in Nodes.Values) node.Game.Keys.Pressed = false;
        }
        internal void Deliver(int sender, ClientExtensionCommand command)
        {
            var result = Nodes[1].Runtime.ExecuteHostCommand(sender, command);
            Nodes[sender].Runtime.CompleteCommand(new ServerExtensionCommandResult { RequestId = command.RequestId, Status = (ExtensionCommandStatus)result.Status, Reason = result.Reason });
        }
        internal void FlushCommands()
        {
            while (Pending.TryDequeue(out var item)) Deliver(item.Sender, item.Packet);
        }
        public void Dispose() { foreach (var node in Nodes.Values) node.Host.Dispose(); }
    }

    private sealed class Node
    {
        internal readonly ExtensionRuntime Runtime = new();
        internal readonly Game Game;
        internal readonly FeatureHost Host;
        internal Node(World world, int id)
        {
            Game = new Game(world); Host = new FeatureHost(Runtime, Game, rules: () => MultiplayerModes.RulesFor(world.Mode));
            Host.RegisterAll(new[] { new ManualBelayFeature() }, new Version(1, 0));
            Runtime.Attach(new Bridge(world, this, id));
        }
    }
    private sealed class Bridge : IExtensionNetworkBridge
    {
        private readonly World _world; private readonly Node _node;
        internal Bridge(World world, Node node, int id) { _world = world; _node = node; LocalPlayerId = id; }
        public bool IsConnected => true;
        public bool IsHost => LocalPlayerId == 1;
        public int LocalPlayerId { get; }
        public MultiplayerPlayer LocalPlayer => new(LocalPlayerId, "P" + LocalPlayerId, true, IsHost);
        public IReadOnlyList<MultiplayerPlayer> Players => _world.Nodes.Keys.Select(id => new MultiplayerPlayer(id, "P" + id, id == LocalPlayerId, id == 1)).ToArray();
        public bool IsExtensionEnabled(string extensionId, int playerId) => _world.Nodes.ContainsKey(playerId);
        public void SendCommand(ClientExtensionCommand command)
        {
            if (_world.DeferCommands) _world.Pending.Enqueue((LocalPlayerId, command));
            else _world.Deliver(LocalPlayerId, command);
        }
        public void SendCommandResult(int target, ServerExtensionCommandResult result) { }
        public void BroadcastEvent(ServerExtensionEvent message, string extensionId)
        {
            _world.Events.Add(message);
            foreach (var (id, node) in _world.Nodes) if (id != 1) node.Runtime.ApplyEvent(message);
        }
        public void BroadcastState(ServerExtensionState state, string extensionId) { }
        public void ReportExtensionFailure(string extensionId, string message) => _world.Failures.Add(message);
    }

    private sealed class Game : IGameApi, IGameTimeApi, IPlayersApi
    {
        private readonly World _world;
        internal readonly Belay BelayImpl = new(); internal readonly Chat ChatImpl = new(); internal readonly Keys Keys = new();
        internal readonly Panel PanelImpl = new();
        internal Game(World world) => _world = world;
        public IMainMenuApi MainMenu => UnavailableGameApi.Instance.MainMenu;
        public IGameStateApi State => new State();
        public IGameTimeApi Time => this;
        public IGameInputApi Input => Keys;
        public IGameHudApi Hud => UnavailableGameApi.Instance.Hud;
        public IChatApi Chat => ChatImpl;
        public IInventoryApi Inventory => UnavailableGameApi.Instance.Inventory;
        public IClockApi Clock => UnavailableGameApi.Instance.Clock;
        public IPlayersApi Players => this;
        public IWeatherApi Weather => UnavailableGameApi.Instance.Weather;
        public IWorldApi World => UnavailableGameApi.Instance.World;
        public IManualBelayApi Belay => BelayImpl;
        public IBelayPanelApi BelayPanel => PanelImpl;
        public float UnscaledTime => _world.Time;
        public float UnscaledDeltaTime => .02f;
        public bool IsPaused => false;
        public IReadOnlyList<int> RemotePlayersInGame => new[] { 1, 2, 3 };
        public IReadOnlyList<int> RemoteSleepParticipants => RemotePlayersInGame;
        public IReadOnlyList<int> RemotePlayersInGameOrBivouac => RemotePlayersInGame;
        public bool IsRemotePlayerInBivouac(int id) => false;
        public bool TryGetLocation(int id, out PlayerLocation location) => _world.Locations.TryGetValue(id, out location);
        public bool TryCaptureHandPose(out byte[] packed) { packed = null; return false; }
        public bool TryCaptureAppearance(out int packed) { packed = 0; return false; }
        public bool TryCaptureCosmetics(out byte packed) { packed = 0; return false; }
        public void SetRemoteHandPose(int id, byte[] packed) { }
        public void SetRemoteAppearance(int id, int packed) { }
        public void SetRemoteCosmetics(int id, byte packed) { }
        public void ResetHandPoseCaches() { }
        public void ResetAppearanceCaches() { }
        public bool IsLocalPlayerFalling => false;
        public bool IsRopedToLocalPlayer(int id) => false;
        public bool ShakeLocalClimberGrip() => false;
    }
    private sealed class Belay : IManualBelayApi
    {
        internal bool OptIn = true, Anchored = true, CanBegin = true, ProtectionReady = true, Active, LastBrake;
        internal int BeginCalls, SetCalls, StopCalls, ReadinessChecks;
        internal uint Sequence;
        private Guid _session;
        public bool Enabled => OptIn;
        public bool CanBeginStation(out string reason) { ReadinessChecks++; reason = "Need station"; return Anchored; }
        public bool CanClimb(out string reason) { ReadinessChecks++; reason = "Need protection"; return ProtectionReady; }
        public bool StationValid => Anchored;
        public bool BeginStation(out string reason) { reason = "Need station"; return Anchored; }
        public void EndStation() { }
        public bool BeginClimber(Guid id, out string reason) { BeginCalls++; reason = "No native rope"; _session = id; return Active = CanBegin; }
        public bool SetBrake(Guid id, uint sequence, bool braked)
        { if (!Active || id != _session || sequence <= Sequence) return false; Sequence = sequence; LastBrake = braked; SetCalls++; return true; }
        public ManualBelayStatus Status => new(Active, false, LastBrake, Sequence, 0, 0, 0);
        public void Stop(Guid id) { if (id == _session) { Active = false; StopCalls++; } }
        public void Tick() { }
        public void ResetScene() => Active = false;
        public void Shutdown() => Active = false;
    }
    private sealed class Chat : IChatApi
    {
        private Action<string> _command;
        public bool IsTyping => false;
        internal void Run(string args) => _command(args);
        public IGameRegistration AddCommand(string name, string usage, string description, Action<string> execute)
        { _command = execute; return UnavailableGameApi.Instance.Chat.AddCommand(name, usage, description, execute); }
        public IGameRegistration Configure(Action<string> send, Func<bool> host, Func<bool> canType) => UnavailableGameApi.Instance.Chat.Configure(send, host, canType);
        public void AddRemoteLine(string name, string text) { }
        public void AddSystemLine(string text) { }
        public void Tick() { }
        public void Draw() { }
        public void ForceClose() { }
    }
    private sealed class Keys : IGameInputApi
    {
        internal bool Captured, Pressed;
        internal GameKey? Key;
        public bool IsKeyboardCaptured => Captured;
        public bool IsPauseMenuActive => false;
        public bool WasPressed(GameInputAction action) => false;
        public bool WasKeyPressed(GameKey key) => Pressed && key == (Key ?? GameKey.Space);
    }
    private sealed class Panel : IBelayPanelApi
    {
        private Action<BelayPanelAction, int> _action;
        internal BelayPanelView View = new();
        public bool IsOpen { get; private set; }
        public IGameRegistration Register(Action<BelayPanelAction, int> action) { _action = action; return new Registration(this); }
        public void SetView(BelayPanelView view) => View = view;
        public void Show() => IsOpen = true;
        public void Close() => IsOpen = false;
        public void Draw() { }
        internal void Act(BelayPanelAction action, int target = -1) => _action(action, target);
        private sealed class Registration : IGameRegistration
        {
            private Panel _owner;
            internal Registration(Panel owner) => _owner = owner;
            public string Id => "test.panel";
            public bool IsActive => _owner != null;
            public void Dispose() { _owner?.Close(); _owner = null; }
        }
    }
    private sealed class State : IGameStateApi
    {
        public PlayerState LocalPlayerState => PlayerState.InGame;
        public bool IsLocalPlayerInGame => true;
        public bool IsLocalPlayerInBivouac => false;
        public bool HasReachedSummit => false;
        public bool TryGetCampPosition(out WorldPosition position) { position = default; return false; }
    }
}
