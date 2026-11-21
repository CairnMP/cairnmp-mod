using System;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed partial class ManualBelayFeatureTests
{
    private static BelayRopeVisualFrame Drawing(World world, uint sequence = 1)
        => new() { Session = world.Last(ManualBelayPhase.Started).Session, Sequence = sequence, Revision = 1,
            Points = new WorldPosition[] { new(0, 0, 0), new(.5f, -.3f, 0), new(1, 0, 0) } };

    private static void Draw(World world, int sender, int recipient, BelayRopeVisualFrame frame)
        => world.Nodes[recipient].Host.DispatchStream(sender, FeatureStreamRouter.Hash("manual-belay.rope-visual-v1"), Bytes(frame));

    [Fact]
    public void OnlyTheConsentingBelayerDrawsTheActiveClimbersRopeWithoutChangingPhysics()
    {
        using var world = World.Started();
        var drawing = Drawing(world);
        var belayer = world.Nodes[1].Game.BelayImpl;
        var climber = world.Nodes[2].Game.BelayImpl;
        var before = climber.SetCalls;
        Draw(world, 3, 1, drawing); // An outsider cannot impersonate the climber.
        Draw(world, 2, 3, drawing); // A spectator has not joined this pair.
        Draw(world, 1, 2, drawing); // The climber retains the native rope.
        Assert.Null(belayer.Visual); Assert.Null(climber.Visual); Assert.Null(world.Nodes[3].Game.BelayImpl.Visual);
        Draw(world, 2, 1, drawing);
        Assert.Equal(drawing.Points, belayer.Visual);
        Assert.Equal(before, climber.SetCalls);
        Assert.Equal(0, belayer.BeginCalls);
        Assert.True(climber.Active);
    }

    [Fact]
    public void StaleOrWrongSessionDrawingCannotReplaceTheCurrentRoute()
    {
        using var world = World.Started();
        Draw(world, 2, 1, Drawing(world, 2));
        world.Time += .1f;
        Draw(world, 2, 1, Drawing(world, 1));
        var wrong = Drawing(world, 3); wrong.Session = Guid.NewGuid(); Draw(world, 2, 1, wrong);
        Assert.Equal(1, world.Nodes[1].Game.BelayImpl.VisualCalls);
        Draw(world, 2, 1, Drawing(world, 3));
        Assert.Equal(2, world.Nodes[1].Game.BelayImpl.VisualCalls);
    }

    [Fact]
    public void DrawingExpiresWithoutEndingThePairAndItsSequenceCannotBeReplayed()
    {
        using var world = World.Started();
        Draw(world, 2, 1, Drawing(world, 5));
        world.Time += .8f; world.Tick();
        Assert.Null(world.Nodes[1].Game.BelayImpl.Visual);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Draw(world, 2, 1, Drawing(world, 4));
        Assert.Null(world.Nodes[1].Game.BelayImpl.Visual);
        Draw(world, 2, 1, Drawing(world, 6));
        Assert.NotNull(world.Nodes[1].Game.BelayImpl.Visual);
    }

    [Fact]
    public void AnEmptyNewFrameHidesTheRouteDuringRebindingAndEndClearsIt()
    {
        using var world = World.Started();
        Draw(world, 2, 1, Drawing(world));
        world.Time += .1f;
        var empty = Drawing(world, 2); empty.Points = Array.Empty<WorldPosition>(); Draw(world, 2, 1, empty);
        Assert.Null(world.Nodes[1].Game.BelayImpl.Visual);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        world.Time += .1f; Draw(world, 2, 1, Drawing(world, 3));
        world.Nodes[1].Game.ChatImpl.Run("stop");
        Assert.Null(world.Nodes[1].Game.BelayImpl.Visual);
        Draw(world, 2, 1, DrawingFrom(empty.Session, 4));
        Assert.Null(world.Nodes[1].Game.BelayImpl.Visual);
    }

    private static BelayRopeVisualFrame DrawingFrom(Guid session, uint sequence)
        => new() { Session = session, Sequence = sequence, Points = new WorldPosition[] { new(0, 0, 0), new(1, 0, 0) } };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DrawingsCannotBypassPauseOrFocusLoss(bool pause)
    {
        using var world = World.Started();
        world.Nodes[1].Game.Keys.Paused = pause;
        world.Nodes[1].Game.Keys.Focused = pause;
        Draw(world, 2, 1, Drawing(world));
        Assert.Null(world.Nodes[1].Game.BelayImpl.Visual);
    }

    [Fact]
    public void DrawingPacketRetainsNativeSlackAndRequiresAnExactBoundedPayload()
    {
        var frame = DrawingFrom(Guid.NewGuid(), 7);
        frame.Revision = 9;
        frame.Points = new WorldPosition[] { new(0, 0, 0), new(.5f, -2, 0), new(1, 0, 0) };
        var bytes = Bytes(frame);
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var copy = new BelayRopeVisualFrame(); copy.Deserialize(reader);
        Assert.Equal(frame.Session, copy.Session); Assert.Equal(frame.Sequence, copy.Sequence);
        Assert.Equal(frame.Revision, copy.Revision); Assert.Equal(frame.Points, copy.Points);
        Assert.True(copy.MatchesRoute(default, new(1, 0, 0), 40));
        foreach (var delta in new[] { -1, 1, -12 })
        {
            var malformed = (byte[])bytes.Clone(); Array.Resize(ref malformed, malformed.Length + delta);
            using var bad = new BinaryReader(new MemoryStream(malformed));
            Assert.Throws<InvalidDataException>(() => new BelayRopeVisualFrame().Deserialize(bad));
        }
        frame.Points = new WorldPosition[BelayRopeVisualFrame.MaximumPoints + 1];
        Assert.Throws<InvalidDataException>(() => Bytes(frame));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(500)]
    public void InvalidDrawingDoesNotConsumeSequenceOrPoisonTheFollowingFrame(float value)
    {
        using var world = World.Started();
        var invalid = Drawing(world); invalid.Points[1] = new(value, 0, 0);
        Draw(world, 2, 1, invalid);
        Assert.Null(world.Nodes[1].Game.BelayImpl.Visual);
        Draw(world, 2, 1, Drawing(world));
        Assert.NotNull(world.Nodes[1].Game.BelayImpl.Visual);
    }

    [Fact]
    public void DrawingMustStartAtTheStationAndEndNearTheActualClimber()
    {
        var frame = DrawingFrom(Guid.NewGuid(), 1);
        Assert.False(frame.MatchesRoute(new(10, 0, 0), new(1, 0, 0), 40));
        Assert.False(frame.MatchesRoute(default, new(10, 0, 0), 40));
        frame.Points = new WorldPosition[192];
        for (var i = 0; i < frame.Points.Length; i++) frame.Points[i] = new(i % 2, i % 2 == 0 ? 0 : -10, 0);
        frame.Points[^1] = new(1, 0, 0);
        Assert.False(frame.MatchesRoute(default, new(1, 0, 0), 40)); // A looping payload cannot exceed the capacity bound.
    }
}
