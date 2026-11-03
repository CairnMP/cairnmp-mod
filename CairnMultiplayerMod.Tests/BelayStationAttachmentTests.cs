using System;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayStationAttachmentTests
{
    [Fact]
    public void NewStationMustRetainTheOriginalRouteAndFiveObservedSecondsOfPhysicalPins()
    {
        var trial = New();
        Observe(trial, .1);
        Assert.Equal(BelayStationAttachmentStage.Connecting, trial.Stage);
        Observe(trial, .3);
        Assert.Equal(BelayStationAttachmentStage.Confirming, trial.Stage);
        for (var i = 1; i < 10; i++) Observe(trial, .3 + i * .5);
        Assert.NotEqual(BelayStationAttachmentStage.Ready, trial.Stage);
        Observe(trial, 5.31);
        Assert.Equal(BelayStationAttachmentStage.Ready, trial.Stage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LogicalInsertionOrUnchangedOriginalRouteAloneCannotPass(bool inserted)
    {
        var trial = New();
        trial.Observe(inserted ? After() : Before(), inserted ? new long[] { 10, 20 } : new long[] { 20 }, false, .1);
        trial.Observe(inserted ? After() : Before(), inserted ? new long[] { 10, 20 } : new long[] { 20 }, false, 3.01);
        Assert.Equal(StationConnectionFailure.Timeout, trial.Failure);
    }

    [Theory]
    [InlineData(20, 10)]
    [InlineData(10, 10)]
    [InlineData(10, 21)]
    public void AppendedStationDuplicatedStationOrReplacedProtectionCannotPass(long first, long last)
    {
        var trial = New();
        trial.Observe(After(), new[] { first, last }, true, .1);
        Assert.Equal(StationConnectionFailure.RouteChanged, trial.Failure);
    }

    [Theory]
    [InlineData(2000, 20)]
    [InlineData(1000, 10)]
    public void ReplacementRopeOrLastBackupIsRejected(long rope, long last)
    {
        var trial = New();
        trial.Observe(new BelayTopologySnapshot(rope, last, new long[] { 30, 31, 32 }, new long[] { 900, 100, 200, 901 }, 30, 2),
            new long[] { 10, 20 }, true, .1);
        Assert.Equal(StationConnectionFailure.RouteChanged, trial.Failure);
    }

    [Fact]
    public void SameCountsDoNotHideReplacedOriginalNonPitonHolders()
    {
        var trial = New();
        var corrupted = new BelayTopologySnapshot(1000, 20, new long[] { 30, 31, 32 }, new long[] { 999, 100, 200, 901 }, 30, 2);
        trial.Observe(corrupted, new long[] { 10, 20 }, true, .1);
        trial.Observe(corrupted, new long[] { 10, 20 }, true, 3.01);
        Assert.Equal(StationConnectionFailure.Timeout, trial.Failure);
    }

    [Fact]
    public void MissingReadbackRestartsConfirmation()
    {
        var trial = New();
        Observe(trial, .1); Observe(trial, .3);
        trial.Observe(null, null, false, .8);
        for (var i = 0; i < 10; i++) Observe(trial, 1 + i * .5);
        Assert.NotEqual(BelayStationAttachmentStage.Ready, trial.Stage);
        Observe(trial, 6.01);
        Assert.Equal(BelayStationAttachmentStage.Ready, trial.Stage);
    }

    [Fact]
    public void ALongFrameGapCannotCountAsObservedConfirmation()
    {
        var trial = New();
        Observe(trial, .1); Observe(trial, .3); Observe(trial, 2);
        for (var i = 1; i < 10; i++) Observe(trial, 2 + i * .5);
        Assert.NotEqual(BelayStationAttachmentStage.Ready, trial.Stage);
        Observe(trial, 7.01);
        Assert.Equal(BelayStationAttachmentStage.Ready, trial.Stage);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidTimeCannotApproveNativeAttachment(double now)
    {
        var trial = New(); Observe(trial, now);
        Assert.Equal(StationConnectionFailure.Context, trial.Failure);
    }

    [Fact]
    public void LostContextCannotResumeAfterCancellation()
    {
        var trial = New(); trial.Fail(StationConnectionFailure.Context);
        for (var i = 1; i < 15; i++) Observe(trial, i * .5);
        Assert.Equal(BelayStationAttachmentStage.Failed, trial.Stage);
    }

    [Fact]
    public void OriginalSnapshotIsCapturedRatherThanBorrowed()
    {
        var before = Before();
        var trial = new BelayStationAttachment(before, new long[] { 20 }, 10, 100, 0);
        before.Holders[0] = 999;
        for (var i = 1; i <= 13; i++) Observe(trial, i * .5);
        Assert.Equal(BelayStationAttachmentStage.Ready, trial.Stage);
    }

    [Fact]
    public void AlreadyAttachedOrRepeatedOriginalPitonsCannotStartAnInitialAttachment()
    {
        Assert.Throws<ArgumentException>(() => new BelayStationAttachment(Before(), new long[] { 10 }, 10, 100, 0));
        Assert.Throws<ArgumentException>(() => new BelayStationAttachment(Before(), new long[] { 20 }, 10, 200, 0));
    }

    private static BelayStationAttachment New() => new(Before(), new long[] { 20 }, 10, 100, 0);
    private static BelayTopologySnapshot Before() => new(1000, 20, new long[] { 11, 12 }, new long[] { 900, 200, 901 }, 30, 1);
    private static BelayTopologySnapshot After() => new(1000, 20, new long[] { 30, 31, 32 }, new long[] { 900, 100, 200, 901 }, 30, 2);
    private static void Observe(BelayStationAttachment trial, double now) => trial.Observe(After(), new long[] { 10, 20 }, true, now);
}
