using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayRopeControlTests
{
    [Fact]
    public void BrakePreservesExistingSlackAndReleaseDoesNotSpendTheWholeReserve()
    {
        var rope = new BelayRopeControl(40, 8, 5);
        rope.SetBrake(true);
        Assert.True(rope.TryStep(6, 6, .02f));
        Assert.Equal(8, rope.Released);
        Assert.Equal(2, rope.Slack);
        Assert.Equal(32, rope.Reserve);
        rope.SetBrake(false);
        Assert.True(rope.TryStep(6, 6, .02f));
        Assert.Equal(8, rope.Released);
        Assert.True(rope.TryStep(12, 12, .02f));
        Assert.Equal(12.03f, rope.Released, 4);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void FeedAndTakeInUseElapsedTimeAtDifferentFrameRates(int rate)
    {
        var rope = new BelayRopeControl(40, 8, 5);
        Assert.True(rope.TrySetMotion(1));
        for (var i = 0; i < 3 * rate; i++) Assert.True(rope.TryStep(5, 5, 1f / rate));
        Assert.Equal(11f, rope.Released, 3);
        Assert.True(rope.TrySetMotion(-1));
        for (var i = 0; i < 2 * rate; i++) Assert.True(rope.TryStep(5, 5, 1f / rate));
        Assert.Equal(9f, rope.Released, 3);
    }

    [Fact]
    public void TakeInRemovesSlackDuringAscentWithoutPullingTheClimber()
    {
        var rope = new BelayRopeControl(40, 10, 8);
        rope.SetBrake(true);
        rope.TrySetMotion(-1);
        for (var i = 0; i < 100; i++) rope.TryStep(8, 8, .1f);
        Assert.Equal(8.03f, rope.Released, 4);
        for (var i = 0; i < 100; i++) rope.TryStep(5, 5, .1f);
        Assert.Equal(5.03f, rope.Released, 4);
        // A loaded native suspension can require more than the straight rope
        // measurement. That independent lower bound prevents accidental hauling.
        rope = new BelayRopeControl(40, 10, 5);
        rope.TrySetMotion(-1);
        for (var i = 0; i < 100; i++) rope.TryStep(5, 9, .1f);
        Assert.Equal(9.03f, rope.Released, 4);
    }

    [Fact]
    public void BrakeKeepsReleasedLengthAcrossAscentAndRepeatedFalls()
    {
        var rope = new BelayRopeControl(40, 8, 5);
        rope.SetBrake(true);
        foreach (var distance in new[] { 6f, 8.1f, 3f, 7f, 8.2f })
        {
            Assert.True(rope.TryStep(distance, distance, .02f));
            Assert.Equal(8, rope.Released);
        }
        rope.TrySetMotion(-1);
        Assert.True(rope.TryStep(8.2f, 8.2f, .1f));
        Assert.Equal(8, rope.Released); // Stretch must not rewrite the brake target.
    }

    [Fact]
    public void RopeEndCapsOpenPayoutFeedAndTakeInDuringStretch()
    {
        var rope = new BelayRopeControl(10, 8, 5);
        rope.TrySetMotion(1);
        for (var i = 0; i < 100; i++) rope.TryStep(5, 5, .1f);
        Assert.Equal(10, rope.Released);
        rope.TrySetMotion(-1);
        Assert.True(rope.TryStep(10.5f, 10.5f, .1f));
        Assert.Equal(10, rope.Released);
        Assert.Equal(0, rope.Reserve);
        Assert.Equal(0, rope.Slack);
    }

    [Fact]
    public void BrakeBlocksFeedAndLongStallsDoNotCauseLargeLengthJumps()
    {
        var rope = new BelayRopeControl(40, 8, 5);
        rope.TrySetMotion(1);
        rope.SetBrake(true);
        Assert.True(rope.TryStep(5, 5, 20));
        Assert.Equal(8, rope.Released);
        rope.SetBrake(false);
        Assert.True(rope.TryStep(5, 5, 20));
        Assert.Equal(8.1f, rope.Released, 4);
        Assert.False(rope.TrySetMotion(2));
        rope.TrySetMotion(0);
        Assert.True(rope.TryStep(5, 5, .1f));
        Assert.Equal(8.1f, rope.Released, 4);
    }

    [Theory]
    [InlineData(float.NaN, 5f, .1f)]
    [InlineData(5f, float.PositiveInfinity, .1f)]
    [InlineData(5f, 5f, -1f)]
    [InlineData(5f, 5f, float.NaN)]
    [InlineData(0f, 5f, .1f)]
    public void InvalidMeasurementDoesNotMutateTheBudget(float distance, float minimum, float elapsed)
    {
        var rope = new BelayRopeControl(40, 8, 5);
        rope.TrySetMotion(-1);
        Assert.False(rope.TryStep(distance, minimum, elapsed));
        Assert.Equal(8, rope.Released);
        Assert.Equal(5, rope.Distance);
    }

    [Fact]
    public void SuspensionTranslationPreservesSlackAndTheRemainingRopeEndBudget()
    {
        var rope = new BelayRopeControl(40, 10, 8);
        rope.SetBrake(true);
        Assert.True(rope.TryAttachSuspension(5, out var target));
        Assert.Equal(7, target); // Two metres of slack, not a catch at current distance.
        rope.SetBrake(false);
        Assert.True(rope.TryGetSuspensionTarget(out target));
        Assert.Equal(37, target); // Thirty-two metres remain beyond the current body.
        Assert.Equal(10, rope.Released);
    }

    [Fact]
    public void UpstreamGeometryChangesDoNotResetPaidOutLengthBrakeOrHeldCommand()
    {
        var rope = new BelayRopeControl(36.61846f, 6.346119f, 1.226f);
        rope.SetBrake(true);
        rope.TrySetMotion(-1);
        foreach (var capacity in new[] { 36.55f, 36.7f, 36.45f, 36.8f })
        {
            Assert.True(rope.TryObserveCapacity(capacity));
            Assert.Equal(6.346119f, rope.Released);
            Assert.True(rope.Braked);
            Assert.Equal(-1, rope.Motion);
        }
        Assert.True(rope.TryStep(1.226f, 1.226f, .1f));
        Assert.Equal(6.246119f, rope.Released, 4);
        Assert.Equal(36.8f - rope.Released, rope.Reserve, 4);
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0f)]
    [InlineData(201f)]
    public void InvalidCapacityOrInsufficientRopeDoesNotClampAnExistingTarget(float capacity)
    {
        var rope = new BelayRopeControl(40, 8, 5);
        Assert.False(rope.TryObserveCapacity(capacity));
        Assert.Equal(40, rope.Capacity);
        Assert.Equal(8, rope.Released);
    }

    [Fact]
    public void LoadedCapacityIsFrozenUntilTheSuspensionEnds()
    {
        var rope = new BelayRopeControl(40, 8, 5);
        Assert.True(rope.TryAttachSuspension(2, out var target));
        Assert.Equal(37, target);
        Assert.True(rope.TryObserveCapacity(39));
        Assert.Equal(40, rope.Capacity);
        Assert.True(rope.TryGetSuspensionTarget(out target));
        Assert.Equal(37, target);
        rope.EndSuspension();
        Assert.True(rope.TryObserveCapacity(39));
        Assert.Equal(39, rope.Capacity);
        Assert.Equal(8, rope.Released);
    }

    [Fact]
    public void NewSuspensionUsesItsOwnOriginWithoutResettingPaidOutRope()
    {
        var rope = new BelayRopeControl(40, 8, 5);
        rope.SetBrake(true);
        Assert.True(rope.TryAttachSuspension(2, out var target));
        Assert.Equal(5, target);
        Assert.False(rope.TryAttachSuspension(3, out _));
        rope.EndSuspension();
        Assert.False(rope.TryGetSuspensionTarget(out _));
        Assert.True(rope.TryStep(3, 3, .02f));
        Assert.True(rope.TryAttachSuspension(1, out target));
        Assert.Equal(6, target);
        Assert.Equal(8, rope.Released);
        Assert.Equal(5, rope.Slack);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0f)]
    [InlineData(198f)]
    public void InvalidSuspensionTranslationDoesNotAcquireOrAlterTheBudget(float real)
    {
        var rope = new BelayRopeControl(40, 8, 5);
        Assert.False(rope.TryAttachSuspension(real, out _));
        Assert.False(rope.Suspended);
        Assert.Equal(8, rope.Released);
        Assert.True(rope.TryAttachSuspension(5, out _));
    }

    [Theory]
    [InlineData(0f, 1f, 1f)]
    [InlineData(201f, 1f, 1f)]
    [InlineData(40f, float.NaN, 1f)]
    [InlineData(40f, 41f, 1f)]
    [InlineData(40f, 1f, 41f)]
    public void InvalidInitialBudgetIsRejected(float capacity, float released, float distance)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new BelayRopeControl(capacity, released, distance));
}
