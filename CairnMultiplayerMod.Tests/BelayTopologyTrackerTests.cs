using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayTopologyTrackerTests
{
    private static BelayTopologySnapshot Route(long last = 30, float available = 36, long[] parts = null, long[] holders = null)
        => new(10, last, parts ?? new long[] { 11, 12 }, holders ?? new long[] { 20, 21, 22 }, available, (parts?.Length ?? 2) - 1);

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
        Assert.Equal(BelayTopologyChange.Invalid, tracker.Observe(Route(holders: new long[] { 20, 20 }), false, 1));
        Assert.Equal(1u, tracker.Revision);
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
}
