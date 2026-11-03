using System;
using System.Numerics;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayRopeRebindTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClippingRedistributesPaidOutRopeAndPreservesBrake(bool braked)
    {
        var rope = new BelayRopeControl(35, 6, 2);
        rope.SetBrake(braked);
        rope.TrySetMotion(-1);
        Assert.True(rope.TryRebindRoute(3, 7, 31, 1));
        Assert.Equal(2, rope.Released);
        Assert.Equal(9, 7 + rope.Released);
        Assert.Equal(braked, rope.Braked);
        Assert.Equal(0, rope.Motion);
        Assert.Equal(31, rope.Capacity);
        Assert.Equal(1, rope.Distance);
    }

    [Fact]
    public void FreeClippingMayPayOutOnlyEnoughForTheNewGeometry()
    {
        var rope = new BelayRopeControl(35, 2, 1);
        Assert.True(rope.TryRebindRoute(3, 6, 32, .5f));
        Assert.Equal(.53f, rope.Released, 4);
        Assert.False(rope.Braked);
    }

    [Fact]
    public void ClosedBrakeCannotSilentlyGrantRopeToClipOutOfReach()
    {
        var rope = new BelayRopeControl(35, 2, 1);
        rope.SetBrake(true);
        rope.TrySetMotion(-1);
        Assert.False(rope.TryRebindRoute(3, 6, 32, .5f));
        Assert.Equal(2, rope.Released);
        Assert.Equal(35, rope.Capacity);
        Assert.Equal(1, rope.Distance);
        Assert.True(rope.Braked);
        Assert.Equal(-1, rope.Motion);
    }

    [Fact]
    public void ClosedBrakeCannotShortenTheNewFinalSegmentBelowItsCurrentDistance()
    {
        var rope = new BelayRopeControl(35, 2, 1);
        rope.SetBrake(true);
        // Positive rope remains, but it cannot span the new piton-to-harness gap.
        Assert.False(rope.TryRebindRoute(3, 4.9f, 32, .5f));
        Assert.Equal(2, rope.Released);
        Assert.Equal(1, rope.Distance);
        Assert.True(rope.Braked);
    }

    [Fact]
    public void RepeatedClipsCannotMultiplySlackOrResetTheBrake()
    {
        var rope = new BelayRopeControl(38, 10, 2);
        rope.SetBrake(true);
        Assert.True(rope.TryRebindRoute(0, 3, 35, 1));
        Assert.Equal(7, rope.Released);
        Assert.True(rope.TryRebindRoute(3, 6, 32, 1));
        Assert.Equal(4, rope.Released);
        Assert.Equal(10, 6 + rope.Released);
        Assert.True(rope.Braked);
    }

    [Fact]
    public void ALoadedConstraintCannotBeReboundEvenWithEnoughSlack()
    {
        var rope = new BelayRopeControl(35, 6, 2);
        Assert.True(rope.TryAttachSuspension(2, out _));
        Assert.False(rope.TryRebindRoute(3, 7, 31, 1));
        Assert.True(rope.Suspended);
        Assert.Equal(6, rope.Released);
    }

    [Theory]
    [InlineData(float.NaN, 3, 35, 1)]
    [InlineData(0, float.PositiveInfinity, 35, 1)]
    [InlineData(-1, 3, 35, 1)]
    [InlineData(0, -1, 35, 1)]
    [InlineData(0, 3, float.NaN, 1)]
    [InlineData(0, 3, 0, 1)]
    [InlineData(0, 3, 201, 1)]
    [InlineData(0, 3, 35, float.NaN)]
    [InlineData(0, 3, 35, 0)]
    [InlineData(0, 3, 35, 36)]
    [InlineData(199, 3, 35, 1)]
    [InlineData(0, 199, 35, 1)]
    [InlineData(0, 1, 2, 1)]
    public void InvalidOrInsufficientCapacityNeverClampsExistingPaidOutRope(float previous, float next, float capacity, float distance)
    {
        var rope = new BelayRopeControl(35, 6, 2);
        Assert.False(rope.TryRebindRoute(previous, next, capacity, distance));
        Assert.Equal(6, rope.Released);
        Assert.Equal(35, rope.Capacity);
    }

    [Fact]
    public void OnlyOneNewProtectionAtTheEndPreservesTheOldOrderedRoute()
    {
        var a = Point(1); var b = Point(2); var c = Point(3); var d = Point(4);
        Assert.True(BelayRoutePlanner.IsSingleProtectionAppend(new[] { a, b }, new[] { a, b, c }));
        Assert.False(BelayRoutePlanner.IsSingleProtectionAppend(new[] { a, b }, new[] { b, a, c }));
        Assert.False(BelayRoutePlanner.IsSingleProtectionAppend(new[] { a, b }, new[] { a, c }));
        Assert.False(BelayRoutePlanner.IsSingleProtectionAppend(new[] { a, b }, new[] { a, b }));
        Assert.False(BelayRoutePlanner.IsSingleProtectionAppend(new[] { a, b }, new[] { a, b, c, d }));
        Assert.False(BelayRoutePlanner.IsSingleProtectionAppend(new[] { a, b }, new[] { a, b, a }));
        Assert.False(BelayRoutePlanner.IsSingleProtectionAppend(Array.Empty<BelayProtectionPoint>(), new[] { a }));
        Assert.False(BelayRoutePlanner.IsSingleProtectionAppend(null, new[] { a }));
    }

    [Fact]
    public void StableTemporaryHoldersCannotReplaceTheAcceptedProtectionRoute()
    {
        var before = new[] { Point(1), Point(2) };
        var tracker = new BelayTopologyTracker();
        var accepted = Snapshot(2, new long[] { 11, 12, 13 }, 2);
        var temporary = Snapshot(2, new long[] { 11, 12, 13, 14 }, 2);
        var appended = Snapshot(3, new long[] { 11, 12, 15, 14 }, 3);
        tracker.Observe(accepted, false, 0);
        Assert.Equal(BelayTopologyChange.Settling, tracker.ObserveProtectionAppend(temporary, before, before, .1));
        Assert.Equal(BelayTopologyChange.Settling, tracker.ObserveProtectionAppend(temporary, before, before, .4));
        Assert.Same(accepted, tracker.Current);
        Assert.Equal(1u, tracker.Revision);
        var after = new[] { Point(1), Point(2), Point(3) };
        Assert.Equal(BelayTopologyChange.Settling, tracker.ObserveProtectionAppend(appended, before, after, .5));
        Assert.Equal(BelayTopologyChange.Rebound, tracker.ObserveProtectionAppend(appended, before, after, .7));
        Assert.Same(appended, tracker.Current);
        Assert.Equal(2u, tracker.Revision);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(1)]
    [InlineData(1, 2, 2)]
    [InlineData(1, 2, 3, 4)]
    public void IncompleteOrUnsupportedClipsNeverPromoteANewMapping(params uint[] ids)
    {
        var before = new[] { Point(1), Point(2) };
        var after = Array.ConvertAll(ids, Point);
        var tracker = new BelayTopologyTracker();
        var accepted = Snapshot(2, new long[] { 11, 12, 13 }, 2);
        var candidate = Snapshot(3, new long[] { 11, 12, 15, 14 }, 3);
        tracker.Observe(accepted, false, 0);
        tracker.ObserveProtectionAppend(candidate, before, after, .1);
        Assert.Equal(BelayTopologyChange.Settling, tracker.ObserveProtectionAppend(candidate, before, after, 1));
        Assert.Same(accepted, tracker.Current);
        Assert.Equal(1u, tracker.Revision);
    }

    [Fact]
    public void AnInterruptedClipMustSettleAgainBeforeTheNewMappingIsAccepted()
    {
        var before = new[] { Point(1), Point(2) };
        var after = new[] { Point(1), Point(2), Point(3) };
        var tracker = new BelayTopologyTracker();
        var accepted = Snapshot(2, new long[] { 11, 12, 13 }, 2);
        var appended = Snapshot(3, new long[] { 11, 12, 15, 14 }, 3);
        tracker.Observe(accepted, false, 0);
        tracker.ObserveProtectionAppend(appended, before, after, .1);
        tracker.ObserveProtectionAppend(accepted, before, before, .3);
        Assert.Equal(BelayTopologyChange.Settling, tracker.ObserveProtectionAppend(appended, before, after, .4));
        Assert.Equal(BelayTopologyChange.Rebound, tracker.ObserveProtectionAppend(appended, before, after, .6));
    }

    [Fact]
    public void InvalidOwnershipOrTimeCannotBeTreatedAsAClipInProgress()
    {
        var before = new[] { Point(1), Point(2) };
        var tracker = new BelayTopologyTracker();
        var accepted = Snapshot(2, new long[] { 11, 12, 13 }, 2);
        tracker.Observe(accepted, false, 1);
        var replaced = new BelayTopologySnapshot(99, 2, new long[] { 21, 22 }, new long[] { 11, 12 }, 35, 2);
        Assert.Equal(BelayTopologyChange.Invalid, tracker.ObserveProtectionAppend(replaced, before, before, 2));
        Assert.Equal(BelayTopologyChange.Invalid, tracker.ObserveProtectionAppend(accepted, before, before, .5));
        Assert.Equal(BelayTopologyChange.Invalid, tracker.ObserveProtectionAppend(accepted, before, before, double.NaN));
        Assert.Same(accepted, tracker.Current);
        Assert.Equal(1u, tracker.Revision);
    }

    [Fact]
    public void AClippedPitonCannotRebindUntilItsTemporaryRopeHolderIsReleased()
    {
        var before = new[] { Point(1), Point(2) };
        var after = new[] { Point(1), Point(2), Point(3) };
        var tracker = new BelayTopologyTracker();
        // Native r8 sequence: 2 pitons/3 parts/5 holders -> 2/4/6 ->
        // 3/5/7 while clipping -> 3/4/6 after the held rope is released.
        var accepted = NativeSnapshot(2, 3, 5);
        var holding = NativeSnapshot(2, 4, 6);
        var clipped = NativeSnapshot(3, 5, 7);
        var released = NativeSnapshot(3, 4, 6);
        tracker.Observe(accepted, false, 0);
        tracker.ObserveProtectionAppend(holding, before, before, .005);
        Assert.Equal(BelayTopologyChange.Settling, tracker.ObserveProtectionAppend(clipped, before, after, .531));
        Assert.Equal(BelayTopologyChange.Settling, tracker.ObserveProtectionAppend(clipped, before, after, .745));
        Assert.Same(accepted, tracker.Current);
        Assert.Equal(1u, tracker.Revision);
        Assert.Equal(BelayTopologyChange.Settling, tracker.ObserveProtectionAppend(released, before, after, 1.201));
        Assert.Equal(BelayTopologyChange.Rebound, tracker.ObserveProtectionAppend(released, before, after, 1.401));
        Assert.Same(released, tracker.Current);
        Assert.Equal(2u, tracker.Revision);
        Assert.Equal(BelayTopologyChange.Unchanged, tracker.Observe(released, false, 6.401));
    }

    [Theory]
    [InlineData(3, 5, 7)]
    [InlineData(3, 4, 7)]
    [InlineData(3, 5, 6)]
    [InlineData(3, 3, 6)]
    [InlineData(3, 4, 5)]
    [InlineData(2, 4, 6)]
    public void CorrectPitonOrderCannotBypassAnIncompleteNativeSplit(int protections, int parts, int holders)
    {
        var before = new[] { Point(1), Point(2) };
        var after = new[] { Point(1), Point(2), Point(3) };
        var tracker = new BelayTopologyTracker();
        var accepted = NativeSnapshot(2, 3, 5);
        var incomplete = NativeSnapshot(protections, parts, holders);
        tracker.Observe(accepted, false, 0);
        tracker.ObserveProtectionAppend(incomplete, before, after, .1);
        Assert.Equal(BelayTopologyChange.Settling, tracker.ObserveProtectionAppend(incomplete, before, after, .4));
        Assert.Same(accepted, tracker.Current);
        Assert.Equal(1u, tracker.Revision);
    }

    private static BelayTopologySnapshot NativeSnapshot(int protections, int parts, int holders)
    {
        var partIds = new long[parts];
        var holderIds = new long[holders];
        for (var i = 0; i < parts; i++) partIds[i] = 21 + i;
        for (var i = 0; i < holders; i++) holderIds[i] = 11 + i;
        return new BelayTopologySnapshot(1, protections, partIds, holderIds, 35, protections);
    }

    private static BelayProtectionPoint Point(uint id) => new(id, new Vector3(0, id, 0));

    private static BelayTopologySnapshot Snapshot(long lastPiton, long[] holders, int protections)
    {
        var parts = new long[protections + 1];
        for (var i = 0; i < parts.Length; i++) parts[i] = 21 + i;
        return new BelayTopologySnapshot(1, lastPiton, parts, holders, 35, protections);
    }
}
