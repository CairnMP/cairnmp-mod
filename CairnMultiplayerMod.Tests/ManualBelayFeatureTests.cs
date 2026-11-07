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
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed partial class ManualBelayFeatureTests
{
    [Fact]
    public void NearbyInteractionBodiesCanConnectEvenWhenTheActorBasesAreFarApart()
    {
        using var world = new World(participants: 2);
        world.Locations[1] = new PlayerLocation(50, 0, 0, "Wall", PlayerState.InGame);
        world.Locations[2] = new PlayerLocation(100, 0, 0, "Wall", PlayerState.InGame);
        var bodies = new Dictionary<int, PlayerLocation>
        {
            [1] = new(0, 0, 0, "Wall", PlayerState.InGame),
            [2] = new(2, 0, 0, "Wall", PlayerState.InGame),
        };
        world.Nodes[1].Game.InteractionLocations = bodies;
        world.Nodes[2].Game.InteractionLocations = bodies;
        world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.Toggle); world.Tick();
        Assert.Equal(2, Assert.Single(world.Nodes[1].Game.PanelImpl.View.Partners).Distance);
        world.Nodes[1].Game.ChatImpl.Run("offer 2"); world.Nodes[2].Game.ChatImpl.Run("accept"); world.Tick();
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void ASceneMismatchExplainsTheMissingPartnerWithoutBypassingSceneValidation()
    {
        using var world = new World(participants: 2);
        world.Locations[2] = new PlayerLocation(1, 0, 0, "OtherWall", PlayerState.InGame);
        var game = world.Nodes[1].Game;
        game.PanelImpl.Act(BelayPanelAction.Toggle); world.Tick();
        Assert.Empty(game.PanelImpl.View.Partners);
        Assert.Contains("Different gameplay scenes", game.PanelImpl.View.Description);
        Assert.False(game.PanelImpl.View.CanOffer);
        game.ChatImpl.Run("offer 2");
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
    }

    [Theory]
    [InlineData(MultiplayerMode.RopeTeam)]
    [InlineData(MultiplayerMode.FreeSolo)]
    [InlineData(MultiplayerMode.Race)]
    public void OtherModesCannotOpenThePanelOrStartPartnerBelay(MultiplayerMode mode)
    {
        using var world = new World { Mode = mode };
        var game = world.Nodes[1].Game;
        game.PanelImpl.Act(BelayPanelAction.Toggle);
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
        game.PanelImpl.Act(BelayPanelAction.Toggle);
        world.Tick();
        Assert.True(game.PanelImpl.IsOpen);
        Assert.Empty(game.PanelImpl.View.Partners);
        Assert.False(game.PanelImpl.View.CanOffer);
        Assert.False(game.PanelImpl.View.CanRequest);
        Assert.Equal(0, game.BelayImpl.ReadinessChecks);
    }

    [Fact]
    public void ExistingAnchorCanBecomeAndStopBeingAStationWithoutAPartner()
    {
        using var world = new World(participants: 1);
        var game = world.Nodes[1].Game;
        game.PanelImpl.Act(BelayPanelAction.Toggle);
        game.PanelImpl.Act(BelayPanelAction.DefineStation);
        Assert.True(game.BelayImpl.HasStation);
        Assert.True(game.PanelImpl.View.StationDefined);
        Assert.Empty(world.Messages);
        Assert.Equal(0, game.BelayImpl.BeginCalls);
        game.PanelImpl.Act(BelayPanelAction.RemoveStation);
        Assert.False(game.BelayImpl.HasStation);
        Assert.False(game.PanelImpl.View.StationDefined);
        Assert.True(game.BelayImpl.Anchored);
        Assert.True(game.PanelImpl.IsOpen);
        game.PanelImpl.Act(BelayPanelAction.DefineStation);
        Assert.True(game.BelayImpl.HasStation);
    }

    [Fact]
    public void UnanchoredPlayerCannotDefineAStation()
    {
        using var world = new World(participants: 1);
        var game = world.Nodes[1].Game;
        game.BelayImpl.Anchored = false;
        game.PanelImpl.Act(BelayPanelAction.DefineStation);
        Assert.False(game.BelayImpl.HasStation);
        Assert.False(game.PanelImpl.View.StationDefined);
        Assert.Empty(world.Messages);
    }

    [Theory]
    [InlineData(MultiplayerMode.RopeTeam)]
    [InlineData(MultiplayerMode.FreeSolo)]
    [InlineData(MultiplayerMode.Race)]
    public void OtherModesCannotDefineAStation(MultiplayerMode mode)
    {
        using var world = new World(participants: 1) { Mode = mode };
        var game = world.Nodes[1].Game;
        game.PanelImpl.Act(BelayPanelAction.DefineStation);
        Assert.False(game.BelayImpl.HasStation);
    }

    [Fact]
    public void ReleasingPersonalAnchorRemovesAnIdleStation()
    {
        using var world = new World(participants: 1);
        var game = world.Nodes[1].Game;
        game.PanelImpl.Act(BelayPanelAction.DefineStation);
        game.BelayImpl.Anchored = false;
        world.Tick();
        Assert.False(game.BelayImpl.HasStation);
    }

    [Fact]
    public void RemovingAnActiveStationIsIgnoredUntilBelayIsExplicitlyEnded()
    {
        using var world = World.Started();
        var belayer = world.Nodes[1].Game;
        var session = world.Last(ManualBelayPhase.Started).Session;
        belayer.PanelImpl.Act(BelayPanelAction.RemoveStation);
        world.Tick();
        Assert.True(belayer.BelayImpl.HasStation);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Equal(session, belayer.BelayImpl.ProtectedSession);
        belayer.PanelImpl.Act(BelayPanelAction.Cancel);
        world.Tick();
        Assert.False(belayer.BelayImpl.HasStation);
        Assert.True(belayer.BelayImpl.Anchored);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = session, Sequence = 999 }).Committed);
        Assert.Equal(Guid.Empty, belayer.BelayImpl.ProtectedSession);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void OnlyTheBelayersPersonalAnchorIsProtectedUntilExplicitEnd(int belayer, int climber)
    {
        using var world = new World();
        world.Nodes[belayer].Game.ChatImpl.Run("offer " + climber);
        world.Nodes[climber].Game.ChatImpl.Run("accept"); world.Tick();
        Assert.NotEqual(Guid.Empty, world.Nodes[belayer].Game.BelayImpl.ProtectedSession);
        Assert.Equal(Guid.Empty, world.Nodes[climber].Game.BelayImpl.ProtectedSession);
        world.Nodes[climber].Game.PanelImpl.Act(BelayPanelAction.Cancel); world.Tick();
        Assert.Equal(Guid.Empty, world.Nodes[belayer].Game.BelayImpl.ProtectedSession);
        Assert.False(world.Nodes[climber].Game.BelayImpl.Active);
    }

    [Fact]
    public void AnchoredBodyMotionWithinTheStationAreaDoesNotEndThePair()
    {
        using var world = World.Started();
        world.Locations[1] = new PlayerLocation(1, 0, 0, "Wall", PlayerState.InGame);
        Advance(world, .3f);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        world.Locations[1] = new PlayerLocation(3.1f, 0, 0, "Wall", PlayerState.InGame);
        Advance(world, .3f);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
    }

    [Fact]
    public void LosingStationBeforeInvitationAcceptanceCancelsTheOffer()
    {
        using var world = new World();
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        var session = world.Last(ManualBelayPhase.Offered).Session;
        world.Nodes[1].Game.BelayImpl.Anchored = false;
        world.Tick();
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.Accept, Session = session }).Committed);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.BeginCalls);
    }

    [Fact]
    public void RemovingStationCancelsAPendingOfferWithoutDetachingThePersonalAnchor()
    {
        using var world = new World();
        var game = world.Nodes[1].Game;
        game.ChatImpl.Run("offer 2");
        var session = world.Last(ManualBelayPhase.Offered).Session;
        game.PanelImpl.Act(BelayPanelAction.RemoveStation);
        Assert.False(game.BelayImpl.HasStation);
        Assert.True(game.BelayImpl.Anchored);
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.Accept, Session = session }).Committed);
    }

    [Fact]
    public void LeavingBelayTeamRemovesAnIdleStation()
    {
        using var world = new World(participants: 1);
        var game = world.Nodes[1].Game;
        game.PanelImpl.Act(BelayPanelAction.DefineStation);
        world.Mode = MultiplayerMode.RopeTeam;
        world.Tick();
        Assert.False(game.BelayImpl.HasStation);
        Assert.True(game.BelayImpl.Anchored);
    }

    [Fact]
    public void PanelHotkeyClosesItsOwnCaptureButDoesNotOpenOverAnotherCapture()
    {
        using var world = new World(participants: 1);
        var game = world.Nodes[1].Game;
        game.PanelImpl.Act(BelayPanelAction.Toggle);
        Assert.True(game.PanelImpl.IsOpen);
        game.Keys.Captured = true;
        game.PanelImpl.Act(BelayPanelAction.Toggle);
        Assert.False(game.PanelImpl.IsOpen);
        game.PanelImpl.Act(BelayPanelAction.Toggle);
        Assert.False(game.PanelImpl.IsOpen);
        game.Keys.Captured = false;
        game.PanelImpl.Act(BelayPanelAction.Toggle);
        Assert.True(game.PanelImpl.IsOpen);
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
            world.Locations[1] = new PlayerLocation(3.1f, 0, 0, "Wall", PlayerState.InGame);
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
        Assert.True(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = id, Sequence = 2 }).Committed);
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = id, Sequence = 3 }).Committed);
        world.Time += 2.9f;
        Assert.True(world.Execute(2, new() { Operation = ManualBelayOperation.Report, Session = id, Sequence = 2,
            Released = 8, Slack = 3, Reserve = 32 }).Committed);
        world.Time += .11f;
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = id, Sequence = 3 }).Committed);
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
            Target = 2, Sequence = 8, AppliedSequence = 7, Braked = true, Suspended = true, Limit = 1.5f, RealDistance = 1.4f,
            Station = new BelaySharedStation(Guid.NewGuid(), new WorldPosition(1, 2, 3), 1, 7),
            Motion = -1, Released = 8, Slack = 3, Reserve = 32, LengthApplied = true, RopeCapacity = 40 };
        var decoded = new ManualBelayCommand();
        using (var reader = new BinaryReader(new MemoryStream(Bytes(request)))) decoded.Deserialize(reader);
        Assert.Equal(request.Session, decoded.Session);
        Assert.Equal(8u, decoded.Sequence);
        Assert.Equal(7u, decoded.AppliedSequence);
        Assert.Equal(1.5f, decoded.Limit);
        Assert.Equal(1.4f, decoded.RealDistance);
        Assert.Equal(118, Bytes(request).Length);
        Assert.Equal(40, decoded.RopeCapacity);
        Assert.Equal(request.Station, decoded.Station);
        Assert.Equal(-1, decoded.Motion);
        Assert.Equal(8, decoded.Released);
        Assert.Equal(3, decoded.Slack);
        Assert.Equal(32, decoded.Reserve);
        Assert.True(decoded.LengthApplied);
        var message = new ManualBelayMessage { Phase = ManualBelayPhase.Telemetry, Session = request.Session,
            Belayer = 1, Climber = 2, Sequence = 8, AppliedSequence = 7, Braked = true, Suspended = true, Limit = 1.5f, RealDistance = 1.4f,
            Station = request.Station, Motion = -1, Released = 8, Slack = 3, Reserve = 32, LengthApplied = true, RopeCapacity = 40 };
        var eventCopy = new ManualBelayMessage();
        using (var reader = new BinaryReader(new MemoryStream(Bytes(message)))) eventCopy.Deserialize(reader);
        Assert.Equal(message.Session, eventCopy.Session);
        Assert.Equal(1, eventCopy.Belayer);
        Assert.Equal(2, eventCopy.Climber);
        Assert.Equal(7u, eventCopy.AppliedSequence);
        Assert.True(eventCopy.Braked);
        Assert.Equal(126, Bytes(message).Length);
        Assert.Equal(40, eventCopy.RopeCapacity);
        Assert.Equal(message.Station, eventCopy.Station);
        Assert.Equal(-1, eventCopy.Motion);
        Assert.Equal(8, eventCopy.Released);
        Assert.Equal(3, eventCopy.Slack);
        Assert.Equal(32, eventCopy.Reserve);
        Assert.True(eventCopy.LengthApplied);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void BothInvitationDirectionsBindTheSameActualBelayerStation(bool requestBelay, int belayerId)
    {
        using var world = new World();
        var climberId = belayerId == 1 ? 2 : 1;
        var initiator = requestBelay ? climberId : belayerId;
        var recipient = requestBelay ? belayerId : climberId;
        world.Nodes[initiator].Game.ChatImpl.Run((requestBelay ? "request " : "offer ") + recipient);
        world.Nodes[recipient].Game.ChatImpl.Run("accept");
        world.Tick();
        var belayer = world.Nodes[belayerId].Game.BelayImpl;
        var climber = world.Nodes[climberId].Game.BelayImpl;
        var station = world.Last(ManualBelayPhase.Started).Station;
        Assert.True(station.IsValid);
        Assert.Equal(belayer.StationReference, station);
        Assert.Equal(station, climber.BoundStation);
        Assert.True(climber.Active);
        Assert.Equal(1, climber.StationBeginCalls);
        Assert.Equal(0, belayer.StationBeginCalls);
        Assert.All(world.Messages.Where(m => m.Phase is ManualBelayPhase.Intent or ManualBelayPhase.Telemetry),
            m => Assert.Equal(station, m.Station));
        Assert.Empty(world.Failures);
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.PositiveInfinity, 0f)]
    [InlineData(0f, 0f, float.NegativeInfinity)]
    [InlineData(3.01f, 0f, 0f)]
    public void AHostRejectsInvalidOrRemoteStationPositions(float x, float y, float z)
    {
        using var world = new World();
        var result = world.Execute(1, new() { Operation = ManualBelayOperation.Offer, Target = 2,
            Station = new BelaySharedStation(Guid.NewGuid(), new WorldPosition(x, y, z), 1, 7) });
        Assert.False(result.Committed);
        Assert.Empty(world.Messages);
    }

    [Fact]
    public void MissingStationCannotOfferAndAClimberCannotChooseTheBelayersStation()
    {
        using var world = new World();
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Offer, Target = 2 }).Committed);
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.Request, Target = 1,
            Station = new BelaySharedStation(Guid.NewGuid(), default, 1, 7) }).Committed);
        Assert.Empty(world.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptCannotReplaceTheOfferedStationOrItsPosition(bool movePosition)
    {
        using var world = new World();
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        var offer = world.Last(ManualBelayPhase.Offered);
        var replaced = movePosition ? offer.Station with { Position = new WorldPosition(.1f, 0, 0) }
            : offer.Station with { Id = Guid.NewGuid() };
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.Accept,
            Session = offer.Session, Station = replaced }).Committed);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.BeginCalls);
        world.Nodes[2].Game.ChatImpl.Run("accept");
        Assert.Equal(offer.Station, world.Last(ManualBelayPhase.Started).Station);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacingTheNativeStationCancelsConsentInsteadOfRebindingSilently(bool started)
    {
        using var world = new World();
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        var session = world.Last(ManualBelayPhase.Offered).Session;
        if (started) world.Nodes[2].Game.ChatImpl.Run("accept");
        world.Nodes[1].Game.BelayImpl.StationId = Guid.NewGuid();
        world.Tick();
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = session, Sequence = 999 }).Committed);
    }

    [Fact]
    public void AnUnresolvableSharedPitonDisablesAcceptanceWithoutTakingNativeOwnership()
    {
        using var world = new World();
        var climber = world.Nodes[2].Game;
        climber.BelayImpl.SharedStationReady = false;
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        Assert.False(climber.PanelImpl.View.CanAccept);
        climber.ChatImpl.Run("accept");
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Started);
        Assert.Equal(0, climber.BelayImpl.BeginCalls);
    }

    [Fact]
    public void ARequestedStationThatDisappearsBeforeNativeAcquisitionEndsThePair()
    {
        using var world = new World();
        world.Nodes[2].Game.ChatImpl.Run("request 1");
        world.Nodes[2].Game.BelayImpl.SharedStationReady = false;
        world.Nodes[1].Game.ChatImpl.Run("accept");
        world.Tick();
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.BeginCalls);
    }

    [Fact]
    public void RemovingAndRedefiningAStationUsesANewLifecycleIdentity()
    {
        using var world = new World(participants: 1);
        var game = world.Nodes[1].Game;
        game.PanelImpl.Act(BelayPanelAction.DefineStation);
        var first = game.BelayImpl.StationReference;
        game.PanelImpl.Act(BelayPanelAction.DefineStation);
        Assert.Equal(first, game.BelayImpl.StationReference);
        game.PanelImpl.Act(BelayPanelAction.RemoveStation);
        game.PanelImpl.Act(BelayPanelAction.DefineStation);
        Assert.NotEqual(first.Id, game.BelayImpl.StationReference.Id);
    }

    [Theory]
    [InlineData(-28)]
    [InlineData(-4)]
    [InlineData(-14)]
    [InlineData(-1)]
    [InlineData(1)]
    public void MissingStationTailsAndTrailingMessageDataAreRejected(int delta)
    {
        var command = Bytes(new ManualBelayCommand());
        Array.Resize(ref command, command.Length + delta);
        using var commandReader = new BinaryReader(new MemoryStream(command));
        Assert.Throws<InvalidDataException>(() => new ManualBelayCommand().Deserialize(commandReader));
        var message = Bytes(new ManualBelayMessage());
        Array.Resize(ref message, message.Length + delta);
        using var messageReader = new BinaryReader(new MemoryStream(message));
        Assert.Throws<InvalidDataException>(() => new ManualBelayMessage().Deserialize(messageReader));
    }

    [Fact]
    public void ClimberCanRequestBelayAndTheInvitedBelayerAcceptsThroughThePanel()
    {
        using var world = new World();
        var climber = world.Nodes[2].Game;
        climber.PanelImpl.Act(BelayPanelAction.Toggle); world.Tick();
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
        game.PanelImpl.Act(BelayPanelAction.Toggle); world.Tick();
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
            Sequence = 1, AppliedSequence = 1, TopologyRevision = 1, ProtectionCount = 2, Suspended = true, Limit = 2, RealDistance = 2,
            Released = 8, Slack = 3, Reserve = 32 }).Committed);
        world.Time += .3f;
        Assert.True(world.Execute(2, new() { Operation = ManualBelayOperation.Report, Session = id,
            Sequence = 1, AppliedSequence = 0, TopologyRevision = 2, ProtectionCount = 3, Rebinding = true,
            Released = 8, Slack = 3, Reserve = 32 }).Committed);
        world.Time += .3f;
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.Report, Session = id,
            Sequence = 1, AppliedSequence = 1, TopologyRevision = 1, ProtectionCount = 2, Suspended = true, Limit = 2, RealDistance = 2,
            Released = 8, Slack = 3, Reserve = 32 }).Committed);
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
        internal IEnumerable<ManualBelayMessage> Messages => Events.Where(e => e.EventId == "manual-belay.changed").Select(Decode);
        internal readonly List<string> Failures = new();
        internal bool DeferCommands;
        internal int DroppedEventRecipient = -1;
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
            foreach (var node in Nodes.Values) { node.Runtime.Tick(); node.Host.Tick(FeaturePhase.Always); node.Game.BelayImpl.Tick(); }
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
            Game = new Game(world, id); Host = new FeatureHost(Runtime, Game, rules: () => MultiplayerModes.RulesFor(world.Mode));
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
            foreach (var (id, node) in _world.Nodes)
                if (id != 1 && id != _world.DroppedEventRecipient) node.Runtime.ApplyEvent(message);
        }
        public void BroadcastState(ServerExtensionState state, string extensionId) { }
        public void ReportExtensionFailure(string extensionId, string message) => _world.Failures.Add(message);
    }

    private sealed class Game : IGameApi, IGameTimeApi, IPlayersApi
    {
        private readonly World _world;
        internal readonly Belay BelayImpl = new(); internal readonly Chat ChatImpl = new(); internal readonly Keys Keys = new();
        internal readonly Panel PanelImpl = new();
        internal bool Cutscene;
        internal IReadOnlyDictionary<int, PlayerLocation> InteractionLocations;
        internal Game(World world, int id)
        { _world = world; BelayImpl.StationPosition = new WorldPosition(id - 1, 0, 0); BelayImpl.StationOwner = id; BelayImpl.Clock = () => world.Time;
            BelayImpl.HostRegistryAvailable = id == 1; }
        public IMainMenuApi MainMenu => UnavailableGameApi.Instance.MainMenu;
        public IGameStateApi State => new State(() => Cutscene);
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
        public bool TryGetInteractionLocation(int id, out PlayerLocation location, out string reason)
        {
            var found = (InteractionLocations ?? _world.Locations).TryGetValue(id, out location);
            reason = found ? "" : "The synchronized player position is not ready.";
            return found;
        }
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
        public bool SharedDamageEnabled { get; set; }
        internal PartnerFallObservation? PartnerFall;
        internal readonly List<PartnerDamageRequest> DamageRequests = new();
        internal int NativeDamageCalls;
        internal float Hp = 100, MaxHp = 100, DamageScale = 1;
        internal bool DamageCompletes = true, DamageThrows, InvalidDamageReadback;
        internal Action<PartnerDamageRequest> DuringDamage;
        public PartnerFallObservation? PollPartnerFall(Guid session)
            => PartnerFall?.Session == session ? PartnerFall : null;
        public PartnerDamageResult ApplyPartnerDamage(PartnerDamageRequest request)
        {
            DamageRequests.Add(request);
            if (PartnerFall?.Session == request.Session && PartnerFall?.Fall == request.Fall) PartnerFall = null;
            DuringDamage?.Invoke(request);
            if (DamageThrows) throw new InvalidOperationException("Native test failure.");
            var before = Hp;
            if (DamageCompletes && request.Fraction > 0)
            { NativeDamageCalls++; Hp = Math.Max(0, Hp - request.Fraction * MaxHp * DamageScale); }
            return new(request.Fraction > 0, DamageCompletes, request.Fraction,
                InvalidDamageReadback ? float.NaN : (before - Hp) / MaxHp, before,
                InvalidDamageReadback ? float.NaN : Hp, "Native test result.");
        }
        internal WorldPosition[] Visual;
        internal int VisualCalls;
        public void ShowRopeVisual(Guid session, int climber, uint revision, WorldPosition[] points)
        { Visual = points; VisualCalls++; }
        public void ClearRopeVisual() => Visual = null;
        internal bool OptIn = true, Anchored = true, CanBegin = true, ProtectionReady = true, Active, LastBrake, StationRecorded;
        public float RopeCapacity { get; set; } = 40;
        internal int BeginCalls, SetCalls, StopCalls, ReadinessChecks;
        internal uint Sequence;
        internal bool SharedStationReady = true;
        internal int StationBeginCalls;
        internal Guid StationId;
        internal Guid ProtectedSession;
        internal int StationOwner;
        internal bool KnownStation = true;
        internal bool HostRegistryAvailable;
        internal int KnownStationChecks;
        internal WorldPosition StationPosition;
        internal BelaySharedStation BoundStation;
        internal bool RolePreparationReady = true, RolePreparationFailed, RoleCommitFailed;
        internal int RolePrepareCalls, RoleCommitCalls;
        private BelaySharedStation _roleCandidate;
        private Guid _roleSession;
        public bool CanPrepareRoleStation(Guid session, out string reason)
        { reason = "Remain anchored"; return Active && Anchored && _session == session; }
        public bool PrepareRoleStation(Guid session, out BelaySharedStation station, out string reason)
        {
            station = default;
            if (!CanPrepareRoleStation(session, out reason)) return false;
            _roleSession = session;
            station = _roleCandidate = new(Guid.NewGuid(), StationPosition, StationOwner, 7);
            return true;
        }
        public bool RoleStationValid(Guid session, BelaySharedStation station)
            => Active && Anchored && _roleSession == session && station == _roleCandidate;
        public bool CanReceiveRoleSwap(Guid session, BelaySharedStation station, out string reason)
        { reason = "Keep original station"; return StationValid && ProtectedSession == session && station.IsValid && SharedStationReady; }
        public BelayRolePreparation PrepareRoleClimber(Guid session, Guid next, BelaySharedStation station, out string reason)
        {
            RolePrepareCalls++;
            if (!CanReceiveRoleSwap(session, station, out reason) || RolePreparationFailed) return BelayRolePreparation.Failed;
            return RolePreparationReady ? BelayRolePreparation.Ready : BelayRolePreparation.Preparing;
        }
        public bool CommitRoleSwap(Guid session, Guid next, BelaySharedStation station, bool becomeBelayer, out string reason)
        {
            RoleCommitCalls++; reason = "Native role commit refused";
            if (RoleCommitFailed || !Anchored) return false;
            if (becomeBelayer)
            {
                if (!RoleStationValid(session, station)) return false;
                Stop(session); StationRecorded = true; StationId = station.Id; StationPosition = station.Position;
                ProtectedSession = next; return true;
            }
            if (!CanReceiveRoleSwap(session, station, out reason) || !RolePreparationReady) return false;
            EndStation(); BoundStation = station;
            if (!BeginClimber(next, out reason)) return false;
            Sequence = 0; LastBrake = true; Rope.SetBrake(true);
            _inputLease = new BelayInputLease(next, Clock(), true);
            return true;
        }
        public void CancelRoleSwap() { _roleCandidate = default; _roleSession = Guid.Empty; }
        internal Func<double> Clock = () => 0;
        internal BelayRopeControl Rope;
        internal bool CanApplyInput = true, SuppressRopeReadback;
        internal bool NeedsAttachment, NativeAttached;
        internal BelayStationAttachment Preparation;
        private BelayInputLease _inputLease;
        private double _lastStep;
        internal BelaySharedStation StationReference => new(StationId, StationPosition, StationOwner, 7);
        private Guid _session;
        public bool Enabled => OptIn;
        public bool IsStationKnown(BelaySharedStation station)
        { KnownStationChecks++; return HostRegistryAvailable && KnownStation && station.IsValid; }
        public bool CanBeginStation(out string reason) { ReadinessChecks++; reason = "Need station"; return Anchored; }
        public bool CanClimb(out string reason) { ReadinessChecks++; reason = "Need protection"; return ProtectionReady && !StationRecorded; }
        public bool CanClimb(BelaySharedStation station, out string reason)
        { var ready = CanClimb(out reason); return ready && SharedStationReady && station.IsValid; }
        public bool HasStation => StationRecorded;
        public bool StationValid => Anchored && StationRecorded;
        public bool BeginStation(out string reason)
        { reason = "Need station"; if (Anchored && !StationRecorded) StationId = Guid.NewGuid(); return StationRecorded = Anchored; }
        public bool TryGetStation(out BelaySharedStation station) { station = StationReference; return StationValid; }
        public bool ProtectStation(Guid session) { ProtectedSession = session; return StationValid; }
        public void EndStation() { StationRecorded = false; ProtectedSession = Guid.Empty; }
        public bool BeginClimber(Guid id, out string reason)
        {
            BeginCalls++; reason = "No native rope"; _session = id;
            _inputLease = new BelayInputLease(id, Clock());
            Rope = new BelayRopeControl(RopeCapacity, 8, 5); _lastStep = Clock();
            return Active = CanBegin;
        }
        public bool BeginClimber(Guid id, BelaySharedStation station, out string reason)
        {
            if (!CanClimb(station, out reason)) return false;
            StationBeginCalls++; BoundStation = station;
            if (NeedsAttachment)
            {
                _session = id; _inputLease = new BelayInputLease(id, Clock());
                Preparation = new BelayStationAttachment(PreparationBefore(), new long[] { 20 }, 10, 100, Clock());
                return Active = CanBegin;
            }
            return BeginClimber(id, out reason);
        }
        public bool SetBrake(Guid id, uint sequence, bool braked)
        { if (!Active || id != _session || sequence <= Sequence) return false; Sequence = sequence; LastBrake = braked; SetCalls++; return true; }
        public bool SetRopeInput(Guid id, uint sequence, bool braked, int motion)
        {
            if (!Active || !CanApplyInput || !_inputLease.TryAccept(id, sequence, braked, motion, Clock())) return false;
            Sequence = sequence; LastBrake = braked; SetCalls++;
            if (Preparation != null) { _inputLease.CancelMotion(); return true; }
            Rope.SetBrake(braked); RefreshMotion(); return true;
        }
        private void RefreshMotion()
        { var motion = _inputLease.MotionAt(Clock()); Rope.TrySetMotion(LastBrake && motion > 0 ? 0 : motion); }
        public ManualBelayStatus Status => Preparation != null
            ? new(Active, false, LastBrake, Sequence, 0, 0, 0, 1, 0, true, new BelayRopeReadback(0, 0, 0, 0, false))
            : new(Active, false, LastBrake, Sequence, 0, 0, 0,
            rope: Rope == null || SuppressRopeReadback ? null : new BelayRopeReadback(Rope.Motion, Rope.Released, Rope.Slack, Rope.Reserve, true));
        public void Stop(Guid id) { if (id == _session) { Active = false; Preparation = null; Rope?.TrySetMotion(0); StopCalls++; } }
        public void Tick()
        {
            if (!Active) return;
            if (_inputLease.IsExpired(Clock())) { Active = false; Preparation = null; return; }
            if (Preparation != null)
            {
                Preparation.Observe(NativeAttached ? PreparationAfter() : PreparationBefore(),
                    NativeAttached ? new long[] { 10, 20 } : new long[] { 20 }, NativeAttached, Clock());
                if (Preparation.Stage == BelayStationAttachmentStage.Failed) { Active = false; Preparation = null; return; }
                if (Preparation.Stage != BelayStationAttachmentStage.Ready) return;
                Preparation = null; BeginCalls++; _inputLease.CancelMotion();
                Rope = new BelayRopeControl(40, 8, 5); Rope.SetBrake(_inputLease.Braked); _lastStep = Clock();
            }
            RefreshMotion();
            Rope.TryStep(5, 5, (float)Math.Max(0, Clock() - _lastStep)); _lastStep = Clock();
        }
        private static BelayTopologySnapshot PreparationBefore() => new(1000, 20, new long[] { 11, 12 }, new long[] { 900, 200, 901 }, 30, 1);
        private static BelayTopologySnapshot PreparationAfter() => new(1000, 20, new long[] { 30, 31, 32 }, new long[] { 900, 100, 200, 901 }, 30, 2);
        public void ResetScene() { Active = false; Preparation = null; Rope?.TrySetMotion(0); }
        public void Shutdown() { Active = false; Preparation = null; Rope?.TrySetMotion(0); }
    }
    private sealed class Chat : IChatApi
    {
        private Action<string> _command;
        internal bool Typing;
        public bool IsTyping => Typing;
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
        internal bool Captured, Pressed, Up, Down, Paused;
        internal bool Focused = true;
        internal GameKey? Key = GameKey.Space;
        public bool IsKeyboardCaptured => Captured;
        public bool IsWindowFocused => Focused;
        public bool IsPauseMenuActive => Paused;
        public bool WasPressed(GameInputAction action) => false;
        public bool WasKeyPressed(GameKey key) => Pressed && key == (Key ?? GameKey.Space);
        public bool IsKeyHeld(GameKey key) => key == GameKey.UpArrow ? Up : key == GameKey.DownArrow && Down;
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
        private readonly Func<bool> _cutscene;
        internal State(Func<bool> cutscene = null) => _cutscene = cutscene ?? (() => false);
        public bool IsLocalPlayerInCutscene => _cutscene();
        public PlayerState LocalPlayerState => PlayerState.InGame;
        public bool IsLocalPlayerInGame => true;
        public bool IsLocalPlayerInBivouac => false;
        public bool HasReachedSummit => false;
        public bool TryGetCampPosition(out WorldPosition position) { position = default; return false; }
    }
}
