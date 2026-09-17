using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

/// <summary>
/// These counters decide whether anyone should rewrite native game code, so the arithmetic
/// they report has to be right — especially "ms per second", which is the figure that says
/// whether a repeated scan actually costs anything.
/// </summary>
public sealed class NativeProbeStatsTests
{
    // A tick is one millisecond at this frequency, which keeps the expectations readable.
    private const long Frequency = 1000;

    private static NativeProbeStats New() => new("probe", Frequency);

    [Fact]
    public void AFreshProbeReportsNothing()
    {
        var stats = New();

        Assert.Equal(0, stats.Calls);
        Assert.Equal(0, stats.TotalMilliseconds);
        Assert.Equal(0, stats.AverageMilliseconds);
        Assert.Equal(0, stats.MillisecondsPerSecond(10));
    }

    [Fact]
    public void CallsAndTotalsAccumulate()
    {
        var stats = New();
        stats.Add(2);
        stats.Add(3);
        stats.Add(5);

        Assert.Equal(3, stats.Calls);
        Assert.Equal(10, stats.TotalMilliseconds);
        Assert.Equal(10d / 3, stats.AverageMilliseconds, 6);
    }

    [Fact]
    public void TheWorstCallIsKept()
    {
        var stats = New();
        stats.Add(1);
        stats.Add(40);
        stats.Add(2);

        // A single 40 ms frame matters even when the average looks harmless.
        Assert.Equal(40, stats.MaxMilliseconds);
    }

    [Fact]
    public void MillisecondsPerSecondIsTheBudgetFigure()
    {
        var stats = New();
        for (var i = 0; i < 60; i++) stats.Add(1); // 1 ms per frame, 60 frames

        // 60 ms of work spread over 2 seconds is 30 ms/s — 3 % of a frame budget at 60 fps.
        Assert.Equal(30, stats.MillisecondsPerSecond(2), 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnImpossibleElapsedTimeDoesNotDivideByZero(double elapsed)
        => Assert.Equal(0, New().MillisecondsPerSecond(elapsed));

    [Fact]
    public void ANegativeSampleIsDiscardedRatherThanCorruptingTheTotal()
    {
        var stats = New();
        stats.Add(5);
        stats.Add(-100); // mismatched prefix/finalizer, or a wrapped timestamp

        Assert.Equal(1, stats.Calls);
        Assert.Equal(5, stats.TotalMilliseconds);
    }

    [Fact]
    public void ResetClearsEverythingForTheNextWindow()
    {
        var stats = New();
        stats.Add(7);
        stats.Reset();

        Assert.Equal(0, stats.Calls);
        Assert.Equal(0, stats.TotalMilliseconds);
        Assert.Equal(0, stats.MaxMilliseconds);
    }

    [Fact]
    public void TheDescriptionCarriesTheNumbersThatDrivetheDecision()
    {
        var stats = New();
        stats.Add(4);

        var text = stats.Describe(elapsedSeconds: 2);

        Assert.Contains("probe", text);
        Assert.Contains("1 calls", text);
        Assert.Contains("4.00 ms total", text);
        Assert.Contains("2.000 ms/s", text);
    }

    [Fact]
    public void TheDescriptionStaysCultureInvariant()
    {
        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            // A French locale would otherwise write "4,00" and break log parsing.
            System.Threading.Thread.CurrentThread.CurrentCulture =
                new System.Globalization.CultureInfo("fr-FR");

            var stats = New();
            stats.Add(4);

            Assert.Contains("4.00 ms total", stats.Describe(elapsedSeconds: 2));
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void AProbeNeedsANameAndARealClock()
    {
        Assert.Throws<ArgumentNullException>(() => new NativeProbeStats(null, Frequency));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeProbeStats("probe", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeProbeStats("probe", -1));
    }
}
