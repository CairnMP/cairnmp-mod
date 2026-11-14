using System.Numerics;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayClimbLimitTests
{
    [Theory]
    [InlineData(0f, 1f, false)]
    [InlineData(0f, -1f, true)]
    [InlineData(1f, 0f, false)]
    [InlineData(0f, 0f, true)]
    public void TautBrakeAbovePitonStopsFurtherProgressButAllowsRetreat(float x, float y, bool expected)
    {
        Assert.True(BelayClimbLimit.TryRestrict(true, true, 2.03f,
            new Vector3(0, 2, 0), new Vector3(x, y, 0), out var allowed));
        Assert.Equal(expected, allowed);
    }

    [Fact]
    public void BelowUpperPitonTheClimberCanAscendAndTakeInWithTheBrakeClosed()
    {
        Assert.True(BelayClimbLimit.TryRestrict(true, true, 2.03f,
            new Vector3(0, -2, 0), Vector3.UnitY, out var allowed));
        Assert.True(allowed);
        Assert.True(BelayClimbLimit.TryRestrict(true, true, 2.03f,
            new Vector3(0, -2, 0), -Vector3.UnitY, out allowed));
        Assert.False(allowed);
    }

    [Fact]
    public void ExistingSlackIsConsumedBeforeTheClimberNeedsMoreRope()
    {
        var rope = new BelayRopeControl(40, 3, 2);
        rope.SetBrake(true);
        Assert.True(BelayClimbLimit.TryRestrict(true, rope.Braked, rope.Released,
            new Vector3(0, 2, 0), Vector3.UnitY, out var allowed));
        Assert.True(allowed);
        Assert.True(BelayClimbLimit.TryRestrict(true, rope.Braked, rope.Released,
            new Vector3(0, 3, 0), Vector3.UnitY, out allowed));
        Assert.False(allowed);
        rope.SetBrake(false);
        rope.TrySetMotion(1);
        for (var i = 0; i < 10; i++) Assert.True(rope.TryStep(3, 3, .1f));
        rope.SetBrake(true);
        Assert.True(BelayClimbLimit.TryRestrict(true, rope.Braked, rope.Released,
            new Vector3(0, 3, 0), Vector3.UnitY, out allowed));
        Assert.True(allowed);
        Assert.Equal(40, rope.Capacity); // Feeding changes the budget, not the coil.
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void NativeDenialsAreNeverLoosenedAndAnOpenBrakeAddsNoRestriction(bool native, bool braked)
    {
        Assert.True(BelayClimbLimit.TryRestrict(native, braked, 1,
            new Vector3(0, 5, 0), Vector3.UnitY, out var allowed));
        Assert.Equal(native, allowed);
    }

    [Fact]
    public void AnOverrunCanMoveInwardWithoutBeingTeleportedBack()
    {
        Assert.True(BelayClimbLimit.TryRestrict(true, true, 2,
            new Vector3(0, 2.1f, 0), -Vector3.UnitY, out var allowed));
        Assert.True(allowed);
        Assert.True(BelayClimbLimit.TryRestrict(true, true, 2,
            new Vector3(0, 2.1f, 0), Vector3.UnitY, out allowed));
        Assert.False(allowed);
    }

    [Theory]
    [InlineData(0f, 2f, 1f)]
    [InlineData(201f, 2f, 1f)]
    [InlineData(float.NaN, 2f, 1f)]
    [InlineData(2f, float.NaN, 1f)]
    [InlineData(2f, 2f, float.PositiveInfinity)]
    public void InvalidGeometryIsReportedWithoutChangingNativePermission(float released, float distance, float direction)
    {
        Assert.False(BelayClimbLimit.TryRestrict(true, true, released,
            new Vector3(0, distance, 0), new Vector3(0, direction, 0), out var allowed));
        Assert.True(allowed);
    }
}
