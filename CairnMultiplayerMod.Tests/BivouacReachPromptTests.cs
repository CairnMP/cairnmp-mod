using CairnMultiplayerMod.Features;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BivouacReachPromptTests
{
    [Fact]
    public void TheWalkShowsOnlyWhileARopeTeamPartnerIsDownAndCampIsStillAhead()
    {
        Assert.True(BivouacReachPrompt.ShouldShow(
            bivouacRevive: true, teammateRevive: false, connected: true,
            localAlive: true, partnerDown: true, atCamp: false));
    }

    [Theory]
    [InlineData(false, false, true, true, true, false)]
    [InlineData(true, true, true, true, true, false)]
    [InlineData(true, false, false, true, true, false)]
    [InlineData(true, false, true, false, true, false)]
    [InlineData(true, false, true, true, false, false)]
    [InlineData(true, false, true, true, true, true)]
    public void TheWalkStaysHiddenOutsideThatCase(
        bool bivouacRevive, bool teammateRevive, bool connected,
        bool localAlive, bool partnerDown, bool atCamp)
    {
        Assert.False(BivouacReachPrompt.ShouldShow(
            bivouacRevive, teammateRevive, connected, localAlive, partnerDown, atCamp));
    }
}
