using System;
using System.Linq;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.GameApi;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed partial class ManualBelayFeatureTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void OnlyTheBelayersHeldKeysFeedAndTakeInOnTheClimbersBudget(int belayer, int climber)
    {
        using var world = new World();
        world.Nodes[belayer].Game.ChatImpl.Run("offer " + climber);
        world.Nodes[climber].Game.ChatImpl.Run("accept");
        world.Tick();
        var source = world.Nodes[belayer].Game;
        var target = world.Nodes[climber].Game;
        target.Keys.Up = true;
        Advance(world, .11f);
        Assert.Equal(8f, target.BelayImpl.Rope.Released);
        source.Keys.Up = true;
        for (var i = 0; i < 20; i++) Advance(world, .11f);
        var fed = target.BelayImpl.Rope.Released;
        Assert.InRange(fed, 9.9f, 10.1f);
        source.Keys.Up = false;
        Advance(world, .11f);
        Assert.Equal(0, target.BelayImpl.Rope.Motion);
        // The host's native step may run before the remote belayer's frame.
        Assert.InRange(target.BelayImpl.Rope.Released, fed, fed + .11f);
        var stopped = target.BelayImpl.Rope.Released;
        Advance(world, .11f);
        Assert.Equal(stopped, target.BelayImpl.Rope.Released);
        source.Keys.Down = true;
        for (var i = 0; i < 20; i++) Advance(world, .11f);
        Assert.InRange(target.BelayImpl.Rope.Released, 7.9f, 8.1f);
        Assert.Equal(0, source.BelayImpl.SetCalls);
        Assert.True(target.BelayImpl.Active);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void BrakeStopsFeedWhileHeldTakeInRemovesOnlySlack()
    {
        using var world = World.Started();
        var source = world.Nodes[1].Game;
        var target = world.Nodes[2].Game.BelayImpl;
        source.Keys.Up = true;
        Advance(world, .11f);
        source.Keys.Pressed = true;
        Advance(world, .11f);
        Assert.True(target.LastBrake);
        Assert.Equal(0, target.Rope.Motion);
        var locked = target.Rope.Released;
        for (var i = 0; i < 5; i++) Advance(world, .11f);
        Assert.Equal(locked, target.Rope.Released);
        source.Keys.Up = false; source.Keys.Down = true;
        for (var i = 0; i < 40; i++) Advance(world, .11f);
        Assert.True(target.LastBrake);
        Assert.Equal(5.03f, target.Rope.Released, 4);
        Assert.Equal(5, target.Rope.Distance);
        Assert.True(target.Active);
        Assert.Empty(world.Failures);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CapturingInputPausingTypingOrOpeningThePanelSendsNoMotion(int context)
    {
        using var world = World.Started();
        var source = world.Nodes[1].Game;
        var target = world.Nodes[2].Game.BelayImpl;
        source.Keys.Up = true;
        Advance(world, .11f);
        Assert.Equal(1, target.Rope.Motion);
        if (context == 0) source.Keys.Captured = true;
        if (context == 1) source.Keys.Paused = true;
        if (context == 2) source.ChatImpl.Typing = true;
        if (context == 3) source.PanelImpl.Act(BelayPanelAction.Toggle);
        source.Keys.Pressed = true;
        Advance(world, .11f);
        Assert.Equal(0, target.Rope.Motion);
        Assert.False(target.LastBrake);
        Assert.True(target.Active);
    }

    [Fact]
    public void BothArrowKeysCancelRatherThanSelectingAnArbitraryDirection()
    {
        using var world = World.Started();
        world.Nodes[1].Game.Keys.Up = world.Nodes[1].Game.Keys.Down = true;
        Advance(world, .11f);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.Rope.Motion);
        Assert.Equal(8, world.Nodes[2].Game.BelayImpl.Rope.Released);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    public void LostRenewalsStopMotionBeforeTheBrakeAndPairPermissionExpire(int motion, bool braked)
    {
        using var world = World.Started();
        var source = world.Nodes[1].Game;
        source.Keys.Up = motion > 0; source.Keys.Down = motion < 0; source.Keys.Pressed = braked;
        Advance(world, .11f);
        var target = world.Nodes[2].Game.BelayImpl;
        Assert.Equal(motion, target.Rope.Motion);
        world.DroppedEventRecipient = 2;
        for (var i = 0; i < 5; i++) Advance(world, .11f);
        Assert.Equal(0, target.Rope.Motion);
        Assert.Equal(braked, target.LastBrake);
        Assert.True(target.Active);
        var stoppedLength = target.Rope.Released;
        Advance(world, .5f);
        Assert.Equal(stoppedLength, target.Rope.Released);
        Advance(world, 2);
        Assert.False(target.Active);
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ClimberAndThirdPlayerCannotInjectFeedOrTakeIn(int actor)
    {
        using var world = World.Started();
        var session = world.Last(ManualBelayPhase.Started).Session;
        world.Time += .11f;
        foreach (var motion in new sbyte[] { -1, 1 })
            Assert.False(world.Execute(actor, new() { Operation = ManualBelayOperation.Intent,
                Session = session, Sequence = 2, Motion = motion }).Committed);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.Rope.Motion);
        Assert.Equal(8, world.Nodes[2].Game.BelayImpl.Rope.Released);
    }

    [Theory]
    [InlineData(-128)]
    [InlineData(-2)]
    [InlineData(2)]
    [InlineData(127)]
    public void InvalidMotionDoesNotConsumeASequenceOrItsRateCredit(int motion)
    {
        using var world = World.Started();
        var session = world.Last(ManualBelayPhase.Started).Session;
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Intent,
            Session = session, Sequence = 2, Motion = (sbyte)motion }).Committed);
        Assert.True(world.Execute(1, new() { Operation = ManualBelayOperation.Intent,
            Session = session, Sequence = 2, Motion = -1 }).Committed);
        Assert.Equal(-1, world.Nodes[2].Game.BelayImpl.Rope.Motion);
    }

    [Fact]
    public void SmallDeliveryBurstsAreAcceptedButSustainedFloodingIsBounded()
    {
        using var world = World.Started();
        var session = world.Last(ManualBelayPhase.Started).Session;
        world.Time += .2f;
        Assert.True(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = session, Sequence = 2, Motion = 1 }).Committed);
        Assert.True(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = session, Sequence = 3, Motion = 0, Braked = true }).Committed);
        for (uint sequence = 4; sequence < 104; sequence++)
            Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = session, Sequence = sequence }).Committed);
        Assert.True(world.Nodes[2].Game.BelayImpl.LastBrake);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.Rope.Motion);
        world.Time += .1f;
        Assert.True(world.Execute(1, new() { Operation = ManualBelayOperation.Intent, Session = session, Sequence = 104, Motion = -1, Braked = true }).Committed);
    }

    [Fact]
    public void RapidLocalKeyChangesAreCoalescedWithoutRejectingThePair()
    {
        using var world = World.Started();
        var source = world.Nodes[1].Game;
        for (var i = 0; i < 100; i++)
        {
            source.Keys.Up = i % 2 == 0; source.Keys.Down = !source.Keys.Up;
            source.Keys.Pressed = true;
            Advance(world, .01f);
        }
        Assert.InRange(world.Messages.Count(m => m.Phase == ManualBelayPhase.Intent), 2, 12);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Ended);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void DelayedInputBurstRetriesCurrentKeysInsteadOfEndingOrReplayingOldMotion()
    {
        using var world = new World();
        var source = world.Nodes[2].Game;
        source.ChatImpl.Run("offer 1");
        world.Nodes[1].Game.ChatImpl.Run("accept");
        world.Tick();
        world.DeferCommands = true;
        source.Keys.Up = true;
        for (var i = 0; i < 5; i++) Advance(world, .11f);
        source.Keys.Up = false; source.Keys.Down = true;
        world.DeferCommands = false;
        world.FlushCommands();
        Assert.True(world.Nodes[1].Game.BelayImpl.Active);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Ended);
        Advance(world, .11f);
        Assert.Equal(-1, world.Last(ManualBelayPhase.Intent).Motion);
        Assert.Equal(-1, world.Nodes[1].Game.BelayImpl.Rope.Motion);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void NativeInputRefusalEndsTheSessionWithoutFallingBackToBrakeOnlyControl()
    {
        using var world = World.Started();
        world.Nodes[2].Game.BelayImpl.CanApplyInput = false;
        world.Nodes[1].Game.Keys.Down = true;
        Advance(world, .11f);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.Rope.Motion);
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
    }

    [Fact]
    public void ActiveControlWithoutABudgetReadbackCannotContinueAsPartnerBelay()
    {
        using var world = World.Started();
        var target = world.Nodes[2].Game.BelayImpl;
        target.SuppressRopeReadback = true;
        Advance(world, .11f);
        Assert.False(target.Active);
        Assert.Equal(0, target.Rope.Motion);
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(8f, -1f, 32f)]
    [InlineData(8f, 9f, 32f)]
    [InlineData(8f, 3f, -1f)]
    [InlineData(8f, 3f, float.PositiveInfinity)]
    [InlineData(8f, 3f, 193f)]
    public void InvalidBudgetReportsCannotBecomeClimberReadback(float released, float slack, float reserve)
    {
        using var world = World.Started();
        world.Time += .31f;
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.Report,
            Session = world.Last(ManualBelayPhase.Started).Session, Sequence = 1,
            Released = released, Slack = slack, Reserve = reserve }).Committed);
        Assert.Equal(8, world.Last(ManualBelayPhase.Telemetry).Released);
    }

    [Fact]
    public void RouteRebindingCannotClaimThatItsNativeLengthIsAlreadyApplied()
    {
        using var world = World.Started();
        world.Time += .31f;
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.Report,
            Session = world.Last(ManualBelayPhase.Started).Session, Sequence = 1,
            Released = 8, Slack = 3, Reserve = 32, Rebinding = true, LengthApplied = true }).Committed);
    }

    [Fact]
    public void BelayerPanelShowsClimberBudgetReadbackAndTheHeldKeyInstructions()
    {
        using var world = World.Started();
        var source = world.Nodes[1].Game;
        source.PanelImpl.Act(BelayPanelAction.Toggle);
        Advance(world, .31f);
        var description = source.PanelImpl.View.Description;
        Assert.Contains("Slack", description);
        Assert.Contains("reserve", description);
        Assert.Contains("native length matches", description);
        Assert.Contains("hold Up/Down", description);
    }

    private static void Advance(World world, float elapsed) { world.Time += elapsed; world.Tick(); }
}
