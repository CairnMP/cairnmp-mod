using System;
using System.Linq;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.GameApi;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed partial class ManualBelayFeatureTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void AnUpperBelayerCanConnectBeyondTheOldSixAndFortyMetreLimitsWhenTheClimbersRopeReaches(int belayer, bool request)
    {
        using var world = new World(participants: 2);
        var climber = belayer == 1 ? 2 : 1;
        world.Locations[belayer] = new(0, 0, 0, "Wall", PlayerState.InGame);
        world.Locations[climber] = new(0, -50, 0, "Wall", PlayerState.InGame);
        world.Nodes[climber].Game.BelayImpl.RopeCapacity = 60;
        var initiator = request ? climber : belayer;
        var recipient = request ? belayer : climber;
        world.Nodes[initiator].Game.ChatImpl.Run((request ? "request " : "offer ") + recipient);
        world.Nodes[recipient].Game.ChatImpl.Run("accept");
        world.Tick();
        Assert.True(world.Nodes[climber].Game.BelayImpl.Active);
        Assert.Equal(60, world.Last(ManualBelayPhase.Started).RopeCapacity);
        world.Time += .6f; world.Tick();
        Assert.DoesNotContain(world.Messages, message => message.Phase == ManualBelayPhase.Ended);
        Assert.Empty(world.Failures);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(201)]
    public void InvalidCapacityInTheClimbersAcceptCannotStartControl(float capacity)
    {
        using var world = new World();
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        var offer = world.Last(ManualBelayPhase.Offered);
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.Accept,
            Session = offer.Session, Station = offer.Station, RopeCapacity = capacity }).Committed);
        Assert.DoesNotContain(world.Messages, message => message.Phase == ManualBelayPhase.Started);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.BeginCalls);
    }

    [Fact]
    public void OfferingDoesNotAssumeTheBelayersOwnRopeCanCoverForAShorterClimberRope()
    {
        using var world = new World(participants: 2);
        world.Locations[2] = new(50, 0, 0, "Wall", PlayerState.InGame);
        world.Nodes[1].Game.BelayImpl.RopeCapacity = 100;
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        Assert.Equal(0, world.Last(ManualBelayPhase.Offered).RopeCapacity);
        world.Nodes[2].Game.ChatImpl.Run("accept");
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.DoesNotContain(world.Messages, message => message.Phase == ManualBelayPhase.Started);
    }

    [Fact]
    public void AClimbersRequestBeyondTheirOwnRopeCannotReserveTheHostSession()
    {
        using var world = new World(participants: 2);
        world.Locations[2] = new(50, 0, 0, "Wall", PlayerState.InGame);
        world.Nodes[2].Game.ChatImpl.Run("request 1");
        Assert.Empty(world.Messages);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
    }

    [Fact]
    public void TheBelayerCannotReplaceTheCapacityDeclaredByTheRequestingClimber()
    {
        using var world = new World(participants: 2);
        world.Nodes[2].Game.ChatImpl.Run("request 1");
        var offer = world.Last(ManualBelayPhase.Offered);
        var station = world.Nodes[1].Game.BelayImpl;
        station.BeginStation(out _);
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Accept,
            Session = offer.Session, Station = station.StationReference, RopeCapacity = 100 }).Committed);
        Assert.DoesNotContain(world.Messages, message => message.Phase == ManualBelayPhase.Started);
    }

    [Fact]
    public void ALongCapacityCannotOverrideTheNativeStationRouteRefusal()
    {
        using var world = new World(participants: 2);
        world.Locations[2] = new(30, 0, 0, "Wall", PlayerState.InGame);
        var climber = world.Nodes[2].Game;
        climber.BelayImpl.RopeCapacity = 100;
        climber.BelayImpl.SharedStationReady = false;
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        Assert.False(climber.PanelImpl.View.CanAccept);
        climber.ChatImpl.Run("accept");
        Assert.DoesNotContain(world.Messages, message => message.Phase == ManualBelayPhase.Started);
        Assert.Equal(0, climber.BelayImpl.BeginCalls);
    }

    [Fact]
    public void ACapacityChangeAfterConsentDoesNotExpandTheExistingSessionsRange()
    {
        using var world = new World(participants: 2);
        var climber = world.Nodes[2].Game.BelayImpl;
        climber.RopeCapacity = 60;
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        world.Nodes[2].Game.ChatImpl.Run("accept");
        world.Tick();
        climber.RopeCapacity = 100;
        world.Locations[2] = new(70, 0, 0, "Wall", PlayerState.InGame);
        world.Time += .6f; world.Tick();
        Assert.Contains(world.Messages, message => message.Phase == ManualBelayPhase.Ended);
        Assert.False(climber.Active);
    }
}
