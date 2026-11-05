using System.Linq;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayRopeVisualSamplingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SegmentOrientationKeepsNativeSlackAndDoesNotRewriteItsInput(bool backwards)
    {
        var start = new WorldPosition(8, 20, -3); var end = new WorldPosition(12, 20, -3);
        var points = new WorldPosition[] { start, new(10, 17, -3), end };
        if (backwards) System.Array.Reverse(points);
        var original = (WorldPosition[])points.Clone();
        Assert.True(BelayRopeVisualSampling.TryOrient(points, start, end, out var reverse, out var startError, out var endError));
        Assert.Equal(backwards, reverse);
        Assert.Equal(0, startError); Assert.Equal(0, endError);
        Assert.Equal(original, points);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1.001f, false)]
    public void EachNativeEndpointRetainsTheOneMeterLimit(float error, bool accepted)
    {
        var start = new WorldPosition(0, 0, 0); var end = new WorldPosition(10, 0, 0);
        var points = new WorldPosition[] { new(0, error, 0), end };
        Assert.Equal(accepted, BelayRopeVisualSampling.TryOrient(points, start, end, out _, out var startError, out _));
        Assert.Equal(error, startError);
        points = new WorldPosition[] { start, new(10, error, 0) };
        Assert.Equal(accepted, BelayRopeVisualSampling.TryOrient(points, start, end, out _, out _, out var endError));
        Assert.Equal(error, endError);
    }

    [Fact]
    public void ATranslatedOrStaleDrawingCannotBeMovedOntoTheExpectedRoute()
    {
        var points = new WorldPosition[] { new(50, 100, 0), new(51, 98, 0), new(52, 100, 0) };
        Assert.False(BelayRopeVisualSampling.TryOrient(points, default, new(2, 0, 0), out _, out var startError, out var endError));
        Assert.True(startError > 1); Assert.True(endError > 1);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void FiniteEndpointsCannotHideAnInvalidInteriorVertex(float value)
    {
        var points = new WorldPosition[] { default, new(value, 0, 0), new(2, 0, 0) };
        Assert.False(BelayRopeVisualSampling.TryOrient(points, default, new(2, 0, 0), out _, out _, out _));
    }

    [Fact]
    public void SegmentValidationBoundsVertexCountAndRejectsInvalidHolderPositions()
    {
        var points = new WorldPosition[4096];
        Assert.True(BelayRopeVisualSampling.TryOrient(points, default, default, out _, out _, out _));
        Assert.False(BelayRopeVisualSampling.TryOrient(new WorldPosition[4097], default, default, out _, out _, out _));
        Assert.False(BelayRopeVisualSampling.TryOrient(new WorldPosition[1], default, default, out _, out _, out _));
        Assert.False(BelayRopeVisualSampling.TryOrient(null, default, default, out _, out _, out _));
        Assert.False(BelayRopeVisualSampling.TryOrient(points, new(float.NaN, 0, 0), default, out _, out _, out _));
        Assert.False(BelayRopeVisualSampling.TryOrient(points, default, new(float.PositiveInfinity, 0, 0), out _, out _, out _));
    }

    [Fact]
    public void EveryProtectionJunctionSurvivesTheNetworkPointBudget()
    {
        var segments = Enumerable.Range(0, 63).Select(i => Enumerable.Range(0, 20)
            .Select(j => new WorldPosition(i, j, 0)).ToArray()).ToArray();
        var sampled = BelayRopeVisualSampling.Sample(segments, 192);
        Assert.Equal(192, sampled.Length);
        foreach (var segment in segments)
        {
            Assert.Contains(segment[0], sampled);
            Assert.Contains(segment[^1], sampled);
        }
        Assert.Equal(segments[0][0], sampled[0]); Assert.Equal(segments[^1][^1], sampled[^1]);
    }

    [Fact]
    public void ShortRoutesKeepAllNativePointsIncludingTheSlackShape()
    {
        var points = new WorldPosition[] { new(0, 0, 0), new(.5f, -3, 0), new(1, 0, 0) };
        Assert.Equal(points, BelayRopeVisualSampling.Sample(new[] { points }, 192));
        Assert.Null(BelayRopeVisualSampling.Sample(new[] { points }, 1));
        Assert.Null(BelayRopeVisualSampling.Sample(new[] { new WorldPosition[1] }, 192));
    }
}
