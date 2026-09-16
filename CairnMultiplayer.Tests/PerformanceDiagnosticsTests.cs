using System.Text.Json;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PerformanceDiagnosticsTests
{
    [Fact]
    public void WarmupIsExcludedAndScopesBelongToTheFollowingBoundary()
    {
        var c = new PerformanceCapture(0, 1000, 30, 180, 10);
        c.Advance(29999);
        c.AddTicks(PerformanceArea.Network, 900);
        Assert.False(c.IsRecording);
        c.Advance(30000);
        c.AddTicks(PerformanceArea.Network, 2);
        c.AddTicks(PerformanceArea.Network, 3);
        c.Advance(30016);
        c.Advance(30036);
        Assert.Equal(2, c.Count);
        Assert.Equal(16, c.FrameMilliseconds[0]);
        Assert.Equal(20, c.FrameMilliseconds[1]);
        Assert.Equal(5, c.AreaTicks[(int)PerformanceArea.Network]);
        Assert.Equal(0, c.AreaTicks[PerformanceCapture.AreaCount + (int)PerformanceArea.Network]);
    }

    [Fact]
    public void LongFinalStallIsNotClippedToTheRequestedDuration()
    {
        var c = new PerformanceCapture(0, 1000, 0, 1, 10);
        c.Advance(0);
        c.Advance(990);
        c.Advance(1250);
        Assert.True(c.IsComplete);
        Assert.Equal("duration-complete", c.StopReason);
        Assert.Equal(260, c.FrameMilliseconds[1]);
        c.Advance(1500);
        Assert.Equal(2, c.Count);
    }

    [Fact]
    public void FullBufferStopsWithoutOverwritingOrSilentlyDroppingFrames()
    {
        var c = new PerformanceCapture(0, 1000, 0, 180, 2);
        c.Advance(0);
        c.Advance(10);
        c.Advance(30);
        c.Advance(60);
        Assert.Equal("buffer-capacity", c.StopReason);
        Assert.Equal(new double[] { 10, 20 }, c.FrameMilliseconds);
    }

    [Theory]
    [InlineData("manual-stop")]
    [InlineData("shutdown")]
    [InlineData("diagnostics-disabled")]
    public void EarlyStopDoesNotInventASampleOrChangeItsReason(string reason)
    {
        var c = new PerformanceCapture(0, 1000);
        c.Stop(500, reason);
        c.Stop(1000, "shutdown");
        c.Advance(40000);
        Assert.Equal(0, c.Count);
        Assert.Equal(reason, c.StopReason);
        Assert.Null(c.MeasurementStartSeconds);
    }

    [Fact]
    public void SceneEventsSurviveTransitionsAndTheirOverflowIsExplicit()
    {
        var c = new PerformanceCapture(0, 1000, 0, 1, 10);
        c.Advance(0);
        c.AddEvent(10, "scene-unloaded", "A");
        c.AddEvent(20, "scene-loaded", "B");
        for (var i = 0; i < PerformanceCapture.EventCapacity; i++) c.AddEvent(30, "scene-loaded", "overlay");
        c.Advance(1100);
        Assert.Equal("B", c.Events[1].Value);
        Assert.Equal(2, c.DroppedEvents);
        Assert.Equal(1100, c.FrameMilliseconds[0]);
        Assert.Equal("duration-complete", c.StopReason);
    }

    [Fact]
    public void SamplingAndScopeAccumulationAllocateNoManagedMemory()
    {
        var c = new PerformanceCapture(0, 1000, 0, 180, 10000);
        c.Advance(0);
        // Warm the exact paths before measuring allocations, not JIT/startup work.
        c.AddTicks(PerformanceArea.Features, 1);
        c.Advance(1);
        using (c.Measure(PerformanceArea.Network)) { }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 2; i < 10000; i++)
        {
            using (c.Measure(PerformanceArea.Network)) c.AddTicks(PerformanceArea.Features, 1);
            c.Advance(i);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void StatisticsUseNearestRankAndRatioOfTotalsRatherThanMeanInstantaneousFps()
    {
        var s = TimingSummary.Calculate(new double[] { 10, 20, 40, 50, 100, 200 });
        Assert.Equal(40, s.MedianMs);
        Assert.Equal(200, s.P95Ms);
        Assert.Equal(200, s.P99Ms);
        Assert.Equal(6000.0 / 420, s.AverageFps.Value, 8);
        Assert.Equal(4, s.Over33Point3Ms);
        Assert.Equal(2, s.Over50Ms);
        Assert.Equal(1, s.Over100Ms);
        Assert.Null(TimingSummary.Calculate(Array.Empty<double>()).AverageFps);
        Assert.Null(TimingSummary.Calculate(new double[] { 0 }).AverageFps);
        Assert.Throws<ArgumentException>(() => TimingSummary.Calculate(new[] { double.NaN }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReportsAreReadableForEmptyAndSceneTransitionCaptures(bool collect)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cairnmp-performance-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var c = new PerformanceCapture(0, 1000, 0, 180, 10);
            if (collect)
            {
                c.Advance(0);
                c.AddTicks(PerformanceArea.Ui, 4);
                c.AddEvent(5, "scene-loaded", "B|<test>\nnext");
                c.Advance(20);
            }
            c.Stop(30, "manual-stop");
            var path = PerformanceReport.Write(dir, c, new() { ["StartSession"] = "solo" });
            using var report = JsonDocument.Parse(File.ReadAllText(path + ".json"));
            Assert.Equal(collect ? 1 : 0, report.RootElement.GetProperty("Frames").GetProperty("Samples").GetInt32());
            Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("Areas").GetProperty("Ui").GetProperty("AverageFps").ValueKind);
            Assert.Contains("not display presents", File.ReadAllText(path + ".md"));
            if (collect) Assert.Contains("B\\|&lt;test&gt; next", File.ReadAllText(path + ".md"));
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(dir)));
            Assert.StartsWith("cairnmp-performance-test-", Path.GetFileName(dir));
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
