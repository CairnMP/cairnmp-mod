using System;
using System.Linq;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.GameApi;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed partial class ManualBelayFeatureTests
{
    private static ManualBelayMessage RequestSwap(World world, int belayer = 1)
    {
        world.Nodes[belayer].Game.PanelImpl.Act(BelayPanelAction.ProposeSwap);
        return world.Last(ManualBelayPhase.SwapRequested);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 3)]
    public void APlainBelayerRequestNeedsTheClimbersRealStationAndThenTheBelayersConfirmation(int belayer, int climber)
    {
        using var world = ConfirmedRecoveryPair(belayer, climber);
        var previous = world.Last(ManualBelayPhase.Started);
        var requested = RequestSwap(world, belayer);
        Assert.Equal(default, requested.Station);
        foreach (var id in new[] { belayer, climber })
        {
            Assert.Equal(BelayPanelPhase.SwapRequest, world.Nodes[id].Game.PanelImpl.View.Phase);
            Assert.Equal(0, world.Nodes[id].Game.BelayImpl.RoleStationPrepareCalls);
            Assert.Equal(0, world.Nodes[id].Game.BelayImpl.RolePrepareCalls);
        }
        Assert.False(world.Nodes[belayer].Game.PanelImpl.View.CanAccept);
        Assert.True(world.Nodes[climber].Game.PanelImpl.View.CanAccept);
        AssertRole(world, belayer, "SEGURADOR / BELAYER", "P" + climber);
        AssertRole(world, climber, "ESCALADOR / CLIMBER", "P" + belayer);
        world.Nodes[belayer].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapOffered);

        world.Nodes[climber].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        var offered = world.Last(ManualBelayPhase.SwapOffered);
        Assert.Equal(requested.Swap, offered.Swap);
        Assert.NotEqual(previous.Station.Id, offered.Station.Id);
        Assert.Equal(1, world.Nodes[climber].Game.BelayImpl.RoleStationPrepareCalls);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapPreparing);
        Assert.True(world.Nodes[climber].Game.BelayImpl.Active);
        Assert.Equal(previous.Session, world.Nodes[belayer].Game.BelayImpl.ProtectedSession);
        AssertRole(world, belayer, "SEGURADOR / BELAYER", "P" + climber);

        world.Nodes[belayer].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        Advance(world, .11f); Advance(world, .11f);
        var committed = world.Last(ManualBelayPhase.SwapCommitted);
        Assert.Equal(requested.Swap, committed.Swap);
        Assert.Equal(climber, committed.Belayer); Assert.Equal(belayer, committed.Climber);
        Assert.True(world.Nodes[belayer].Game.BelayImpl.Active);
        Assert.True(world.Nodes[belayer].Game.BelayImpl.LastBrake);
        Assert.False(world.Nodes[climber].Game.BelayImpl.Active);
        Assert.Equal(committed.Swap, world.Nodes[climber].Game.BelayImpl.ProtectedSession);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void ARequestedExchangeWaitsForTheClimbersActualAnchorAndShowsTheBlockingReason()
    {
        using var world = ConfirmedRecoveryPair();
        var climber = world.Nodes[2].Game;
        climber.BelayImpl.Anchored = false;
        RequestSwap(world);
        Assert.False(climber.PanelImpl.View.CanAccept);
        Assert.Contains("Remain anchored", climber.PanelImpl.View.Description);
        climber.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapOffered);
        Assert.Equal(0, world.Nodes[1].Game.BelayImpl.RolePrepareCalls);
        Advance(world, .11f);
        Assert.True(climber.BelayImpl.Active);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCancelled);
        climber.BelayImpl.Anchored = true;
        climber.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        Assert.Single(world.Messages, m => m.Phase == ManualBelayPhase.SwapOffered);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapPreparing);
    }

    [Fact]
    public void RepeatedRequestOrAcceptWhileCommandsAreInFlightDoesNotPrepareAnotherStation()
    {
        using var world = ConfirmedRecoveryPair(2, 1);
        world.DeferCommands = true;
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.ProposeSwap);
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.ProposeSwap);
        Assert.Single(world.Pending);
        world.DeferCommands = false; world.FlushCommands();
        Assert.Single(world.Messages, m => m.Phase == ManualBelayPhase.SwapRequested);
        // Now the guest is the climber, so its acceptance can be delayed too.
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.CancelSwap);
        using var other = ConfirmedRecoveryPair();
        RequestSwap(other); other.DeferCommands = true;
        other.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        other.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        Assert.Single(other.Pending);
        Assert.Equal(1, other.Nodes[2].Game.BelayImpl.RoleStationPrepareCalls);
        other.DeferCommands = false; other.FlushCommands();
        Assert.Single(other.Messages, m => m.Phase == ManualBelayPhase.SwapOffered);
    }

    [Fact]
    public void ALateRequestRejectionCannotCancelTheClimbersCompetingValidProposal()
    {
        using var world = ConfirmedRecoveryPair(2, 1);
        world.DeferCommands = true;
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.ProposeSwap);
        Assert.Single(world.Pending);
        var offered = ProposeSwap(world, 1); // The host's own command bypasses the deferred bridge.
        Assert.Equal(BelayPanelPhase.SwapInvitation, world.Nodes[2].Game.PanelImpl.View.Phase);
        world.DeferCommands = false; world.FlushCommands();
        world.Nodes[2].Runtime.Tick();
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCancelled);
        Assert.Equal(BelayPanelPhase.SwapInvitation, world.Nodes[2].Game.PanelImpl.View.Phase);
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        Advance(world, .11f); Advance(world, .11f);
        Assert.Equal(offered.Swap, world.Last(ManualBelayPhase.SwapCommitted).Swap);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
    }

    [Fact]
    public void ARejectionFromAnExpiredRequestCannotClearANewerStillPendingAttempt()
    {
        using var world = ConfirmedRecoveryPair(2, 1);
        world.DeferCommands = true;
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.ProposeSwap);
        var old = Assert.Single(world.Pending); world.Pending.Clear();
        world.DeferCommands = false;
        for (var i = 0; i < 12; i++) Advance(world, .5f);
        Assert.True(world.Nodes[1].Game.BelayImpl.Active);
        world.DeferCommands = true;
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.ProposeSwap);
        Assert.Single(world.Pending);
        // Revalidate the old command in a briefly unavailable host registry so
        // its delayed response is a rejection, while the new attempt is pending.
        world.Nodes[1].Game.BelayImpl.KnownStation = false;
        world.Deliver(old.Sender, old.Packet);
        world.Nodes[2].Runtime.Tick();
        world.Nodes[1].Game.BelayImpl.KnownStation = true;
        world.DeferCommands = false; world.FlushCommands();
        Assert.Single(world.Messages, m => m.Phase == ManualBelayPhase.SwapRequested);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCancelled);
        Assert.Equal(BelayPanelPhase.SwapRequest, world.Nodes[2].Game.PanelImpl.View.Phase);
        Assert.True(world.Nodes[1].Game.BelayImpl.Active);
    }

    [Fact]
    public void ARequestRequiresTheBelayerTheCurrentStationAndTheHostNonceBeforeProposal()
    {
        using var world = ConfirmedRecoveryPair();
        var current = world.Last(ManualBelayPhase.Started);
        ManualBelayCommand Request() => new() { Operation = ManualBelayOperation.RequestSwap,
            Session = current.Session, Station = current.Station };
        Assert.False(world.Execute(2, Request()).Committed);
        Assert.False(world.Execute(3, Request()).Committed);
        var wrong = Request(); wrong.Station = wrong.Station with { Id = Guid.NewGuid() };
        Assert.False(world.Execute(1, wrong).Committed);
        wrong = Request(); wrong.RopeCapacity = 40; Assert.False(world.Execute(1, wrong).Committed);
        wrong = Request(); wrong.Swap = Guid.NewGuid(); Assert.False(world.Execute(1, wrong).Committed);
        var requested = RequestSwap(world);
        Assert.False(world.Execute(1, Request()).Committed);
        var proposal = new ManualBelayCommand { Operation = ManualBelayOperation.ProposeSwap,
            Session = current.Session, Swap = Guid.NewGuid(),
            Station = new(Guid.NewGuid(), world.Nodes[2].Game.BelayImpl.StationPosition, 2, 7) };
        Assert.False(world.Execute(2, proposal).Committed);
        proposal.Swap = requested.Swap;
        Assert.False(world.Execute(1, proposal).Committed);
        Assert.False(world.Execute(3, proposal).Committed);
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.AcceptSwap,
            Session = current.Session, Swap = requested.Swap, RopeCapacity = 40 }).Committed);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapPreparing);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0)]
    public void DecliningOrTimingOutARequestKeepsCurrentRolesAndRetiresLateEvents(int canceller)
    {
        using var world = ConfirmedRecoveryPair();
        var requested = RequestSwap(world);
        var packet = world.Events.Last(e => Decode(e).Phase == ManualBelayPhase.SwapRequested);
        if (canceller != 0) world.Nodes[canceller].Game.PanelImpl.Act(BelayPanelAction.CancelSwap);
        else for (var i = 0; i < 42; i++) Advance(world, .5f);
        Assert.Contains(world.Messages, m => m.Phase == ManualBelayPhase.SwapCancelled);
        foreach (var id in new[] { 1, 2 })
        {
            world.Nodes[id].Runtime.ApplyEvent(packet);
            world.Nodes[id].Game.PanelImpl.Act(BelayPanelAction.Toggle);
            Assert.Equal(BelayPanelPhase.Active, world.Nodes[id].Game.PanelImpl.View.Phase);
            Assert.Equal(0, world.Nodes[id].Game.BelayImpl.RoleStationPrepareCalls);
        }
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCommitted);
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.ProposeSwap,
            Session = requested.Session, Swap = requested.Swap,
            Station = new(Guid.NewGuid(), world.Nodes[2].Game.BelayImpl.StationPosition, 2, 7) }).Committed);
    }

    [Fact]
    public void CancellationReceivedBeforeTheDelayedRequestPreventsTheRequestFromReappearing()
    {
        using var world = ConfirmedRecoveryPair();
        world.DeferredEventRecipient = 2; world.DeferredPhase = ManualBelayPhase.SwapRequested;
        RequestSwap(world);
        world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.CancelSwap);
        world.DeferredEventRecipient = -1; world.FlushEvents();
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.Toggle);
        Assert.Equal(BelayPanelPhase.Active, world.Nodes[2].Game.PanelImpl.View.Phase);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.RoleStationPrepareCalls);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCommitted);
    }

    [Fact]
    public void EndDuringTheClimbersStationProposalCannotCommitOrResurrectTheRequest()
    {
        using var world = ConfirmedRecoveryPair();
        RequestSwap(world); world.DeferCommands = true;
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        Assert.Single(world.Pending);
        world.Nodes[1].Game.ChatImpl.Run("stop");
        world.DeferCommands = false; world.FlushCommands(); Advance(world, .11f);
        Assert.False(world.Nodes[1].Game.BelayImpl.Active);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapOffered);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCommitted);
    }

    [Fact]
    public void RecoveryDropsTheUnconfirmedRequestAndOldEventsCannotFollowTheNewSession()
    {
        using var world = ConfirmedRecoveryPair();
        RequestSwap(world);
        var packet = world.Events.Last(e => Decode(e).Phase == ManualBelayPhase.SwapRequested);
        InterruptRecoveryPair(world);
        world.Nodes[2].Game.Keys.Focused = true;
        PumpRecovery(world);
        Assert.Contains(world.Messages, m => m.Phase == ManualBelayPhase.Resumed);
        foreach (var id in new[] { 1, 2 })
        {
            world.Nodes[id].Runtime.ApplyEvent(packet);
            world.Nodes[id].Game.PanelImpl.Act(BelayPanelAction.Toggle);
            Assert.Equal(BelayPanelPhase.Active, world.Nodes[id].Game.PanelImpl.View.Phase);
        }
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapOffered);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCommitted);
    }
}
