using System.Linq;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayRopeVisualSamplingTests
{
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
