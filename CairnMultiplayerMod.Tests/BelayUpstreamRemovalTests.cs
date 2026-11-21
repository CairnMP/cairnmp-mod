using System.Numerics;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayUpstreamRemovalTests
{
    private static readonly BelayProtectionPoint[] Before = { new(1, Vector3.Zero), new(2, Vector3.UnitY), new(3, Vector3.One) };
    private static readonly BelayProtectionPoint[] After = { Before[1], Before[2] };
    private static readonly long[] BeforePins = { 100, 101, 101, 102, 102, 103, 103, 104 };
    private static readonly long[] AfterPins = { 100, 102, 102, 103, 103, 104 };
    private static BelayTopologySnapshot Old() => new(10, 30, new long[] { 11, 12, 13, 14 }, new long[] { 20, 21, 22, 23, 24 }, 30, 3);
    private static BelayTopologySnapshot New(long tailPart = 14, long harness = 24)
        => new(10, 30, new long[] { 31, 13, tailPart }, new long[] { 20, 22, 23, harness }, 32, 2);

    [Fact]
    public void RemovingAnOlderPitonBeforeTheStationRetainsTheProtectedPhysicalSuffix()
    {
        var tracker = new BelayTopologyTracker(); tracker.Observe(Old(), false, 0);
        Assert.Equal(BelayTopologyChange.Settling, tracker.ObserveUpstreamRemoval(New(), Before, After, 2, BeforePins, AfterPins, false, 1));
        Assert.Equal(BelayTopologyChange.Rebound, tracker.ObserveUpstreamRemoval(New(), Before, After, 2, BeforePins, AfterPins, false, 1.2));
        Assert.Equal(2u, tracker.Revision); Assert.Equal(2, tracker.Current.ProtectionCount);
    }

    [Fact]
    public void LoadedRemovalCannotRebindEvenIfTheProtectedSuffixMatches()
    {
        var tracker = new BelayTopologyTracker(); tracker.Observe(Old(), false, 0);
        Assert.Equal(BelayTopologyChange.Invalid, tracker.ObserveUpstreamRemoval(New(), Before, After, 2, BeforePins, AfterPins, true, 1));
        Assert.Equal(3, tracker.Current.ProtectionCount);
    }

    [Theory]
    [InlineData(99, 24)]
    [InlineData(14, 99)]
    public void AChangedProtectedPartOrHarnessCannotBeAccepted(long part, long harness)
    {
        var tracker = new BelayTopologyTracker(); tracker.Observe(Old(), false, 0);
        foreach (var time in new[] { 1d, 2d })
            Assert.Equal(BelayTopologyChange.Settling, tracker.ObserveUpstreamRemoval(New(part, harness), Before, After, 2, BeforePins, AfterPins, false, time));
        Assert.Equal(1u, tracker.Revision);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void MissingStationOrAChangedDownstreamWalkCannotUseUpstreamRecovery(uint station)
    {
        var tracker = new BelayTopologyTracker(); tracker.Observe(Old(), false, 0);
        var points = station == 1 ? After : new[] { Before[0], Before[1] };
        foreach (var time in new[] { 1d, 2d })
            Assert.Equal(BelayTopologyChange.Settling, tracker.ObserveUpstreamRemoval(New(), Before, points, station, BeforePins, AfterPins, false, time));
        Assert.Equal(3, tracker.Current.ProtectionCount);
    }

    [Fact]
    public void APhysicalPinChangeResetsTheSettlingInterval()
    {
        var tracker = new BelayTopologyTracker(); tracker.Observe(Old(), false, 0);
        tracker.ObserveUpstreamRemoval(New(), Before, After, 2, BeforePins, AfterPins, false, 1);
        var changed = (long[])AfterPins.Clone(); changed[^1] = 999;
        Assert.Equal(BelayTopologyChange.Settling, tracker.ObserveUpstreamRemoval(New(), Before, After, 2, BeforePins, changed, false, 1.2));
        Assert.Equal(BelayTopologyChange.Settling, tracker.ObserveUpstreamRemoval(New(), Before, After, 2, BeforePins, AfterPins, false, 2));
        Assert.Equal(BelayTopologyChange.Rebound, tracker.ObserveUpstreamRemoval(New(), Before, After, 2, BeforePins, AfterPins, false, 2.2));
    }
}
