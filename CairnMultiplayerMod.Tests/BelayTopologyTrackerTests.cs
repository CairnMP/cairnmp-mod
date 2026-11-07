using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayTopologyTrackerTests
{
    private static BelayTopologySnapshot Route(long last = 30, float available = 36, long[] parts = null, long[] holders = null)
        => new(10, last, parts ?? new long[] { 11, 12 }, holders ?? new long[] { 20, 21, 22 }, available, (parts?.Length ?? 2) - 1);

    [Theory]
    [InlineData(false, false, -1, true)]
    [InlineData(false, false, 0, true)]
    [InlineData(true, false, 0, false)]
    [InlineData(false, true, 0, false)]
    [InlineData(false, false, 1, false)]
    [InlineData(false, false, float.NaN, false)]
    [InlineData(false, false, float.PositiveInfinity, false)]
    [InlineData(false, false, float.NegativeInfinity, false)]
    public void RecoveryCannotAcquireDuringAFallSuspensionOrNativeLengthOverride(
        bool falling, bool suspended, float lengthOverride, bool expected)
        => Assert.Equal(expected, Route().TryValidateControlResume(22, falling, suspended, lengthOverride, out _));

    [Fact]
    public void RecoveryStillRequiresTheCurrentCompleteProtectionAndUniqueHarnessWalk()
    {
        var transient = new BelayTopologySnapshot(10, 30, new long[] { 11, 12, 13 },
            new long[] { 20, 21, 99, 22 }, 36, 1);
        Assert.False(transient.TryValidateControlResume(22, false, false, 0, out _));
        Assert.False(Route(holders: new long[] { 20, 22, 21, 22 })
            .TryValidateControlResume(22, false, false, 0, out _));
        Assert.False(Route().TryValidateControlResume(99, false, false, 0, out _));
        Assert.True(Route(parts: new long[] { 31, 32 }, holders: new long[] { 20, 21, 22, 20 })
            .TryValidateControlResume(22, false, false, 0, out _));
    }

    [Fact]
    public void AddingAndRemovingProtectionRebindsOnlyAfterAStableInterval()
    {
        var tracker = new BelayTopologyTracker();
        Assert.Equal(BelayTopologyChange.Rebound, tracker.Observe(Route(), false, 0));
        var secondPiton = Route(31, 30, new long[] { 11, 13, 14 }, new long[] { 20, 21, 23, 22 });
        Assert.Equal(BelayTopologyChange.Settling, tracker.Observe(secondPiton, false, 1));
        Assert.Equal(BelayTopologyChange.Settling, tracker.Observe(secondPiton, false, 1.1));
        Assert.Equal(1u, tracker.Revision);
        Assert.Equal(BelayTopologyChange.Rebound, tracker.Observe(secondPiton, false, 1.2));
        Assert.Equal(2u, tracker.Revision);
        Assert.Equal(2, tracker.Current.ProtectionCount);
        Assert.Equal(BelayTopologyChange.Settling, tracker.Observe(Route(), false, 2));
        Assert.Equal(BelayTopologyChange.Rebound, tracker.Observe(Route(), false, 2.2));
        Assert.Equal(3u, tracker.Revision);
        Assert.Equal(1, tracker.Current.ProtectionCount);
    }

    [Fact]
    public void SameCountWithDifferentMiddleHolderIsStillADifferentRoute()
    {
        var tracker = new BelayTopologyTracker();
        tracker.Observe(Route(), false, 0);
        var rerouted = Route(holders: new long[] { 20, 99, 22 });
        Assert.Equal(BelayTopologyChange.Settling, tracker.Observe(rerouted, false, 1));
        Assert.Equal(BelayTopologyChange.Rebound, tracker.Observe(rerouted, false, 1.2));
    }

    [Fact]
    public void LoadedProtectionCannotSwitchUnderTheBody()
    {
        var tracker = new BelayTopologyTracker();
        tracker.Observe(Route(), false, 0);
        Assert.Equal(BelayTopologyChange.Invalid, tracker.Observe(Route(last: 99), true, 1));
        Assert.Equal(30, tracker.Current.LastPiton);
        Assert.Equal(1u, tracker.Revision);
    }

    [Fact]
    public void PartialAndReplacementRopesCannotBecomeNewBindings()
    {
        var tracker = new BelayTopologyTracker();
        tracker.Observe(Route(), false, 0);
        Assert.Equal(BelayTopologyChange.Invalid, tracker.Observe(Route(parts: new long[] { 11, 0 }), false, 1));
        Assert.Equal(BelayTopologyChange.Invalid, tracker.Observe(new(999, 30, new long[] { 11, 12 }, new long[] { 20, 21 }, 36, 1), false, 1));
        Assert.Equal(BelayTopologyChange.Invalid, tracker.Observe(Route(parts: new long[] { 11, 11 }), false, 1));
        Assert.Equal(BelayTopologyChange.Invalid, tracker.Observe(Route(holders: new long[] { 20, 0 }), false, 1));
        Assert.Equal(1u, tracker.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedEndpointHoldersPreserveAValidOrderedNativeRoute(bool twoPitons)
    {
        var parts = twoPitons ? new long[] { 11, 13, 14 } : new long[] { 11, 12 };
        var holders = twoPitons ? new long[] { 20, 21, 23, 22, 20 } : new long[] { 20, 21, 22, 20 };
        var route = Route(parts: parts, holders: holders);
        var tracker = new BelayTopologyTracker();
        Assert.Equal(BelayTopologyChange.Rebound, tracker.Observe(route, false, 0));
        Assert.Equal(holders, tracker.Current.Holders);
        Assert.Equal(twoPitons ? 2 : 1, tracker.Current.ProtectionCount);
        Assert.Equal(BelayTopologyChange.Unchanged, tracker.Observe(route, true, .1));
        Assert.Equal(1u, tracker.Revision);
    }

    [Fact]
    public void RepeatedHolderOrderAndMultiplicityStillRequireAnUnloadedStableRebind()
    {
        var tracker = new BelayTopologyTracker();
        var original = Route(holders: new long[] { 20, 21, 22, 20 });
        tracker.Observe(original, false, 0);
        var reordered = Route(holders: new long[] { 20, 22, 21, 20 });
        Assert.Equal(BelayTopologyChange.Invalid, tracker.Observe(reordered, true, 1));
        Assert.Equal(original.Holders, tracker.Current.Holders);
        Assert.Equal(BelayTopologyChange.Settling, tracker.Observe(reordered, false, 1));
        Assert.Equal(BelayTopologyChange.Rebound, tracker.Observe(reordered, false, 1.2));
        var anotherOccurrence = Route(holders: new long[] { 20, 22, 21, 20, 20 });
        Assert.Equal(BelayTopologyChange.Invalid, tracker.Observe(anotherOccurrence, true, 2));
        Assert.Equal(BelayTopologyChange.Settling, tracker.Observe(anotherOccurrence, false, 2));
        Assert.Equal(BelayTopologyChange.Rebound, tracker.Observe(anotherOccurrence, false, 2.2));
        Assert.Equal(3u, tracker.Revision);
    }

    [Fact]
    public void IntermediateFluctuationsResetSettlingAndNativePayoutDoesNotChangeIdentity()
    {
        var tracker = new BelayTopologyTracker();
        tracker.Observe(Route(), false, 0);
        Assert.Equal(BelayTopologyChange.Unchanged, tracker.Observe(Route(available: 35), true, .1));
        Assert.Equal(1u, tracker.Revision);
        tracker.Observe(Route(last: 31), false, 1);
        tracker.Observe(Route(last: 32), false, 1.1);
        Assert.Equal(BelayTopologyChange.Settling, tracker.Observe(Route(last: 32), false, 1.2));
        Assert.Equal(BelayTopologyChange.Rebound, tracker.Observe(Route(last: 32), false, 1.3));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0f)]
    [InlineData(201f)]
    public void InvalidCapacityNeverBecomesAControlTarget(float capacity)
    {
        Assert.Equal(BelayTopologyChange.Invalid, new BelayTopologyTracker().Observe(Route(available: capacity), false, 0));
    }

    [Fact]
    public void SnapshotsCopyNativeIdentityArraysAndBoundTheirSize()
    {
        var parts = new long[] { 11, 12 };
        var snapshot = Route(parts: parts);
        parts[1] = 0;
        Assert.True(snapshot.IsValid);
        Assert.False(Route(parts: new long[65]).IsValid);
        var tracker = new BelayTopologyTracker();
        Assert.Equal(BelayTopologyChange.Invalid, tracker.Observe(snapshot, false, double.NaN));
        tracker.Observe(snapshot, false, 1);
        Assert.Equal(BelayTopologyChange.Invalid, tracker.Observe(snapshot, false, .5));
    }

    [Fact]
    public void ANewFallWithTheSamePitonsHasANewApplicationGeneration()
    {
        var tracker = new BelayTopologyTracker();
        Assert.False(tracker.RearmAfterSuspension());
        tracker.Observe(Route(), false, 0);
        Assert.True(tracker.RearmAfterSuspension());
        Assert.Equal(2u, tracker.Revision);
        Assert.Equal(BelayTopologyChange.Unchanged, tracker.Observe(Route(), false, 1));
        Assert.Equal(2u, tracker.Revision);
    }

    [Fact]
    public void ControlCannotAcquireATemporarySplitAsItsPermanentBaseline()
    {
        // R38: three protections plus two temporary rope holders were acquired
        // as six parts, then ordinary cleanup reduced the same route to four.
        var transient = new BelayTopologySnapshot(1, 30,
            new long[] { 100, 101, 102, 103, 104, 105 },
            new long[] { 9, 9, 9, 9, 9, 9, 20, 51, 52, 21, 22, 40 }, 30, 3);
        Assert.True(transient.IsValid);
        Assert.False(transient.TryValidateControlStart(40, out var reason));
        Assert.Contains("settle", reason);
        var settled = new BelayTopologySnapshot(1, 30, new long[] { 100, 101, 104, 105 },
            new long[] { 9, 9, 9, 9, 9, 9, 20, 21, 22, 40 }, 30, 3);
        Assert.True(settled.TryValidateControlStart(40, out _));
        Assert.False(settled.TryValidateControlStart(41, out _));
    }

    [Fact]
    public void ControlStartDoesNotMistakeRepeatedRootPaddingForExtraPhysicalParts()
    {
        var padded = new BelayTopologySnapshot(1, 30, new long[] { 100, 101 },
            new long[] { 9, 9, 20, 40, 9 }, 30, 1);
        Assert.True(padded.TryValidateControlStart(40, out _));
        var repeatedHarness = new BelayTopologySnapshot(1, 30, new long[] { 100, 101 },
            new long[] { 9, 40, 20, 40 }, 30, 1);
        Assert.False(repeatedHarness.TryValidateControlStart(40, out _));
    }
}
