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

    [Fact]
    public void EveryHarnessFlagCombinationHasOneOutcome()
    {
        var rows = new (bool whistle, bool busy, bool leads, bool left, bool follow, ClimbotRecall.Harness answer)[]
        {
            (false, false, false, false, false, ClimbotRecall.Harness.Hide),
            (false, false, false, false, true, ClimbotRecall.Harness.FollowerPin),
            (false, false, false, true, false, ClimbotRecall.Harness.LeaderLeftRelease),
            (false, false, false, true, true, ClimbotRecall.Harness.LeaderLeftRelease),
            (false, false, true, false, false, ClimbotRecall.Harness.LeaderPin),
            (false, false, true, false, true, ClimbotRecall.Harness.LeaderPin),
            (false, false, true, true, false, ClimbotRecall.Harness.LeaderPin),
            (false, false, true, true, true, ClimbotRecall.Harness.LeaderPin),
            (false, true, false, false, false, ClimbotRecall.Harness.StaleRelease),
            (false, true, false, false, true, ClimbotRecall.Harness.FollowerPin),
            (false, true, false, true, false, ClimbotRecall.Harness.LeaderLeftRelease),
            (false, true, false, true, true, ClimbotRecall.Harness.LeaderLeftRelease),
            (false, true, true, false, false, ClimbotRecall.Harness.LeaderRelease),
            (false, true, true, false, true, ClimbotRecall.Harness.LeaderRelease),
            (false, true, true, true, false, ClimbotRecall.Harness.LeaderRelease),
            (false, true, true, true, true, ClimbotRecall.Harness.LeaderRelease),
            (true, false, false, false, false, ClimbotRecall.Harness.Leave),
            (true, false, false, false, true, ClimbotRecall.Harness.FollowerPin),
            (true, false, false, true, false, ClimbotRecall.Harness.Leave),
            (true, false, false, true, true, ClimbotRecall.Harness.FollowerPin),
            (true, false, true, false, false, ClimbotRecall.Harness.Leave),
            (true, false, true, false, true, ClimbotRecall.Harness.FollowerPin),
            (true, false, true, true, false, ClimbotRecall.Harness.Leave),
            (true, false, true, true, true, ClimbotRecall.Harness.FollowerPin),
            (true, true, false, false, false, ClimbotRecall.Harness.Leave),
            (true, true, false, false, true, ClimbotRecall.Harness.FollowerPin),
            (true, true, false, true, false, ClimbotRecall.Harness.Leave),
            (true, true, false, true, true, ClimbotRecall.Harness.FollowerPin),
            (true, true, true, false, false, ClimbotRecall.Harness.Leave),
            (true, true, true, false, true, ClimbotRecall.Harness.FollowerPin),
            (true, true, true, true, false, ClimbotRecall.Harness.Leave),
            (true, true, true, true, true, ClimbotRecall.Harness.FollowerPin),
        };

        Assert.Equal(32, rows.Length);
        foreach (var row in rows)
        {
            Assert.Equal(
                row.answer,
                ClimbotRecall.ChooseHarness(row.whistle, row.busy, row.leads, row.left, row.follow));
        }
    }
}
