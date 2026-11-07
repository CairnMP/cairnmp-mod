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
    public void InconsistentTerminalStationDuplicatedStationOrReplacedProtectionCannotPass(long first, long last)
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

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void AnchoredRolePreparationAcceptsAnUpperStationAfterAllOriginalProtections(int protections)
    {
        var (before, after, original, connected) = TerminalInsertion(protections);
        var trial = new BelayStationAttachment(before, original, 10, 100, 0);
        trial.Observe(after, connected, true, .1);
        trial.Observe(after, connected, true, .3);
        for (var i = 1; i < 10; i++) trial.Observe(after, connected, true, .3 + i * .5);
        Assert.Equal(BelayStationAttachmentStage.Confirming, trial.Stage);
        trial.Observe(after, connected, true, 5.31);
        Assert.Equal(BelayStationAttachmentStage.Ready, trial.Stage);
        // A ready transaction may wait for the other endpoint, but cannot lose
        // its original protections or physical pins before commitment.
        trial.Observe(after, connected, true, 11);
        Assert.Equal(BelayStationAttachmentStage.Ready, trial.Stage);
        trial.Observe(after, connected, false, 11.1);
        Assert.Equal(StationConnectionFailure.RouteChanged, trial.Failure);
    }

    [Fact]
    public void InitialConnectionAcceptsTheObservedTerminalInsertionOnlyAfterFiveSecondsOfPins()
    {
        // R35 retained P1/P2/P3 and appended S immediately before the harness;
        // the first invitation rejected this route, while a retry accepted it.
        var (before, after, original, connected) = TerminalInsertion(3);
        var trial = new BelayStationAttachment(before, original, 10, 100, 0);
        trial.Observe(before, original, true, .01);
        trial.Observe(after, connected, false, .1);
        trial.Observe(after, connected, true, .2);
        trial.Observe(after, connected, true, .4);
        for (var i = 1; i <= 9; i++) trial.Observe(after, connected, true, .4 + i * .5);
        Assert.Equal(BelayStationAttachmentStage.Confirming, trial.Stage);
        trial.Observe(after, connected, true, 5.41);
        Assert.Equal(BelayStationAttachmentStage.Ready, trial.Stage);
        Assert.Equal(StationConnectionFailure.None, trial.Failure);
    }

    [Fact]
    public void TerminalRoleStationRequiresItsNativeLastPitonAndRetainedOriginalOrder()
    {
        var (before, after, original, connected) = TerminalInsertion(3);
        var wrongLast = new BelayStationAttachment(before, original, 10, 100, 0);
        wrongLast.Observe(new(after.Rope, before.LastPiton, after.Parts, after.Holders, 30, after.ProtectionCount), connected, true, .1);
        Assert.Equal(StationConnectionFailure.RouteChanged, wrongLast.Failure);
        var reordered = new BelayStationAttachment(before, original, 10, 100, 0);
        reordered.Observe(after, new long[] { 20, 22, 21, 10 }, true, .1);
        Assert.Equal(StationConnectionFailure.RouteChanged, reordered.Failure);
        var repeated = new BelayStationAttachment(before, original, 10, 100, 0);
        repeated.Observe(after, new long[] { 20, 10, 22, 10 }, true, .1);
        Assert.Equal(StationConnectionFailure.RouteChanged, repeated.Failure);
    }

    [Theory]
    [InlineData(999, 200, 100, 901)] // replaced root
    [InlineData(900, 200, 100, 999)] // replaced harness
    [InlineData(900, 100, 200, 901)] // station holder disagrees with piton order
    [InlineData(900, 200, 901, 100)] // station outside the original endpoints
    public void TerminalRoleStationCannotApproveADifferentPhysicalWalk(long root, long a, long b, long end)
    {
        var (before, after, original, connected) = TerminalInsertion(1);
        var trial = new BelayStationAttachment(before, original, 10, 100, 0);
        var corrupt = new BelayTopologySnapshot(after.Rope, 10, after.Parts, new[] { root, a, b, end }, 30, 2);
        for (var i = 1; i <= 13; i++) trial.Observe(corrupt, connected, true, i * .5);
        Assert.Equal(BelayStationAttachmentStage.Failed, trial.Stage);
    }

    [Fact]
    public void ReadyHeadInsertionIsRevalidatedUntilTheRoleTransactionCommits()
    {
        var trial = New();
        for (var i = 1; i <= 13; i++) Observe(trial, i * .5);
        Assert.Equal(BelayStationAttachmentStage.Ready, trial.Stage);
        var moved = new BelayTopologySnapshot(1000, 20, new long[] { 30, 31, 33 }, After().Holders, 30, 2);
        trial.Observe(moved, new long[] { 10, 20 }, true, 6.6);
        Assert.Equal(StationConnectionFailure.RouteChanged, trial.Failure);
    }

    private static (BelayTopologySnapshot Before, BelayTopologySnapshot After, long[] Original, long[] Connected)
        TerminalInsertion(int protections)
    {
        var original = new long[protections]; var connected = new long[protections + 1];
        var holders = new long[protections + 2]; var nextHolders = new long[protections + 3];
        var parts = new long[protections + 1]; var nextParts = new long[protections + 2];
        holders[0] = nextHolders[0] = 900;
        for (var i = 0; i < protections; i++)
        {
            original[i] = connected[i] = 20 + i;
            holders[i + 1] = nextHolders[i + 1] = 200 + i;
        }
        for (var i = 0; i < parts.Length; i++) parts[i] = 30 + i;
        for (var i = 0; i < nextParts.Length; i++) nextParts[i] = 40 + i;
        connected[^1] = 10; holders[^1] = nextHolders[^1] = 901; nextHolders[^2] = 100;
        return (new(1000, original[^1], parts, holders, 30, protections),
            new(1000, 10, nextParts, nextHolders, 30, protections + 1), original, connected);
    }

    private static BelayStationAttachment New() => new(Before(), new long[] { 20 }, 10, 100, 0);
    private static BelayTopologySnapshot Before() => new(1000, 20, new long[] { 11, 12 }, new long[] { 900, 200, 901 }, 30, 1);
    private static BelayTopologySnapshot After() => new(1000, 20, new long[] { 30, 31, 32 }, new long[] { 900, 100, 200, 901 }, 30, 2);
    private static void Observe(BelayStationAttachment trial, double now) => trial.Observe(After(), new long[] { 10, 20 }, true, now);
}
