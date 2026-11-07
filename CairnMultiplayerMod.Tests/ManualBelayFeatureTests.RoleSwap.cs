using System;
using System.IO;
using System.Linq;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.GameApi;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed partial class ManualBelayFeatureTests
{
    private static ManualBelayMessage ProposeSwap(World world, int climber = 2)
    {
        world.Nodes[climber].Game.PanelImpl.Act(BelayPanelAction.ProposeSwap);
        return world.Last(ManualBelayPhase.SwapOffered);
    }

    [Fact]
    public void RepeatedProposeWhileTheFirstCommandIsInFlightKeepsTheSamePreparedStation()
    {
        using var world = World.Started();
        world.DeferCommands = true;
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.ProposeSwap);
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.ProposeSwap);
        Assert.Single(world.Pending);
        world.DeferCommands = false; world.FlushCommands(); world.Tick();
        Assert.Single(world.Messages, m => m.Phase == ManualBelayPhase.SwapOffered);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCancelled);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void BothHostArrangementsExchangeOnlyAfterBothReadyAndStartBraked(int belayer, int climber)
    {
        using var world = new World();
        world.Nodes[belayer].Game.ChatImpl.Run("offer " + climber);
        world.Nodes[climber].Game.ChatImpl.Run("accept"); world.Tick();
        var old = world.Last(ManualBelayPhase.Started);
        var proposed = ProposeSwap(world, climber);
        Assert.True(world.Nodes[climber].Game.BelayImpl.Active);
        Assert.Equal(old.Session, world.Nodes[belayer].Game.BelayImpl.ProtectedSession);
        world.Nodes[belayer].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        world.Tick(); world.Time += .1f; world.Tick();
        var swapped = world.Last(ManualBelayPhase.SwapCommitted);
        Assert.Equal(climber, swapped.Belayer); Assert.Equal(belayer, swapped.Climber);
        Assert.Equal(proposed.Swap, swapped.Swap); Assert.NotEqual(old.Session, swapped.Swap);
        Assert.False(world.Nodes[climber].Game.BelayImpl.Active);
        Assert.Equal(swapped.Swap, world.Nodes[climber].Game.BelayImpl.ProtectedSession);
        Assert.True(world.Nodes[belayer].Game.BelayImpl.Active);
        Assert.True(world.Nodes[belayer].Game.BelayImpl.LastBrake);
        Assert.True(world.Nodes[belayer].Game.BelayImpl.Anchored);
        Assert.True(world.Nodes[climber].Game.BelayImpl.Anchored);
        Assert.Empty(world.Failures);
        Assert.False(world.Execute(belayer, new() { Operation = ManualBelayOperation.Intent,
            Session = old.Session, Sequence = 999, Braked = false }).Committed);
        Assert.True(world.Nodes[belayer].Game.BelayImpl.LastBrake);
    }

    [Fact]
    public void PreparingDoesNotReleaseTheOldControllerOrAllowPanelCloseAndCancelKeepsRoles()
    {
        using var world = World.Started();
        var old = world.Last(ManualBelayPhase.Started);
        world.Nodes[1].Game.BelayImpl.RolePreparationReady = false;
        ProposeSwap(world);
        world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap); world.Tick();
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCommitted);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Equal(old.Session, world.Nodes[1].Game.BelayImpl.ProtectedSession);
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.Close);
        world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.Toggle);
        Assert.True(world.Nodes[2].Game.PanelImpl.IsOpen); Assert.True(world.Nodes[1].Game.PanelImpl.IsOpen);
        world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.CancelSwap); world.Time += .1f; world.Tick();
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Equal(old.Session, world.Nodes[1].Game.BelayImpl.ProtectedSession);
        Assert.Equal(BelayPanelPhase.Active, world.Nodes[2].Game.PanelImpl.View.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeclineOrNativePreparationFailureRetainsTheOriginalPair(bool nativeFailure)
    {
        using var world = World.Started();
        ProposeSwap(world);
        if (nativeFailure)
        {
            world.Nodes[1].Game.BelayImpl.RolePreparationFailed = true;
            world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap); world.Tick();
        }
        else world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.CancelSwap);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.True(world.Nodes[1].Game.BelayImpl.StationValid);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCommitted);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Ended);
        Assert.Equal(0, world.Nodes[1].Game.BelayImpl.BeginCalls);
    }

    [Fact]
    public void ProposalAndReadinessRequireTheAgreedRolesStationNonceAndRopeCapacity()
    {
        using var world = World.Started();
        var offered = ProposeSwap(world);
        ManualBelayCommand Accept() => new() { Operation = ManualBelayOperation.AcceptSwap,
            Session = offered.Session, Swap = offered.Swap, Station = offered.Station, RopeCapacity = 40 };
        Assert.False(world.Execute(2, Accept()).Committed);
        Assert.False(world.Execute(3, Accept()).Committed);
        var wrong = Accept(); wrong.Swap = Guid.NewGuid(); Assert.False(world.Execute(1, wrong).Committed);
        wrong = Accept(); wrong.Station = wrong.Station with { Id = Guid.NewGuid() }; Assert.False(world.Execute(1, wrong).Committed);
        wrong = Accept(); wrong.RopeCapacity = float.NaN; Assert.False(world.Execute(1, wrong).Committed);
        world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        var ready = new ManualBelayCommand { Operation = ManualBelayOperation.ReadySwap,
            Session = offered.Session, Swap = offered.Swap, Station = offered.Station };
        Assert.False(world.Execute(3, ready).Committed);
        Assert.True(world.Execute(2, ready).Committed);
        Assert.False(world.Execute(2, ready).Committed);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCommitted);
        world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.CancelSwap);
        Assert.False(world.Execute(1, ready).Committed);
    }

    [Fact]
    public void AStationLossBeforeCommitCancelsTheProposalWhileTheOldControllerRemains()
    {
        using var world = World.Started();
        ProposeSwap(world);
        world.Nodes[2].Game.BelayImpl.Anchored = false; world.Tick();
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.True(world.Nodes[1].Game.BelayImpl.StationValid);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCommitted);
    }

    [Fact]
    public void RoleProposalTimeoutKeepsTheExistingPairWithFreshOriginalBelayerInput()
    {
        using var world = World.Started();
        ProposeSwap(world);
        for (var i = 0; i < 42; i++) { world.Time += .5f; world.Tick(); }
        Assert.Contains(world.Messages, m => m.Phase == ManualBelayPhase.SwapCancelled);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCommitted);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
    }

    [Fact]
    public void CommitFailureEndsTheNewSessionAndRetainsPersonalAnchors()
    {
        using var world = World.Started();
        world.Nodes[1].Game.BelayImpl.RoleCommitFailed = true;
        ProposeSwap(world); world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        world.Tick(); world.Time += .1f; world.Tick();
        Assert.Contains(world.Messages, m => m.Phase == ManualBelayPhase.SwapCommitted);
        Assert.Contains(world.Messages, m => m.Phase == ManualBelayPhase.Ended);
        Assert.True(world.Nodes[1].Game.BelayImpl.Anchored); Assert.True(world.Nodes[2].Game.BelayImpl.Anchored);
        Assert.False(world.Nodes[1].Game.BelayImpl.Active); Assert.False(world.Nodes[2].Game.BelayImpl.Active);
    }

    [Fact]
    public void SwappingBackUsesAnotherTransactionAndOldReadyCannotReplayIt()
    {
        using var world = World.Started();
        var first = ProposeSwap(world); world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        world.Tick(); world.Time += .1f; world.Tick();
        var second = ProposeSwap(world, 1); world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        world.Tick(); world.Time += .1f; world.Tick();
        Assert.Equal(2, world.Messages.Count(m => m.Phase == ManualBelayPhase.SwapCommitted));
        Assert.NotEqual(first.Swap, second.Swap);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.ReadySwap,
            Session = first.Session, Swap = first.Swap, Station = first.Station }).Committed);
    }

    [Fact]
    public void RoleTransactionIdentitySurvivesTheFixedSizeWireContract()
    {
        var command = new ManualBelayCommand { Operation = ManualBelayOperation.ReadySwap, Swap = Guid.NewGuid() };
        using var reader = new BinaryReader(new MemoryStream(Bytes(command)));
        var decoded = new ManualBelayCommand(); decoded.Deserialize(reader);
        Assert.Equal(command.Swap, decoded.Swap); Assert.Equal(ManualBelayOperation.ReadySwap, decoded.Operation);
        var message = new ManualBelayMessage { Phase = ManualBelayPhase.SwapCommitted, Swap = command.Swap };
        using var eventReader = new BinaryReader(new MemoryStream(Bytes(message)));
        var decodedEvent = new ManualBelayMessage(); decodedEvent.Deserialize(eventReader);
        Assert.Equal(command.Swap, decodedEvent.Swap);
    }

    [Fact]
    public void AnOldBelayerHeartbeatRejectionCanArriveBeforeTheRoleCommitWithoutDestroyingConsent()
    {
        using var world = new World();
        world.Nodes[2].Game.ChatImpl.Run("offer 1"); world.Nodes[1].Game.ChatImpl.Run("accept"); world.Tick();
        ProposeSwap(world, 1); world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        world.DroppedEventRecipient = 2; world.Tick();
        var committed = world.Events.Last(e => Decode(e).Phase == ManualBelayPhase.SwapCommitted);
        Assert.True(world.Nodes[2].Game.BelayImpl.StationValid); // Old heartbeat was rejected, commit still in flight.
        world.DroppedEventRecipient = -1;
        world.Nodes[2].Runtime.ApplyEvent(committed);
        world.Time += .1f; world.Tick();
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.True(world.Nodes[2].Game.BelayImpl.LastBrake);
        Assert.Equal(Decode(committed).Swap, world.Nodes[1].Game.BelayImpl.ProtectedSession);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Ended);
    }
}
