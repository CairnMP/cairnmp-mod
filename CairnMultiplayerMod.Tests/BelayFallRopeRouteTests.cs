using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayFallRopeRouteTests
{
    [Fact]
    public void R31SixHoldersAndThreePartsMeasureOnlyTheStationToHarnessWalk()
    {
        // The R31 trace repeats the same robot three times before A, B and harness.
        long[] holders = { 900, 900, 900, 100, 200, 300 };
        BelayFallRopePart[] parts =
        {
            new(1, 900, 100, float.NaN, false),
            new(2, 100, 200, 2.5f, true),
            new(3, 200, 300, 3, true)
        };
        Span<int> selected = stackalloc int[2];

        Assert.True(BelayFallRopeRoute.TryMeasure(holders.AsSpan(3, 3), parts, selected,
            out var length, out var failure, out var failedLeg));
        Assert.Equal(5.5f, length);
        Assert.Equal(BelayFallRouteFailure.None, failure);
        Assert.Equal(-1, failedLeg);
        Assert.Equal(1, selected[0]);
        Assert.Equal(2, selected[1]);
    }

    [Fact]
    public void ReversedPinsAndReorderedPartsKeepIndicesInWalkOrder()
    {
        long[] colliders = { 100, 200, 300, 400 };
        BelayFallRopePart[] parts =
        {
            new(30, 400, 300, 3, true),
            new(10, 200, 100, 1, true),
            new(20, 300, 200, 2, true)
        };
        Span<int> selected = stackalloc int[4];
        selected[3] = 123;

        Assert.True(BelayFallRopeRoute.TryMeasure(colliders, parts, selected, out var length, out _, out _));
        Assert.Equal(6, length);
        Assert.Equal(1, selected[0]);
        Assert.Equal(2, selected[1]);
        Assert.Equal(0, selected[2]);
        Assert.Equal(123, selected[3]);
        Assert.Equal(10, parts[selected[0]].Id);
        Assert.Equal(20, parts[selected[1]].Id);
        Assert.Equal(30, parts[selected[2]].Id);
    }

    [Fact]
    public void UnrelatedUpstreamAndDownstreamPartsNeedNoValidLengthOrLoadedState()
    {
        long[] colliders = { 100, 200, 300 };
        BelayFallRopePart[] parts =
        {
            new(0, 900, 100, float.NaN, false),
            new(2, 100, 200, 2, true),
            new(0, 300, 400, float.PositiveInfinity, false),
            new(3, 200, 300, 3, true),
            new(4, 500, 600, -1, true)
        };
        Span<int> selected = stackalloc int[2];

        Assert.True(BelayFallRopeRoute.TryMeasure(colliders, parts, selected, out var length, out _, out _));
        Assert.Equal(5, length);
        Assert.Equal(1, selected[0]);
        Assert.Equal(3, selected[1]);
    }

    [Fact]
    public void AnUnrelatedDuplicatePartIdentityDoesNotInvalidateTheSelectedWalk()
    {
        long[] colliders = { 100, 200, 300 };
        BelayFallRopePart[] parts =
        {
            new(1, 100, 200, 2, true),
            new(2, 200, 300, 3, true),
            new(1, 800, 900, 5, true)
        };
        Span<int> selected = stackalloc int[2];

        Assert.True(BelayFallRopeRoute.TryMeasure(colliders, parts, selected, out var length, out _, out _));
        Assert.Equal(5, length);
    }

    [Fact]
    public void AmbiguousPartsInTheExcludedPrefixDoNotMakeTheSelectedWalkAmbiguous()
    {
        long[] colliders = { 100, 200, 300 };
        BelayFallRopePart[] parts =
        {
            new(8, 900, 100, float.NaN, false),
            new(9, 100, 900, -1, false),
            new(1, 100, 200, 2, true),
            new(2, 200, 300, 3, true)
        };
        Span<int> selected = stackalloc int[2];

        Assert.True(BelayFallRopeRoute.TryMeasure(colliders, parts, selected, out var length, out _, out _));
        Assert.Equal(5, length);
        Assert.Equal(2, selected[0]);
        Assert.Equal(3, selected[1]);
    }

    [Fact]
    public void ASingleDirectStationToHarnessPartIsAValidRoute()
    {
        long[] colliders = { 100, 300 };
        BelayFallRopePart[] parts = { new(1, 300, 100, .375f, true) };
        Span<int> selected = stackalloc int[1];

        Assert.True(BelayFallRopeRoute.TryMeasure(colliders, parts, selected, out var length, out _, out _));
        Assert.Equal(.375f, length);
        Assert.Equal(0, selected[0]);
    }

    [Fact]
    public void MissingSecondLegNeverFallsBackToAnotherPartOrGeometry()
    {
        AssertFailure(new long[] { 100, 200, 300 }, new BelayFallRopePart[]
        {
            new(1, 100, 200, 2, true),
            new(2, 200, 400, 3, true)
        }, BelayFallRouteFailure.MissingPart, 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateEndpointPairsAreAmbiguousEvenWhenOneCandidateIsUnloaded(bool duplicateLoaded)
    {
        AssertFailure(new long[] { 100, 200, 300 }, new BelayFallRopePart[]
        {
            new(1, 100, 200, 2, true),
            new(2, 200, 300, 3, true),
            new(3, 300, 200, duplicateLoaded ? 3 : float.NaN, duplicateLoaded)
        }, BelayFallRouteFailure.AmbiguousPart, 1);
    }

    [Fact]
    public void AReturnAlongTheSamePartCannotCountItsLengthTwice()
    {
        AssertFailure(new long[] { 100, 200, 100 }, new BelayFallRopePart[]
        {
            new(1, 100, 200, 2, true)
        }, BelayFallRouteFailure.ReusedPart, 1);
    }

    [Fact]
    public void DifferentPartIndicesCannotReuseTheSameNativeIdentity()
    {
        AssertFailure(new long[] { 100, 200, 300 }, new BelayFallRopePart[]
        {
            new(1, 100, 200, 2, true),
            new(1, 200, 300, 3, true)
        }, BelayFallRouteFailure.ReusedPart, 1);
    }

    [Fact]
    public void AMatchingPartWithoutANativeIdentityIsMissing()
    {
        AssertFailure(new long[] { 100, 200 }, new BelayFallRopePart[]
        {
            new(0, 100, 200, 2, true)
        }, BelayFallRouteFailure.MissingPart, 0);
    }

    [Theory]
    [InlineData(0, 200, 300, 0)]
    [InlineData(100, 0, 300, 0)]
    [InlineData(100, 200, 0, 1)]
    [InlineData(100, 100, 300, 0)]
    [InlineData(100, 200, 200, 1)]
    public void ZeroCollidersAndSelfLoopsIdentifyTheInvalidLeg(long first, long second, long third, int failedLeg)
    {
        AssertFailure(new[] { first, second, third }, new BelayFallRopePart[]
        {
            new(1, 100, 200, 2, true),
            new(2, 200, 300, 3, true)
        }, BelayFallRouteFailure.InvalidCollider, failedLeg);
    }

    [Fact]
    public void ASelectedUnloadedPartIsRejected()
    {
        AssertFailure(new long[] { 100, 200, 300 }, new BelayFallRopePart[]
        {
            new(1, 100, 200, 2, true),
            new(2, 200, 300, 3, false)
        }, BelayFallRouteFailure.UnloadedPart, 1);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidSelectedRestLengthsNeverProduceAPartialMeasurement(float rest)
    {
        AssertFailure(new long[] { 100, 200, 300 }, new BelayFallRopePart[]
        {
            new(1, 100, 200, 2, true),
            new(2, 200, 300, rest, true)
        }, BelayFallRouteFailure.InvalidLength, 1);
    }

    [Theory]
    [InlineData(100f, 100.001f)]
    [InlineData(199f, 2f)]
    [InlineData(1f, float.MaxValue)]
    public void ATotalAboveTwoHundredMetresIsRejected(float firstLength, float secondLength)
    {
        AssertFailure(new long[] { 100, 200, 300 }, new BelayFallRopePart[]
        {
            new(1, 100, 200, firstLength, true),
            new(2, 200, 300, secondLength, true)
        }, BelayFallRouteFailure.InvalidLength, 1);
    }

    [Fact]
    public void ExactlyTwoHundredMetresIsAccepted()
    {
        long[] colliders = { 100, 200, 300 };
        BelayFallRopePart[] parts = { new(1, 100, 200, 100, true), new(2, 200, 300, 100, true) };
        Span<int> selected = stackalloc int[2];

        Assert.True(BelayFallRopeRoute.TryMeasure(colliders, parts, selected, out var length, out _, out _));
        Assert.Equal(200, length);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(66, 1, 65)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 65, 1)]
    [InlineData(2, 1, 0)]
    [InlineData(3, 2, 1)]
    public void InvalidBoundsFailBeforeInspectingAnyLeg(int colliderCount, int partCount, int selectedCapacity)
    {
        var colliders = new long[colliderCount];
        var parts = new BelayFallRopePart[partCount];
        var selected = new int[selectedCapacity];

        Assert.False(BelayFallRopeRoute.TryMeasure(colliders, parts, selected,
            out var length, out var failure, out var failedLeg));
        Assert.True(float.IsNaN(length));
        Assert.Equal(BelayFallRouteFailure.InvalidBounds, failure);
        Assert.Equal(-1, failedLeg);
    }

    [Fact]
    public void TheMaximumSixtyFourLegWalkUsesCallerOwnedSpanBuffers()
    {
        Span<long> colliders = stackalloc long[65];
        Span<BelayFallRopePart> parts = stackalloc BelayFallRopePart[64];
        Span<int> selected = stackalloc int[64];
        for (var i = 0; i < colliders.Length; i++) colliders[i] = i + 1;
        for (var i = 0; i < parts.Length; i++) parts[i] = new(i + 1, i + 1, i + 2, 1, true);

        Assert.True(BelayFallRopeRoute.TryMeasure(colliders, parts, selected,
            out var length, out var failure, out var failedLeg));
        Assert.Equal(64, length);
        Assert.Equal(BelayFallRouteFailure.None, failure);
        Assert.Equal(-1, failedLeg);
        for (var i = 0; i < selected.Length; i++) Assert.Equal(i, selected[i]);
    }

    private static void AssertFailure(long[] colliders, BelayFallRopePart[] parts,
        BelayFallRouteFailure expectedFailure, int expectedLeg)
    {
        Span<int> selected = stackalloc int[Math.Max(0, colliders.Length - 1)];
        Assert.False(BelayFallRopeRoute.TryMeasure(colliders, parts, selected,
            out var length, out var failure, out var failedLeg));
        Assert.True(float.IsNaN(length));
        Assert.Equal(expectedFailure, failure);
        Assert.Equal(expectedLeg, failedLeg);
    }
}
