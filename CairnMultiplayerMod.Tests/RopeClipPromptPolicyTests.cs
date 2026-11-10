using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class RopeClipPromptPolicyTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void TheTipStaysHiddenUntilAClipInWouldSucceed(
        bool alreadyLinked, bool hasTargetInRange, bool inputAvailable)
    {
        Assert.False(RopeClipPromptPolicy.ShouldShow(
            alreadyLinked, hasTargetInRange, inputAvailable));
    }

    [Fact]
    public void TheTipShowsWhenThePressWouldClip()
    {
        Assert.True(RopeClipPromptPolicy.ShouldShow(
            alreadyLinked: false,
            hasTargetInRange: true,
            inputAvailable: true));
    }

    [Fact]
    public void TheTipStaysHiddenWhileTheStaffHoldsThePitchForTheFollower()
    {
        Assert.False(RopeClipPromptPolicy.ShouldShow(
            alreadyLinked: false,
            hasTargetInRange: true,
            inputAvailable: true,
            pitchHeldForFollower: true));
    }
}
