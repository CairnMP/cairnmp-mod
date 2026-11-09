using System;
using CairnMultiplayerMod.Internal.Game;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayPatchRetentionTests
{
    [Fact]
    public void NativeLossAllowsTheFeatureToDeclareRecoveryAfterTheAdapterTick()
    {
        var session = Guid.NewGuid();
        var retention = new BelayPatchRetention();
        retention.BeginSession(session, 1, 10);
        retention.RequestRelease(11);
        Assert.True(retention.KeepInstalled(1, 11.1));
        Assert.True(retention.TryRetain(session, 1, 11.1));
        Assert.True(retention.KeepInstalled(1, 13.099));
        Assert.False(retention.KeepInstalled(1, 13.1));
    }

    [Fact]
    public void UnconfirmedLossGetsOnlyOneQuarterSecondOfGrace()
    {
        var session = Guid.NewGuid();
        var retention = new BelayPatchRetention();
        retention.BeginSession(session, 1, 0);
        retention.RequestRelease(1);
        retention.RequestRelease(1.2); // Repeated reset cannot extend the grace.
        Assert.True(retention.KeepInstalled(1, 1.249));
        Assert.False(retention.KeepInstalled(1, 1.25));
        Assert.False(retention.TryRetain(session, 1, 1.25));
    }

    [Fact]
    public void RepeatedReadinessChecksCannotExtendTheEpisodeBeyondTwoMinutes()
    {
        var session = Guid.NewGuid();
        var retention = new BelayPatchRetention();
        retention.BeginSession(session, 1, 0);
        Assert.True(retention.TryRetain(session, 1, 1));
        retention.RequestRelease(1);
        for (var now = 2; now <= 120; now++)
            Assert.True(retention.TryRetain(session, 1, now));
        Assert.True(retention.KeepInstalled(1, 120.999));
        Assert.False(retention.TryRetain(session, 1, 121));
        Assert.False(retention.KeepInstalled(1, 121));
        Assert.False(retention.TryRetain(session, 1, 121.1));
    }

    [Fact]
    public void AStalledOrFinishedEpisodeCannotBeResurrectedFromItsOldIdentity()
    {
        var session = Guid.NewGuid();
        var retention = new BelayPatchRetention();
        retention.BeginSession(session, 1, 0);
        Assert.True(retention.TryRetain(session, 1, 1));
        retention.RequestRelease(1);
        Assert.False(retention.TryRetain(session, 1, 3));
        Assert.False(retention.TryRetain(session, 1, 3.1));
        Assert.False(retention.KeepInstalled(1, 3.1));
    }

    [Fact]
    public void ExplicitEndBeforePhysicalReleaseCancelsRenewalAndTheUpcomingGrace()
    {
        var session = Guid.NewGuid();
        var retention = new BelayPatchRetention();
        retention.BeginSession(session, 1, 0);
        Assert.True(retention.TryRetain(session, 1, 1));
        retention.Release();
        retention.RequestRelease(1.1);
        Assert.False(retention.KeepInstalled(1, 1.1));
        Assert.False(retention.TryRetain(session, 1, 1.1));
    }

    [Fact]
    public void ExplicitEndAfterPhysicalReleaseCancelsAnExistingGraceImmediately()
    {
        var retention = new BelayPatchRetention();
        retention.BeginSession(Guid.NewGuid(), 1, 0);
        retention.RequestRelease(1);
        Assert.True(retention.KeepInstalled(1, 1));
        retention.Release();
        Assert.False(retention.KeepInstalled(1, 1));
    }

    [Fact]
    public void NewAcquisitionAfterReleaseHasItsOwnGraceAndRejectsTheRetiredSession()
    {
        var previous = Guid.NewGuid();
        var next = Guid.NewGuid();
        var retention = new BelayPatchRetention();
        retention.BeginSession(previous, 1, 0);
        Assert.True(retention.TryRetain(previous, 1, 1));
        retention.RequestRelease(1);
        retention.Release(); // Release the old episode immediately before acquisition.
        retention.BeginSession(next, 1, 2);
        retention.RequestRelease(3);
        Assert.False(retention.TryRetain(previous, 1, 3));
        Assert.True(retention.KeepInstalled(1, 3));
        Assert.True(retention.TryRetain(next, 1, 3));
    }

    [Fact]
    public void SceneResetRejectsTheOldSessionEvenWhenTheSceneNumberReturns()
    {
        var session = Guid.NewGuid();
        var retention = new BelayPatchRetention();
        retention.BeginSession(session, 1, 0);
        Assert.True(retention.TryRetain(session, 1, 1));
        retention.RequestRelease(1);
        Assert.False(retention.KeepInstalled(2, 1.1));
        Assert.False(retention.TryRetain(session, 1, 1.2));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    public void InvalidOrBackwardClockCannotKeepInstalledPatches(double now)
    {
        var session = Guid.NewGuid();
        var retention = new BelayPatchRetention();
        retention.BeginSession(session, 1, 1);
        Assert.True(retention.TryRetain(session, 1, 1));
        retention.RequestRelease(1);
        Assert.False(retention.KeepInstalled(1, now));
        Assert.False(retention.TryRetain(session, 1, 2));
    }
}
