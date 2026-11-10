using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class RopeCutPromptPolicyTests
{
    [Theory]
    [InlineData(false, true, 0.5f, true)]
    [InlineData(true, false, 0.5f, true)]
    [InlineData(true, true, 1.01f, true)]
    [InlineData(true, true, 0.5f, false)]
    [InlineData(false, false, 5f, false)]
    public void TheTipStaysHiddenUntilTheLastSecond(
        bool gripDraining, bool criticalGrip, float secondsRemaining, bool inputAvailable)
    {
        Assert.False(RopeCutPromptPolicy.ShouldShow(
            gripDraining, criticalGrip, secondsRemaining, inputAvailable));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.4f)]
    [InlineData(0f)]
    public void TheTipShowsInTheLastSecond(float secondsRemaining)
    {
        Assert.Equal(1f, RopeCutPromptPolicy.LeadSeconds);
        Assert.True(RopeCutPromptPolicy.ShouldShow(
            gripDraining: true, criticalGrip: true, secondsRemaining, inputAvailable: true));
    }

    [Theory]
    [InlineData(0.4f, 0f)]
    [InlineData(0.4f, 0.2f)]
    [InlineData(0.2f, 0f)]
    public void AWindowShorterThanASecondShowsTheWholeTime(float window, float elapsed)
    {
        Assert.True(window < RopeCutPromptPolicy.LeadSeconds);
        Assert.True(RopeCutPromptPolicy.ShouldShow(
            gripDraining: true,
            criticalGrip: true,
            secondsRemaining: window - elapsed,
            inputAvailable: true));
    }
}
