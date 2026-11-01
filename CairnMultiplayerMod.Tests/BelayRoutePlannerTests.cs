using System;
using System.Numerics;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayRoutePlannerTests
{
    [Fact]
    public void IntendedSharedRouteAccountsForStationToAllPitonsBeforeTheClimber()
    {
        var pitons = new[] { new BelayProtectionPoint(1, new Vector3(0, 3, 0)), new BelayProtectionPoint(2, new Vector3(4, 3, 0)) };
        Assert.True(BelayRoutePlanner.TryPlan(Vector3.Zero, pitons, new Vector3(4, 6, 0), 40, out var plan));
        Assert.Equal(7, plan.FixedLength);
        Assert.Equal(33, plan.AvailableToClimber);
        Assert.Equal(3, plan.ClimberDistance);
        Assert.True(plan.ReachesClimber);
    }

    [Fact]
    public void RemovingMiddleProtectionRecalculatesPathRatherThanAddingAnotherAllowance()
    {
        var a = new BelayProtectionPoint(1, new Vector3(0, 3, 0));
        var b = new BelayProtectionPoint(2, new Vector3(4, 3, 0));
        var c = new BelayProtectionPoint(3, new Vector3(4, 0, 0));
        Assert.True(BelayRoutePlanner.TryPlan(Vector3.Zero, new[] { a, b, c }, new Vector3(4, -1, 0), 20, out var before));
        Assert.True(BelayRoutePlanner.TryPlan(Vector3.Zero, new[] { a, c }, new Vector3(4, -1, 0), 20, out var after));
        Assert.Equal(10, before.FixedLength);
        Assert.Equal(8, after.FixedLength);
        Assert.Equal(12, after.AvailableToClimber);
    }

    [Fact]
    public void RopeEndAndExhaustedFixedPathAreDifferentCases()
    {
        Assert.True(BelayRoutePlanner.TryPlan(Vector3.Zero, Array.Empty<BelayProtectionPoint>(), new Vector3(0, 41, 0), 40, out var plan));
        Assert.False(plan.ReachesClimber);
        Assert.False(BelayRoutePlanner.TryPlan(Vector3.Zero, new[] { new BelayProtectionPoint(1, new Vector3(0, 41, 0)) }, Vector3.Zero, 40, out _));
    }

    [Fact]
    public void RouteOrderMattersAndDoesNotDependOnVerticalOrdering()
    {
        var lower = new BelayProtectionPoint(1, new Vector3(0, 3, 0));
        var upper = new BelayProtectionPoint(2, new Vector3(0, 8, 0));
        Assert.True(BelayRoutePlanner.TryPlan(Vector3.Zero, new[] { lower, upper }, new Vector3(0, 2, 0), 40, out var upward));
        Assert.True(BelayRoutePlanner.TryPlan(Vector3.Zero, new[] { upper, lower }, new Vector3(0, 2, 0), 40, out var reversed));
        Assert.Equal(8, upward.FixedLength);
        Assert.Equal(13, reversed.FixedLength);
    }

    [Fact]
    public void InvalidPointsDuplicateIdentityAndNonFiniteDataAreRejected()
    {
        var point = new BelayProtectionPoint(1, Vector3.One);
        Assert.False(BelayRoutePlanner.TryPlan(Vector3.Zero, new[] { point, point }, Vector3.One, 40, out _));
        Assert.False(BelayRoutePlanner.TryPlan(new Vector3(float.NaN, 0, 0), Array.Empty<BelayProtectionPoint>(), Vector3.One, 40, out _));
        Assert.False(BelayRoutePlanner.TryPlan(Vector3.Zero, Array.Empty<BelayProtectionPoint>(), Vector3.One, float.PositiveInfinity, out _));
        Assert.False(BelayRoutePlanner.TryPlan(Vector3.Zero, new[] { new BelayProtectionPoint(0, Vector3.One) }, Vector3.One, 40, out _));
    }
}
