using CairnMultiplayerMod.Internal.Game.World;
using Xunit;

namespace CairnMultiplayerMod.Tests;

/// <summary>
/// Teleporting a pawn that is not walking is what makes "bring a player to me" unstable:
/// climbing holds live references to holds and rope constraints, falling and dying run their
/// own recovery. The rule guards the pawn being MOVED, whichever command started the move.
/// </summary>
public sealed class TeleportPolicyTests
{
    [Fact]
    public void OnlyAGroundedWalkAllowsATeleport()
        => Assert.True(TeleportPolicy.AllowsTeleport("Walking"));

    [Theory]
    [InlineData("Climbing")]
    [InlineData("Falling")]
    [InlineData("Dead")]
    [InlineData("Invalid")]
    [InlineData("Crawling")]
    [InlineData("")]
    [InlineData(null)]
    public void EveryOtherPawnStateIsRefused(string state)
        => Assert.False(TeleportPolicy.AllowsTeleport(state));

    [Fact]
    public void WalkingIsMatchedExactly()
    {
        Assert.False(TeleportPolicy.AllowsTeleport("walking"));
        Assert.False(TeleportPolicy.AllowsTeleport("Walking "));
        Assert.False(TeleportPolicy.AllowsTeleport("WalkingOnWall"));
    }

    [Fact]
    public void RefusalNamesTheStateSoThePlayerKnowsWhatToChange()
    {
        Assert.Contains("Climbing", TeleportPolicy.DescribeRefusal("Climbing"));
        Assert.Contains("walking", TeleportPolicy.DescribeRefusal("Climbing"));
    }

    [Theory]
    [InlineData("Dead", "dead")]
    [InlineData("Invalid", "not ready")]
    [InlineData("", "not ready")]
    [InlineData(null, "not ready")]
    public void KnownStatesGetTheirOwnWording(string state, string expected)
        => Assert.Contains(expected, TeleportPolicy.DescribeRefusal(state));

    [Fact]
    public void RefusalIsAlwaysSomethingWeCanShow()
    {
        foreach (var state in new[] { "Walking", "Climbing", "Dead", "Invalid", "", null, "Anything" })
            Assert.False(string.IsNullOrWhiteSpace(TeleportPolicy.DescribeRefusal(state)));
    }
}
