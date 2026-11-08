using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class RopeCutPromptPolicyTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void TheTipStaysHiddenUntilTheLeaderIsLosingGrip(bool gripDraining, bool inputAvailable)
    {
        Assert.False(RopeCutPromptPolicy.ShouldShow(gripDraining, inputAvailable));
    }

    [Fact]
    public void TheTipShowsWhileTheLeadersGripIsEmptying()
    {
        Assert.True(RopeCutPromptPolicy.ShouldShow(gripDraining: true, inputAvailable: true));
    }
}
