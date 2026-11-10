using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class ClimbotRecallTests
{
    [Fact]
    public void ABotThatIsWaitingIsCalledAndTheRopeStaysUnpinned()
    {
        var step = ClimbotRecall.Decide(outThere: true, arrivedHome: false, asks: 0, secondsSinceAsk: 0f);

        Assert.True(step.Call);
        Assert.True(step.HoldRope);
        Assert.Equal(1, step.Asks);
    }

    [Fact]
    public void ASecondAskWaitsUntilTheFirstHasHadTime()
    {
        var soon = ClimbotRecall.Decide(
            outThere: true, arrivedHome: false, asks: 1, secondsSinceAsk: ClimbotRecall.RetryAfterSeconds - 0.01f);

        Assert.False(soon.Call);
        Assert.True(soon.HoldRope);
        Assert.Equal(1, soon.Asks);
    }

    [Fact]
    public void AnIgnoredCallIsAskedOnceMore()
    {
        var step = ClimbotRecall.Decide(
            outThere: true, arrivedHome: false, asks: 1, secondsSinceAsk: ClimbotRecall.RetryAfterSeconds);

        Assert.True(step.Call);
        Assert.True(step.HoldRope);
        Assert.Equal(2, step.Asks);
    }

    [Fact]
    public void AThirdAskDoesNotCome()
    {
        var step = ClimbotRecall.Decide(outThere: true, arrivedHome: false, asks: 2, secondsSinceAsk: 10f);

        Assert.False(step.Call);
        Assert.True(step.HoldRope);
        Assert.Equal(2, step.Asks);
    }

    [Fact]
    public void ABotOnTheWayHomeIsNotAskedAgain()
    {
        var step = ClimbotRecall.Decide(outThere: false, arrivedHome: false, asks: 1, secondsSinceAsk: 10f);

        Assert.False(step.Call);
        Assert.True(step.HoldRope);
        Assert.Equal(1, step.Asks);
    }

    [Fact]
    public void ABotThatIsHomeLetsTheRopeBePinned()
    {
        var already = ClimbotRecall.Decide(outThere: false, arrivedHome: true, asks: 0, secondsSinceAsk: 0f);
        var arrived = ClimbotRecall.Decide(outThere: false, arrivedHome: true, asks: 2, secondsSinceAsk: 3f);

        Assert.False(already.Call);
        Assert.False(already.HoldRope);
        Assert.Equal(0, already.Asks);
        Assert.False(arrived.Call);
        Assert.False(arrived.HoldRope);
        Assert.Equal(0, arrived.Asks);
    }
}
