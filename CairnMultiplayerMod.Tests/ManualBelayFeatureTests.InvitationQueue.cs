using System.Collections.Generic;
using System.IO;
using System.Linq;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.GameApi;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed partial class ManualBelayFeatureTests
{
    private static void SetInvitationDialogue(World world, int player, bool dialogue)
    {
        world.Nodes[player].Game.Cutscene = dialogue;
        world.Nodes[player].Game.InGame = !dialogue;
        var position = world.Locations[player];
        world.Locations[player] = new(position.X, position.Y, position.Z, position.Scene,
            dialogue ? PlayerState.Loading : PlayerState.InGame);
    }

    private static ManualBelayCommand InvitationCommand(ClientExtensionCommand packet)
    {
        using var reader = new BinaryReader(new MemoryStream(packet.Payload));
        var command = new ManualBelayCommand(); command.Deserialize(reader); return command;
    }

    private static void AssertNoInvitationNativeReads(Game game)
    {
        Assert.Equal(0, game.BelayImpl.ReadinessChecks);
        Assert.Equal(0, game.BelayImpl.CapacityReads);
        Assert.Equal(0, game.BelayImpl.StatusReads);
        Assert.Equal(0, game.BelayImpl.StationDefineCalls);
        Assert.Equal(0, game.BelayImpl.BeginCalls);
        Assert.Equal(0, game.InteractionQueries);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void DialoguePanelAndQueueUseOnlyRosterMetadataThenDispatchOnceWithFreshReadiness(bool asking, int sender)
    {
        using var world = new World(participants: 2);
        var target = sender == 1 ? 2 : 1;
        var game = world.Nodes[sender].Game;
        SetInvitationDialogue(world, sender, true);
        game.PanelImpl.Act(BelayPanelAction.Toggle);
        Assert.True(game.PanelImpl.IsOpen);
        Assert.True(game.PanelImpl.View.AwaitingGameplay);
        var row = Assert.Single(game.PanelImpl.View.Partners);
        Assert.Equal(target, row.Id); Assert.True(float.IsNaN(row.Distance));
        for (var i = 0; i < 3; i++) Advance(world, .1f);
        Assert.True(game.PanelImpl.IsOpen);
        game.PanelImpl.Act(asking ? BelayPanelAction.Request : BelayPanelAction.Offer, target);
        Assert.False(game.PanelImpl.IsOpen);
        Assert.Contains("INVITATION QUEUED", game.HudImpl.Messages["manual-belay.belay-invitation-queue"]);
        for (var i = 0; i < 3; i++) Advance(world, .6f);
        Assert.Empty(world.SentBelayCommands);
        Assert.Empty(world.Messages);
        AssertNoInvitationNativeReads(game);

        game.BelayImpl.RopeCapacity = 12;
        SetInvitationDialogue(world, sender, false);
        Advance(world, .51f);
        var offered = Assert.Single(world.Messages, m => m.Phase == ManualBelayPhase.Offered);
        Assert.Equal(sender, offered.Initiator);
        Assert.Equal(asking ? target : sender, offered.Belayer);
        Assert.Equal(asking ? sender : target, offered.Climber);
        Assert.Equal(asking ? 12 : 0, offered.RopeCapacity);
        Assert.Equal(asking ? 0 : 1, game.BelayImpl.StationDefineCalls);
        if (sender != 1)
        {
            var command = InvitationCommand(Assert.Single(world.SentBelayCommands));
            Assert.Equal(asking ? ManualBelayOperation.Request : ManualBelayOperation.Offer, command.Operation);
            Assert.Equal(target, command.Target);
            Assert.Equal(asking ? 12 : 0, command.RopeCapacity);
        }
        else Assert.Empty(world.SentBelayCommands); // HostCommand executes locally without Bridge.SendCommand.
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Started);
        for (var i = 0; i < 5; i++) Advance(world, .1f);
        Assert.Single(world.Messages, m => m.Phase == ManualBelayPhase.Offered);
        Assert.DoesNotContain("manual-belay.belay-invitation-queue", game.HudImpl.Messages.Keys);
        Assert.False(world.Nodes[target].Game.BelayImpl.Active);
    }

    [Fact]
    public void ClosingTheQueuedPanelKeepsTheRequestButOtherActionsDoNotRunNativePreflight()
    {
        using var world = new World(participants: 2);
        var game = world.Nodes[1].Game;
        SetInvitationDialogue(world, 1, true);
        game.PanelImpl.Act(BelayPanelAction.Request, 2);
        game.PanelImpl.Act(BelayPanelAction.Toggle);
        Assert.Equal(BelayPanelPhase.Waiting, game.PanelImpl.View.Phase);
        Assert.True(game.PanelImpl.View.AwaitingGameplay);
        game.PanelImpl.Act(BelayPanelAction.Close);
        game.ChatImpl.Run("accept");
        game.PanelImpl.Act(BelayPanelAction.DefineStation);
        game.PanelImpl.Act(BelayPanelAction.Offer, 2);
        game.PanelImpl.Act(BelayPanelAction.Request, 2);
        Advance(world, .6f);
        AssertNoInvitationNativeReads(game);
        Assert.Empty(world.SentBelayCommands);
        Assert.Empty(world.Messages);
        Assert.Contains("manual-belay.belay-invitation-queue", game.HudImpl.Messages.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void CancelLeaveSceneSessionModeDeathDeadlineAndNewRoomDiscardTheLocalQueue(int ending)
    {
        using var world = new World(participants: 2);
        var game = world.Nodes[1].Game;
        SetInvitationDialogue(world, 1, true);
        game.PanelImpl.Act(BelayPanelAction.Offer, 2);
        if (ending == 0) game.ChatImpl.Run("stop");
        else if (ending == 1) { game.PanelImpl.Act(BelayPanelAction.Toggle); game.PanelImpl.Act(BelayPanelAction.Cancel); }
        else if (ending == 2) world.Nodes[1].Runtime.NotifyPlayerLeft(new MultiplayerPlayer(2, "P2", false, false));
        else if (ending == 3) world.Nodes[1].Host.NotifySceneReset();
        else if (ending == 4) world.Nodes[1].Host.NotifySessionEnded();
        else if (ending == 5) world.Mode = MultiplayerMode.RopeTeam;
        else if (ending == 6) game.BelayImpl.RecoveryAlive = false;
        else if (ending == 7) world.Time += 121;
        else world.Nodes[1].Host.NotifySessionStarted();
        world.Tick();
        Assert.DoesNotContain("manual-belay.belay-invitation-queue", game.HudImpl.Messages.Keys);
        world.Mode = MultiplayerMode.BelayTeam; game.BelayImpl.RecoveryAlive = true;
        SetInvitationDialogue(world, 1, false);
        for (var i = 0; i < 5; i++) Advance(world, .6f);
        Assert.Empty(world.SentBelayCommands);
        Assert.Empty(world.Messages);
        Assert.Equal(0, game.BelayImpl.BeginCalls);
        Assert.Equal(0, game.BelayImpl.StationDefineCalls);
    }

    [Fact]
    public void TheQueueWaitsForFreshPeerPositionsAndCurrentRangeRatherThanUsingDialoguePositions()
    {
        using var world = new World(participants: 2);
        var game = world.Nodes[1].Game;
        SetInvitationDialogue(world, 1, true);
        game.BelayImpl.RopeCapacity = 10;
        game.PanelImpl.Act(BelayPanelAction.Request, 2);
        SetInvitationDialogue(world, 1, false);
        var positions = new Dictionary<int, PlayerLocation> { [1] = world.Locations[1] };
        game.InteractionLocations = positions;
        Advance(world, .51f);
        Assert.Empty(world.Messages);
        Assert.Equal(0, game.BelayImpl.ReadinessChecks);
        positions[2] = new(50, 0, 0, "Wall", PlayerState.InGame);
        Advance(world, .51f);
        Assert.Empty(world.Messages);
        positions[2] = new(2, 0, 0, "OtherWorld", PlayerState.InGame);
        Advance(world, .51f);
        Assert.Empty(world.Messages);
        positions[2] = new(2, 0, 0, "Wall", PlayerState.InGame);
        Advance(world, .51f);
        var offered = Assert.Single(world.Messages, m => m.Phase == ManualBelayPhase.Offered);
        Assert.Equal(1, offered.Climber); Assert.Equal(2, offered.Belayer);
        Assert.Equal(10, offered.RopeCapacity);
    }

    [Fact]
    public void AHostRejectionDoesNotRestartTheConsumedQueue()
    {
        using var world = new World(participants: 2);
        var game = world.Nodes[2].Game;
        SetInvitationDialogue(world, 2, true);
        game.PanelImpl.Act(BelayPanelAction.Offer, 1);
        world.Nodes[1].Game.BelayImpl.KnownStation = false;
        SetInvitationDialogue(world, 2, false);
        Advance(world, .51f);
        Assert.Single(world.SentBelayCommands);
        Assert.Empty(world.Messages);
        Assert.Equal(1, game.BelayImpl.StationDefineCalls);
        for (var i = 0; i < 10; i++) Advance(world, .6f);
        Assert.Single(world.SentBelayCommands);
        Assert.Equal(1, game.BelayImpl.StationDefineCalls);
        Assert.DoesNotContain("manual-belay.belay-invitation-queue", game.HudImpl.Messages.Keys);
    }

    [Fact]
    public void AnIncomingNetworkOfferReplacesTheQueueButIsNeverAutomaticallyAccepted()
    {
        using var world = new World(participants: 2);
        var game = world.Nodes[1].Game;
        SetInvitationDialogue(world, 1, true);
        game.PanelImpl.Act(BelayPanelAction.Request, 2);
        // The host can receive an offer before its last network presence catches
        // up with the local dialogue. The local raw context is still unsafe.
        world.Locations[1] = new(0, 0, 0, "Wall", PlayerState.InGame);
        world.Nodes[2].Game.ChatImpl.Run("offer 1");
        Assert.DoesNotContain("manual-belay.belay-invitation-queue", game.HudImpl.Messages.Keys);
        game.ChatImpl.Run("accept");
        Assert.Single(world.SentBelayCommands);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Started);
        Assert.Equal(0, game.BelayImpl.BeginCalls);
        SetInvitationDialogue(world, 1, false);
        Advance(world, .6f);
        Assert.Single(world.SentBelayCommands);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Started);
    }

    [Fact]
    public void FocusAndPauseSuspendTheQueuedDispatchWithoutDroppingIt()
    {
        using var world = new World(participants: 2);
        var game = world.Nodes[1].Game;
        SetInvitationDialogue(world, 1, true); game.PanelImpl.Act(BelayPanelAction.Request, 2);
        SetInvitationDialogue(world, 1, false); game.Keys.Focused = false;
        Advance(world, .6f); Assert.Empty(world.Messages);
        game.Keys.Focused = true; game.Keys.Paused = true;
        Advance(world, .6f); Assert.Empty(world.Messages);
        AssertNoInvitationNativeReads(game);
        game.Keys.Paused = false;
        Advance(world, .6f); Assert.Single(world.Messages, m => m.Phase == ManualBelayPhase.Offered);
    }

    [Fact]
    public void ARelayHostsLocalQueueDoesNotSuspendTheOtherPairsHostArbitration()
    {
        using var world = ConfirmedRecoveryPair(2, 3);
        var host = world.Nodes[1].Game;
        var invitations = world.Messages.Count(m => m.Phase == ManualBelayPhase.Offered);
        SetInvitationDialogue(world, 1, true);
        host.PanelImpl.Act(BelayPanelAction.Request, 2);
        Assert.Contains("manual-belay.belay-invitation-queue", host.HudImpl.Messages.Keys);
        SetInvitationDialogue(world, 1, false);
        host.BelayImpl.KnownStation = false;
        Advance(world, .11f);
        Assert.DoesNotContain("manual-belay.belay-invitation-queue", host.HudImpl.Messages.Keys);
        Assert.Equal(invitations, world.Messages.Count(m => m.Phase == ManualBelayPhase.Offered));
        Assert.Contains(world.Messages, m => m.Phase == ManualBelayPhase.Interrupted);
    }
}
