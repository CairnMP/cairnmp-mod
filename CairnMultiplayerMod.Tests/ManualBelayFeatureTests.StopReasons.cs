using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.GameApi;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed partial class ManualBelayFeatureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InputSentBeforeRolePreparationIsResampledWithoutEndingThePair(bool cancelBeforeResponse)
    {
        using var world = new World();
        world.Nodes[2].Game.ChatImpl.Run("offer 1");
        world.Nodes[1].Game.ChatImpl.Run("accept"); world.Tick();
        var original = world.Last(ManualBelayPhase.Started);
        var belayer = world.Nodes[2].Game;
        belayer.BelayImpl.RolePreparationReady = false;

        // Keep an ordinary FREE/feed command in flight while the host accepts
        // the exchange. It must be rejected, without replaying the old feed.
        world.DeferCommands = true;
        belayer.Keys.Up = true;
        world.Time += .5f; world.Tick();
        var pending = Assert.Single(world.Pending); world.Pending.Clear();
        using var reader = new BinaryReader(new MemoryStream(pending.Packet.Payload));
        var stale = new ManualBelayCommand(); stale.Deserialize(reader);
        Assert.Equal(ManualBelayOperation.Intent, stale.Operation);
        Assert.False(stale.Braked); Assert.Equal(1, stale.Motion);
        belayer.Keys.Up = false;
        world.DeferCommands = false;

        ProposeSwap(world, 1);
        belayer.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        var rejected = world.Nodes[1].Runtime.ExecuteHostCommand(pending.Sender, pending.Packet);
        Assert.False(rejected.Committed);
        Assert.Contains("during role preparation", rejected.Reason);
        if (cancelBeforeResponse) belayer.PanelImpl.Act(BelayPanelAction.CancelSwap);
        world.Nodes[2].Runtime.CompleteCommand(new ServerExtensionCommandResult
        {
            RequestId = pending.Packet.RequestId,
            Status = (ExtensionCommandStatus)rejected.Status,
            Reason = rejected.Reason,
        });
        world.Time += .11f; world.Tick();

        Assert.DoesNotContain(world.Messages, message => message.Phase == ManualBelayPhase.Ended);
        Assert.True(world.Nodes[1].Game.BelayImpl.Active);
        Assert.Equal(original.Session, belayer.BelayImpl.ProtectedSession);
        var fresh = world.Last(ManualBelayPhase.Intent);
        Assert.True(fresh.Sequence > stale.Sequence);
        Assert.True(fresh.Braked); Assert.Equal(0, fresh.Motion);
        Assert.True(world.Nodes[1].Game.BelayImpl.LastBrake);
        Assert.Equal(0, world.Nodes[1].Game.BelayImpl.Rope.Motion);
        Assert.Equal(cancelBeforeResponse ? BelayPanelPhase.Active : BelayPanelPhase.SwapPreparing, belayer.PanelImpl.View.Phase);
        Assert.Empty(world.Failures);
    }
}
