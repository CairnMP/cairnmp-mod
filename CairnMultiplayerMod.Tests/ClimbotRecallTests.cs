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
        Assert.False(step.Stop);
        Assert.True(step.HoldRope);
        Assert.Equal(1, step.Asks);
    }

    [Fact]
    public void ABotThatIsHomeLetsTheRopeBePinned()
    {
        var already = ClimbotRecall.Decide(outThere: false, arrivedHome: true, asks: 0, secondsSinceAsk: 0f);
        var arrived = ClimbotRecall.Decide(outThere: false, arrivedHome: true, asks: 2, secondsSinceAsk: 3f);
        var readyWhileMarkedBusy = ClimbotRecall.Decide(
            outThere: false, arrivedHome: true, asks: 0, secondsSinceAsk: 0f, stuckBusy: true);

        Assert.False(already.Call);
        Assert.False(already.Stop);
        Assert.False(already.HoldRope);
        Assert.Equal(0, already.Asks);
        Assert.False(arrived.Call);
        Assert.False(arrived.Stop);
        Assert.False(arrived.HoldRope);
        Assert.Equal(0, arrived.Asks);
        Assert.False(readyWhileMarkedBusy.Call);
        Assert.False(readyWhileMarkedBusy.Stop);
        Assert.False(readyWhileMarkedBusy.HoldRope);
        Assert.Equal(0, readyWhileMarkedBusy.Asks);
    }

    [Fact]
    public void ABusyBotIsToldToStopAndTheRopeStaysUnpinned()
    {
        var step = ClimbotRecall.Decide(
            outThere: false, arrivedHome: false, asks: 0, secondsSinceAsk: 0f, stuckBusy: true);

        Assert.False(step.Call);
        Assert.True(step.Stop);
        Assert.True(step.HoldRope);
        Assert.Equal(1, step.Asks);
    }

    [Fact]
    public void ASecondStopWaitsUntilTheFirstHasHadTime()
    {
        var soon = ClimbotRecall.Decide(
            outThere: false,
            arrivedHome: false,
            asks: 1,
            secondsSinceAsk: ClimbotRecall.RetryAfterSeconds - 0.01f,
            stuckBusy: true);

        Assert.False(soon.Call);
        Assert.False(soon.Stop);
        Assert.True(soon.HoldRope);
        Assert.Equal(1, soon.Asks);
    }

    [Fact]
    public void AnIgnoredStopIsAskedOnceMore()
    {
        var step = ClimbotRecall.Decide(
            outThere: false,
            arrivedHome: false,
            asks: 1,
            secondsSinceAsk: ClimbotRecall.RetryAfterSeconds,
            stuckBusy: true);

        Assert.False(step.Call);
        Assert.True(step.Stop);
        Assert.True(step.HoldRope);
        Assert.Equal(2, step.Asks);
    }

    [Fact]
    public void AThirdStopDoesNotCome()
    {
        var step = ClimbotRecall.Decide(
            outThere: false, arrivedHome: false, asks: 2, secondsSinceAsk: 10f, stuckBusy: true);

        Assert.False(step.Call);
        Assert.False(step.Stop);
        Assert.True(step.HoldRope);
        Assert.Equal(2, step.Asks);
    }

    [Fact]
    public void ABotWaitingForTheWhistleIsNotToldToStop()
    {
        var step = ClimbotRecall.Decide(
            outThere: true, arrivedHome: false, asks: 0, secondsSinceAsk: 0f, stuckBusy: true);

        Assert.True(step.Call);
        Assert.False(step.Stop);
        Assert.True(step.HoldRope);
    }

    [Fact]
    public void ALiveLeadAndAFollowerCatchAreNotAStuckBelay()
    {
        Assert.True(ClimbotRecall.IsStuckBelay(
            reportedBusy: true, clippedWhileReady: false, whistleInFlight: false, keepsFollowerCatch: false));
        Assert.False(ClimbotRecall.IsStuckBelay(
            reportedBusy: true, clippedWhileReady: true, whistleInFlight: false, keepsFollowerCatch: false));
        Assert.False(ClimbotRecall.IsStuckBelay(
            reportedBusy: true, clippedWhileReady: false, whistleInFlight: true, keepsFollowerCatch: false));
        Assert.False(ClimbotRecall.IsStuckBelay(
            reportedBusy: true, clippedWhileReady: false, whistleInFlight: false, keepsFollowerCatch: true));
        Assert.False(ClimbotRecall.IsStuckBelay(
            reportedBusy: false, clippedWhileReady: false, whistleInFlight: false, keepsFollowerCatch: false));
    }

}
