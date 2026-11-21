using System;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayRopeVisualPlaybackTests
{
    [Fact]
    public void FirstDrawingRetainsEveryNativeVertexIncludingSlackAndProtectionJunctions()
    {
        var playback = new BelayRopeVisualPlayback();
        var points = new WorldPosition[] { new(2, 10, 4), new(3, 7, 5), new(4, 11, 6), new(5, 6, 7), new(6, 12, 8) };
        Assert.True(playback.SetTarget(Guid.NewGuid(), 3, points, 1));
        Assert.Equal(points, playback.Sample(1).ToArray());
        Assert.Equal(points, playback.Sample(1.05).ToArray());
        Assert.Equal(points, playback.Sample(2).ToArray());
    }

    [Fact]
    public void ANewPacketInterpolatesAllCoordinatesWithoutFlatteningItsSlack()
    {
        var playback = new BelayRopeVisualPlayback();
        var session = Guid.NewGuid();
        var first = new WorldPosition[] { new(0, 10, 0), new(2, 6, 4), new(4, 10, 8) };
        var next = new WorldPosition[] { new(2, 12, 2), new(4, 2, 6), new(6, 12, 10) };
        playback.SetTarget(session, 7, first, 0);
        playback.SetTarget(session, 7, next, 1);
        Assert.Equal(first, playback.Sample(1).ToArray());
        var middle = playback.Sample(1.05);
        AssertPoint(new(1, 11, 1), middle[0]);
        AssertPoint(new(3, 4, 5), middle[1]);
        AssertPoint(new(5, 11, 9), middle[2]);
        Assert.Equal(next, playback.Sample(1.1).ToArray());
    }

    [Fact]
    public void APacketArrivingMidBlendStartsAtTheLastDrawnShapeInsteadOfThePreviousTarget()
    {
        var playback = new BelayRopeVisualPlayback();
        var session = Guid.NewGuid();
        playback.SetTarget(session, 1, Rope(0), 0);
        playback.SetTarget(session, 1, Rope(10), 1);
        var displayed = playback.Sample(1.025).ToArray();
        playback.SetTarget(session, 1, Rope(20), 1.04);
        Assert.Equal(displayed, playback.Sample(1.04).ToArray());
        var halfway = playback.Sample(1.09);
        AssertPoint(new(11.25f, -2, 0), halfway[1]);
        Assert.Equal(Rope(20), playback.Sample(1.2).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANewSessionOrRouteRevisionCannotInterpolateAcrossThePreviousRoute(bool newSession)
    {
        var playback = new BelayRopeVisualPlayback();
        var session = Guid.NewGuid();
        playback.SetTarget(session, 1, Rope(0), 0);
        playback.SetTarget(session, 1, Rope(10), 1);
        playback.Sample(1.05);
        var changedSession = newSession ? Guid.NewGuid() : session;
        var changedRevision = newSession ? 1u : 2u;
        playback.SetTarget(changedSession, changedRevision, Rope(30), 1.06);
        Assert.Equal(Rope(30), playback.Sample(1.06).ToArray());
        Assert.Equal(changedSession, playback.Session);
        Assert.Equal(changedRevision, playback.Revision);
    }

    [Fact]
    public void ChangingPointCountResetsTheShapeInsteadOfMatchingUnrelatedVertices()
    {
        var playback = new BelayRopeVisualPlayback();
        var session = Guid.NewGuid();
        playback.SetTarget(session, 1, Rope(0), 0);
        var longer = new WorldPosition[] { new(0, 5, 0), new(1, 1, 0), new(2, 6, 0), new(3, 3, 0) };
        playback.SetTarget(session, 1, longer, 1);
        Assert.Equal(4, playback.Count);
        Assert.Equal(longer, playback.Sample(1).ToArray());
        playback.SetTarget(session, 1, Rope(10), 2);
        Assert.Equal(Rope(10), playback.Sample(2).ToArray());
    }

    [Fact]
    public void PlaybackOwnsItsPacketSnapshotAndDoesNotExtrapolateOutsideTheBlendWindow()
    {
        var playback = new BelayRopeVisualPlayback();
        var session = Guid.NewGuid();
        playback.SetTarget(session, 1, Rope(0), 0);
        var next = Rope(10);
        playback.SetTarget(session, 1, next, 1);
        next[1] = new(999, 999, 999);
        Assert.Equal(Rope(0), playback.Sample(.5).ToArray());
        Assert.Equal(Rope(10), playback.Sample(100).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(193)]
    public void AnInvalidPacketCountLeavesTheCurrentDrawingAndIdentityUntouched(int count)
    {
        var playback = new BelayRopeVisualPlayback();
        var session = Guid.NewGuid();
        playback.SetTarget(session, 1, Rope(0), 1);
        Assert.False(playback.SetTarget(Guid.NewGuid(), 2, new WorldPosition[count], 2));
        Assert.Equal(session, playback.Session);
        Assert.Equal(1u, playback.Revision);
        Assert.Equal(1, playback.UpdatedAt);
        Assert.Equal(Rope(0), playback.Current.ToArray());
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidInteriorCoordinatesCannotPoisonThePersistentDrawing(float value)
    {
        var playback = new BelayRopeVisualPlayback();
        var session = Guid.NewGuid();
        playback.SetTarget(session, 1, Rope(0), 1);
        for (var axis = 0; axis < 3; axis++)
        {
            var points = Rope(10);
            points[1] = axis == 0 ? new(value, 0, 0) : axis == 1 ? new(0, value, 0) : new(0, 0, value);
            Assert.False(playback.SetTarget(session, 2, points, 2));
            Assert.Equal(Rope(0), playback.Current.ToArray());
            Assert.Equal(1u, playback.Revision);
        }
    }

    [Fact]
    public void InvalidTimeCannotReplaceOrPoisonTheLastDrawing()
    {
        var playback = new BelayRopeVisualPlayback();
        var session = Guid.NewGuid();
        playback.SetTarget(session, 1, Rope(0), 1);
        Assert.False(playback.SetTarget(session, 1, Rope(10), double.NaN));
        Assert.False(playback.SetTarget(session, 1, Rope(10), double.PositiveInfinity));
        Assert.Equal(Rope(0), playback.Sample(double.NaN).ToArray());
    }

    [Fact]
    public void ClearReleasesThePreviousRouteAndTheNextPacketStartsWithoutABlend()
    {
        var playback = new BelayRopeVisualPlayback();
        var session = Guid.NewGuid();
        playback.SetTarget(session, 1, Rope(0), 0);
        playback.SetTarget(session, 1, Rope(10), 1);
        playback.Sample(1.05);
        playback.Clear();
        Assert.Equal(0, playback.Count);
        Assert.Equal(Guid.Empty, playback.Session);
        Assert.True(playback.Sample(2).IsEmpty);
        playback.SetTarget(session, 1, Rope(30), 3);
        Assert.Equal(Rope(30), playback.Sample(3).ToArray());
    }

    [Fact]
    public void StableMaximumSizePacketPlaybackDoesNotAllocateAfterWarmup()
    {
        var playback = new BelayRopeVisualPlayback();
        var session = Guid.NewGuid();
        var first = new WorldPosition[192];
        var second = new WorldPosition[192];
        for (var i = 0; i < first.Length; i++)
        {
            first[i] = new(i, i % 3 - 2, i % 5);
            second[i] = new(i + 2, i % 3 - 4, i % 5 + 2);
        }
        PlayPackets(playback, session, first, second, 0, 100);
        var before = GC.GetAllocatedBytesForCurrentThread();
        PlayPackets(playback, session, first, second, 100, 1000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);
        Assert.Equal(second, playback.Current.ToArray());
    }

    private static void PlayPackets(BelayRopeVisualPlayback playback, Guid session,
        WorldPosition[] first, WorldPosition[] second, double start, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var now = start + i * .125;
            playback.SetTarget(session, 1, i % 2 == 0 ? first : second, now);
            playback.Sample(now);
            playback.Sample(now + .025);
            playback.Sample(now + .05);
            playback.Sample(now + .125);
        }
    }

    private static WorldPosition[] Rope(float x)
        => new WorldPosition[] { new(x, 0, 0), new(x, -2, 0), new(x, 0, 2) };

    private static void AssertPoint(WorldPosition expected, WorldPosition actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }
}
