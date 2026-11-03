using System;
using System.Numerics;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayRoutePlannerTests
{
    [Theory]
    [InlineData(.049f, true)]
    [InlineData(.051f, false)]
    public void SharedStationResolutionRequiresOneNativePitonWithinFiveCentimeters(float offset, bool expected)
    {
        var protections = new[] { new BelayProtectionPoint(10, new Vector3(offset, 0, 0)),
            new BelayProtectionPoint(20, Vector3.UnitY) };
        Assert.Equal(expected, BelayRoutePlanner.TryFindStationPiton(Vector3.Zero, protections, out var id));
        Assert.Equal(expected ? 10u : 0u, id);
    }

    [Theory]
    [InlineData(10u)]
    [InlineData(20u)]
    public void CoincidentOrRepeatedStationOccurrencesCannotSelectAnArbitraryEndpoint(uint secondId)
    {
        var protections = new[] { new BelayProtectionPoint(10, Vector3.Zero),
            new BelayProtectionPoint(secondId, new Vector3(.01f, 0, 0)) };
        Assert.False(BelayRoutePlanner.TryFindStationPiton(Vector3.Zero, protections, out var id));
        Assert.Equal(0u, id);
    }

    [Fact]
    public void RepeatedIntermediatesDoNotMakeAUniqueStationAmbiguous()
    {
        var protections = new[] { new BelayProtectionPoint(10, Vector3.Zero),
            new BelayProtectionPoint(20, Vector3.UnitY), new BelayProtectionPoint(20, Vector3.UnitY) };
        Assert.True(BelayRoutePlanner.TryFindStationPiton(Vector3.Zero, protections, out var id));
        Assert.Equal(10u, id);
    }

    [Fact]
    public void InvalidProtectionAfterAStationMatchDoesNotReturnAPartialBinding()
    {
        var protections = new[] { new BelayProtectionPoint(10, Vector3.Zero),
            new BelayProtectionPoint(20, new Vector3(float.NaN, 0, 0)) };
        Assert.False(BelayRoutePlanner.TryFindStationPiton(Vector3.Zero, protections, out var id));
        Assert.Equal(0u, id);
        protections[1] = new BelayProtectionPoint(0, Vector3.One);
        Assert.False(BelayRoutePlanner.TryFindStationPiton(Vector3.Zero, protections, out id));
        Assert.Equal(0u, id);
    }

    [Fact]
    public void MissingNonFiniteAndOversizedStationRoutesAreRejected()
    {
        Assert.False(BelayRoutePlanner.TryFindStationPiton(Vector3.Zero, null, out _));
        Assert.False(BelayRoutePlanner.TryFindStationPiton(Vector3.Zero, Array.Empty<BelayProtectionPoint>(), out _));
        Assert.False(BelayRoutePlanner.TryFindStationPiton(new Vector3(float.NaN, 0, 0),
            new[] { new BelayProtectionPoint(1, Vector3.Zero) }, out _));
        Assert.False(BelayRoutePlanner.TryFindStationPiton(Vector3.Zero, new BelayProtectionPoint[64], out _));
    }

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
    public void InvalidPointsConflictingIdentityAndNonFiniteDataAreRejected()
    {
        var point = new BelayProtectionPoint(1, Vector3.One);
        Assert.False(BelayRoutePlanner.TryPlan(Vector3.Zero,
            new[] { point, new BelayProtectionPoint(1, Vector3.Zero) }, Vector3.One, 40, out _));
        Assert.False(BelayRoutePlanner.TryPlan(new Vector3(float.NaN, 0, 0), Array.Empty<BelayProtectionPoint>(), Vector3.One, 40, out _));
        Assert.False(BelayRoutePlanner.TryPlan(Vector3.Zero, Array.Empty<BelayProtectionPoint>(), Vector3.One, float.PositiveInfinity, out _));
        Assert.False(BelayRoutePlanner.TryPlan(Vector3.Zero, new[] { new BelayProtectionPoint(0, Vector3.One) }, Vector3.One, 40, out _));
    }

    [Fact]
    public void RepeatedProtectionOccurrencesRetainTheWalkLength()
    {
        var a = new BelayProtectionPoint(1, new Vector3(0, 3, 0));
        var b = new BelayProtectionPoint(2, new Vector3(4, 3, 0));
        Assert.True(BelayRoutePlanner.TryPlan(Vector3.Zero, new[] { a, b, a }, new Vector3(0, 4, 0), 40, out var plan));
        Assert.Equal(11, plan.FixedLength);
        Assert.Equal(3, plan.ProtectionCount);
        Assert.Equal(1, plan.ClimberDistance);
    }

    [Fact]
    public void CapturedStationExcludesTheUpstreamCoilAndEarlierProtections()
    {
        var points = new[] {
            new BelayProtectionPoint(1, new Vector3(0, -10, 0)),
            new BelayProtectionPoint(2, Vector3.Zero),
            new BelayProtectionPoint(3, new Vector3(0, 3, 0)),
            new BelayProtectionPoint(4, new Vector3(4, 3, 0)) };
        Assert.True(BelayRoutePlanner.TryPlanFromStation(2, points, new Vector3(4, 6, 0), 40, out var plan));
        Assert.Equal(7, plan.FixedLength);
        Assert.Equal(2, plan.ProtectionCount);
        Assert.Equal(3, plan.ClimberDistance);
        Assert.Equal(33, plan.AvailableToClimber);
    }

    [Fact]
    public void StationAtTheLastPitonHasNoIntermediatePath()
    {
        Assert.True(BelayRoutePlanner.TryPlanFromStation(1,
            new[] { new BelayProtectionPoint(1, new Vector3(0, 10, 0)) }, new Vector3(0, 8, 0), 40, out var plan));
        Assert.Equal(0, plan.FixedLength);
        Assert.Equal(0, plan.ProtectionCount);
        Assert.Equal(2, plan.ClimberDistance);
    }

    [Fact]
    public void MissingOrAmbiguousStationCannotBecomeADifferentAnchor()
    {
        var a = new BelayProtectionPoint(1, Vector3.Zero);
        var b = new BelayProtectionPoint(2, Vector3.UnitY);
        Assert.False(BelayRoutePlanner.TryPlanFromStation(3, new[] { a, b }, Vector3.One, 40, out _));
        Assert.False(BelayRoutePlanner.TryPlanFromStation(1, new[] { a, b, a }, Vector3.One, 40, out _));
        Assert.False(BelayRoutePlanner.TryPlanFromStation(0, new[] { a }, Vector3.One, 40, out _));
        Assert.False(BelayRoutePlanner.TryPlanFromStation(1, null, Vector3.One, 40, out _));
    }

    [Theory]
    [InlineData(33, 25, 25)]
    [InlineData(25, 33, 25)]
    [InlineData(25, 25, 25)]
    public void FinalBudgetNeverIncreasesNativeOrStationCapacity(float stationAvailable, float nativeAvailable, float expected)
    {
        var plan = new BelayRoutePlan(7, 3, stationAvailable, 2);
        Assert.True(BelayRoutePlanner.TryLimitFinalCapacity(plan, nativeAvailable, out var available));
        Assert.Equal(expected, available);
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(40, 0)]
    [InlineData(40, 201)]
    [InlineData(201, 40)]
    [InlineData(float.NaN, 40)]
    [InlineData(40, float.PositiveInfinity)]
    public void ExhaustedOrInvalidStationCapacityIsRejected(float stationAvailable, float nativeAvailable)
        => Assert.False(BelayRoutePlanner.TryLimitFinalCapacity(new BelayRoutePlan(0, 0, stationAvailable, 0), nativeAvailable, out _));
}
