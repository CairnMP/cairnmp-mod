using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayExerciseInputTests
{
    [Fact]
    public void StandInTakesInWithoutHoldingAnArrowAndExpires()
    {
        var input = new BelayExerciseInput();
        Assert.Equal(0, input.Resolve(true, false, false, false, 0));
        Assert.Equal(-1, input.Resolve(true, true, false, true, 1));
        Assert.Equal(-1, input.Resolve(true, false, false, false, 10));
        Assert.Equal(11, input.RemainingSeconds(10));
        Assert.Equal(0, input.Resolve(true, false, false, false, 21));
        Assert.False(input.TakingIn);
        Assert.Equal(0, input.Resolve(true, false, false, false, 22));
    }

    [Fact]
    public void ChordReleaseOrderDoesNotCancelOrRestartTheCommand()
    {
        var input = new BelayExerciseInput();
        Assert.Equal(-1, input.Resolve(true, true, false, true, 1));
        Assert.Equal(-1, input.Resolve(true, false, false, true, 1.1));
        Assert.Equal(-1, input.Resolve(true, false, false, false, 1.2));
        Assert.Equal(0, input.Resolve(true, true, false, true, 2));
        Assert.Equal(0, input.Resolve(true, false, false, true, 2.1));
        Assert.Equal(0, input.Resolve(true, false, false, false, 2.2));
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(false, true, -1)]
    [InlineData(true, true, 0)]
    public void ManualArrowsCancelTheStandIn(bool feed, bool takeIn, int motion)
    {
        var input = new BelayExerciseInput();
        input.Resolve(true, true, false, true, 1);
        input.Resolve(true, false, false, false, 1.1);
        Assert.Equal(motion, input.Resolve(true, false, feed, takeIn, 2));
        Assert.False(input.TakingIn);
        Assert.Equal(0, input.Resolve(true, false, false, false, 2.1));
    }

    [Fact]
    public void LosingEligibilityCancelsInputAndItDoesNotResumeOnReturn()
    {
        var input = new BelayExerciseInput();
        input.Resolve(true, true, false, true, 1);
        Assert.Equal(0, input.Resolve(false, false, false, false, 2));
        Assert.False(input.TakingIn);
        Assert.Equal(0, input.Resolve(true, false, false, false, 3));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    [InlineData(.5d)]
    public void InvalidOrReversedTimeCancelsTheStandIn(double now)
    {
        var input = new BelayExerciseInput();
        input.Resolve(true, true, false, true, 1);
        Assert.Equal(0, input.Resolve(true, false, false, false, now));
        Assert.False(input.TakingIn);
        Assert.Equal(0, input.Resolve(true, false, false, false, 3));
    }

    [Fact]
    public void HoldingTheChordPastItsDeadlineDoesNotBecomeAnUnlimitedManualCommand()
    {
        var input = new BelayExerciseInput();
        input.Resolve(true, true, false, true, 1);
        Assert.Equal(0, input.Resolve(true, false, false, true, 21));
        Assert.Equal(0, input.Resolve(true, false, false, true, 22));
        Assert.False(input.TakingIn);
        input.Resolve(true, false, false, false, 23);
        Assert.Equal(-1, input.Resolve(true, false, false, true, 24));
    }
}
