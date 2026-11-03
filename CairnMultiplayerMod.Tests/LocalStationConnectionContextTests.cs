using CairnMultiplayerMod.GameApi;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class LocalStationConnectionContextTests
{
    [Theory]
    [InlineData((int)StationConnectionStage.NotReady)]
    [InlineData((int)StationConnectionStage.Passed)]
    [InlineData((int)StationConnectionStage.Failed)]
    public void PreparationRefusalAndStoredResultsDoNotClaimAnActiveNativeTransition(int stage)
    {
        var status = new LocalStationConnectionStatus((StationConnectionStage)stage,
            StationConnectionFailure.NativePinsMismatch, 0, "Physical pin readback differs.");
        Assert.False(status.Active);
    }

    [Theory]
    [InlineData((int)StationConnectionStage.Unclipping)]
    [InlineData((int)StationConnectionStage.Detached)]
    [InlineData((int)StationConnectionStage.Connecting)]
    [InlineData((int)StationConnectionStage.Confirming)]
    public void IssuedNativeTransitionAndHeldDetachedRouteRemainActive(int stage)
    {
        var status = new LocalStationConnectionStatus((StationConnectionStage)stage, StationConnectionFailure.None, 0, "");
        Assert.True(status.Active);
    }

    [Fact]
    public void OfflineStoryNeedsNoMultiplayerPlayerStateToShowPreparationOrAllowTheTrial()
    {
        var context = new LocalStationConnectionContext(true, true, true);
        Assert.True(context.CanRun);
        Assert.True(context.ShowPanel(false, false));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void DisabledNetworkOrNonGameplayContextsCannotStartTheTrial(bool enabled, bool offline, bool nativeInGame)
    {
        var context = new LocalStationConnectionContext(enabled, offline, nativeInGame);
        Assert.False(context.CanRun);
        Assert.False(context.ShowPanel(false, false));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RecordedStationOrResultRemainsVisibleWhenNativeGameplayIsNoLongerActive(bool station, bool result)
    {
        var context = new LocalStationConnectionContext(true, true, false);
        Assert.True(context.ShowPanel(station, result));
        Assert.False(context.CanRun);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void StoredResultsDoNotExposeTheSoloPanelWhenDisabledOrInMultiplayer(bool enabled, bool offline)
    {
        var context = new LocalStationConnectionContext(enabled, offline, true);
        Assert.False(context.ShowPanel(true, true));
        Assert.False(context.CanRun);
    }
}
