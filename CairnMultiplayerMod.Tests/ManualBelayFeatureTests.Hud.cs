using System.Collections.Generic;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.GameApi;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed partial class ManualBelayFeatureTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void AConfirmedPairKeepsItsOwnRoleAndPartnerVisibleWhileThePanelIsClosed(int belayer, int climber)
    {
        using var world = new World();
        world.Nodes[belayer].Game.ChatImpl.Run("offer " + climber);
        world.Nodes[climber].Game.ChatImpl.Run("accept");
        world.Tick();
        Advance(world, .6f);
        AssertRole(world, belayer, "SEGURADOR / BELAYER", "P" + climber);
        AssertRole(world, climber, "ESCALADOR / CLIMBER", "P" + belayer);
        Assert.False(world.Nodes[belayer].Game.PanelImpl.IsOpen);
        Assert.False(world.Nodes[climber].Game.PanelImpl.IsOpen);
        Assert.True(world.Nodes[climber].Game.BelayImpl.Active);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void AttachmentConsentShowsConnectingUntilTheNativeControllerAndReadbackAreReady()
    {
        using var world = new World();
        var climber = world.Nodes[2].Game.BelayImpl;
        climber.NeedsAttachment = true;
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        world.Nodes[2].Game.ChatImpl.Run("accept");
        world.Tick();
        Advance(world, .6f);
        foreach (var id in new[] { 1, 2 })
        {
            var text = world.Nodes[id].Game.HudImpl.Messages["manual-belay.belay-role"];
            Assert.Contains("CONECTANDO / CONNECTING", text);
            Assert.Contains("PARCEIRO / PARTNER: P" + (id == 1 ? 2 : 1), text);
            Assert.DoesNotContain("VOCÊ / YOU:", text);
            Assert.DoesNotContain("SEGURADOR", text);
            Assert.DoesNotContain("ESCALADOR", text);
        }
        Assert.Null(climber.Rope);
        climber.NativeAttached = true;
        for (var i = 0; i < 55; i++) Advance(world, .11f);
        Assert.NotNull(climber.Rope);
        AssertRole(world, 1, "SEGURADOR / BELAYER", "P2");
        AssertRole(world, 2, "ESCALADOR / CLIMBER", "P1");
    }

    [Fact]
    public void ACancelledProposalOrPreparationNeverAnnouncesTheProposedRoles()
    {
        using var world = World.Started();
        Advance(world, .6f);
        world.Nodes[1].Game.BelayImpl.RolePreparationReady = false;
        ProposeSwap(world);
        AssertRole(world, 1, "SEGURADOR / BELAYER", "P2");
        AssertRole(world, 2, "ESCALADOR / CLIMBER", "P1");
        world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        Advance(world, .6f);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCommitted);
        AssertRole(world, 1, "SEGURADOR / BELAYER", "P2");
        AssertRole(world, 2, "ESCALADOR / CLIMBER", "P1");
        world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.CancelSwap);
        Advance(world, .6f);
        AssertRole(world, 1, "SEGURADOR / BELAYER", "P2");
        AssertRole(world, 2, "ESCALADOR / CLIMBER", "P1");
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
    }

    [Fact]
    public void HostCommitUpdatesBothRoleRowsBeforeAnotherFrameOrTelemetryArrives()
    {
        using var world = World.Started();
        Advance(world, .6f);
        world.Nodes[1].Game.BelayImpl.RolePreparationReady = false;
        var offered = ProposeSwap(world);
        world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        world.Tick(); // Current climber ready; future climber still preparing.
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapCommitted);
        world.Nodes[1].Game.BelayImpl.RolePreparationReady = true;
        var result = world.Execute(1, new ManualBelayCommand
        {
            Operation = ManualBelayOperation.ReadySwap,
            Session = offered.Session, Swap = offered.Swap, Station = offered.Station
        });
        Assert.True(result.Committed);
        Assert.Equal(offered.Swap, world.Last(ManualBelayPhase.SwapCommitted).Swap);
        AssertRole(world, 1, "ESCALADOR / CLIMBER", "P2");
        AssertRole(world, 2, "SEGURADOR / BELAYER", "P1");
        Assert.True(world.Nodes[1].Game.BelayImpl.Active);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void StopSceneResetSessionEndParticipantLossAndModeExitRemoveTheRoleRow(int ending)
    {
        using var world = World.Started();
        Advance(world, .6f);
        var game = world.Nodes[2].Game;
        Assert.Contains("manual-belay.belay-role", game.HudImpl.Messages.Keys);
        if (ending == 0) game.ChatImpl.Run("stop");
        else if (ending == 1) world.Nodes[2].Host.NotifySceneReset();
        else if (ending == 2) world.Nodes[2].Host.NotifySessionEnded();
        else if (ending == 3) world.Nodes[1].Runtime.NotifyPlayerLeft(new MultiplayerPlayer(2, "P2", false, false));
        else { world.Mode = MultiplayerMode.RopeTeam; world.Tick(); }
        Assert.DoesNotContain("manual-belay.belay-role", game.HudImpl.Messages.Keys);
        Assert.DoesNotContain("manual-belay.belay", game.HudImpl.Messages.Keys);
        Assert.False(game.BelayImpl.Active);
    }

    private static void AssertRole(World world, int player, string role, string partner)
    {
        var text = world.Nodes[player].Game.HudImpl.Messages["manual-belay.belay-role"];
        Assert.Contains("VOCÊ / YOU: " + role, text);
        Assert.Contains("PARCEIRO / PARTNER: " + partner, text);
        Assert.DoesNotContain("CONNECTING", text);
    }

    private sealed class RoleHud : IGameHudApi
    {
        internal readonly Dictionary<string, string> Messages = new();
        public void ShowMessage(string id, string text, float durationSeconds) => Messages[id] = text;
        public void HideMessage(string id) => Messages.Remove(id);
        public void ShowStandings(string title, IReadOnlyList<StandingRow> rows) { }
        public void HideStandings() { }
    }
}
