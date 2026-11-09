using System.Linq;
using CairnMultiplayer.Shared;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed partial class ManualBelayFeatureTests
{
    [Theory]
    [InlineData(MultiplayerMode.RopeTeam)]
    [InlineData(MultiplayerMode.FreeSolo)]
    [InlineData(MultiplayerMode.Race)]
    public void IdleOtherModesDoNotRepeatBelayStationTeardown(MultiplayerMode mode)
    {
        using var world = new World { Mode = mode };
        var endCalls = world.Nodes.Values.ToDictionary(node => node, node => node.Game.BelayImpl.StationEndCalls);
        for (var i = 0; i < 20; i++) Advance(world, .1f);
        foreach (var node in world.Nodes.Values)
        {
            Assert.Equal(endCalls[node], node.Game.BelayImpl.StationEndCalls);
            Assert.Equal(0, node.Game.BelayImpl.BeginCalls);
            Assert.Equal(0, node.Game.BelayImpl.ReadinessChecks);
        }
        Assert.Empty(world.Messages);
        Assert.Empty(world.Failures);
    }
}
