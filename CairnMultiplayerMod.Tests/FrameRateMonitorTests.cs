using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

/// <summary>
/// These numbers decide whether an engine setting gets kept, so the 1% low in particular has
/// to be right: a change can raise the average while making stutter worse, and that is the
/// case a player actually notices.
/// </summary>
public sealed class FrameRateMonitorTests
{
    private static FrameRateMonitor WithSteadyFps(double fps, int frames)
    {
        var m = new FrameRateMonitor();
        for (var i = 0; i < frames; i++) m.Add(1.0 / fps);
        return m;
    }

    [Fact]
    public void AFreshMonitorReportsNothing()
    {
        var m = new FrameRateMonitor();
        Assert.Equal(0, m.AverageFps);
        Assert.Equal(0, m.OnePercentLowFps);
        Assert.Equal(0, m.WorstFrameMs);
    }

    [Fact]
    public void SteadyFramesGiveThatFrameRate()
    {
        var m = WithSteadyFps(60, 600);
        Assert.Equal(60, m.AverageFps, 1);
        Assert.Equal(60, m.OnePercentLowFps, 1);
        Assert.Equal(16.67, m.WorstFrameMs, 1);
    }

    [Fact]
    public void AStutterBarelyMovesTheAverageButShowsInTheOnePercentLow()
    {
        var m = WithSteadyFps(60, 500);
        m.Add(0.2); // one 200 ms hitch

        Assert.True(m.AverageFps > 55, $"average was {m.AverageFps}");
        Assert.True(m.OnePercentLowFps < 30, $"1% low was {m.OnePercentLowFps}");
        Assert.Equal(200, m.WorstFrameMs, 0);
    }

    [Fact]
    public void ImpossibleDeltasAreIgnored()
    {
        var m = new FrameRateMonitor();
        m.Add(0);
        m.Add(-1);
        m.Add(double.NaN);
        m.Add(double.PositiveInfinity);
        m.Add(double.NegativeInfinity);

        Assert.Equal(0, m.SampleCount);
        Assert.Equal(0, m.ElapsedSeconds);
    }

    [Fact]
    public void ASixSecondGapRemainsVisibleInAverageWorstAndOnePercentLow()
    {
        var m = WithSteadyFps(60, 60);
        m.Add(6);

        Assert.Equal(61, m.SampleCount);
        Assert.Equal(7, m.ElapsedSeconds, 6);
        Assert.Equal(61.0 / 7, m.AverageFps, 6);
        Assert.Equal(6000, m.WorstFrameMs, 6);
        Assert.Equal(1.0 / 6, m.OnePercentLowFps, 6);
    }

    [Fact]
    public void RepeatedLowReadsAndResetDoNotIncludeOldScratchSamples()
    {
        var m = WithSteadyFps(60, 120);
        m.Add(6);
        Assert.Equal(1.0 / 6, m.OnePercentLowFps, 6);
        Assert.Equal(1.0 / 6, m.OnePercentLowFps, 6);

        m.Reset();
        for (var i = 0; i < 60; i++) m.Add(1.0 / 120);
        Assert.Equal(120, m.OnePercentLowFps, 6);
        Assert.Equal(120, m.AverageFps, 6);
        Assert.Equal(1000.0 / 120, m.WorstFrameMs, 6);
    }

    [Fact]
    public void TheWindowStaysBoundedOverALongRun()
    {
        var m = WithSteadyFps(60, 10000);

        Assert.Equal(2048, m.SampleCount);
        Assert.Equal(60, m.AverageFps, 1);
    }

    [Fact]
    public void ResetStartsAFreshWindow()
    {
        var m = WithSteadyFps(30, 100);
        m.Reset();

        Assert.Equal(0, m.SampleCount);
        Assert.Equal(0, m.AverageFps);
        Assert.Equal(0, m.ElapsedSeconds);
    }

    [Fact]
    public void TheDescriptionCarriesTheThreeFiguresThatMatter()
    {
        var text = WithSteadyFps(60, 120).Describe();

        Assert.Contains("avg", text);
        Assert.Contains("1% low", text);
        Assert.Contains("worst frame", text);
    }
}
