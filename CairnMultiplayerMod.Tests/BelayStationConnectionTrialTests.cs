using System;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayStationConnectionTrialTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PhysicalPinReadbackMustAgreeWithTheLogicalStationState(bool attached)
    {
        Assert.Equal(attached, BelayStationConnectionTrial.PinsAgree(10, 20, new long[] { 100, 10, 10, 20, 20, 200 }, attached));
        Assert.Equal(!attached, BelayStationConnectionTrial.PinsAgree(10, 20, new long[] { 100, 20, 20, 200 }, attached));
    }

    [Fact]
    public void OneStaleStationPinCannotPassAsFullyDetachedOrFullyAttached()
    {
        Assert.False(BelayStationConnectionTrial.PinsAgree(10, 20, new long[] { 100, 10, 20, 20, 200 }, true));
        Assert.False(BelayStationConnectionTrial.PinsAgree(10, 20, new long[] { 100, 10, 20, 20, 200 }, false));
        Assert.False(BelayStationConnectionTrial.PinsAgree(10, 20, new long[] { 100, 10, 10, 20, 200 }, true));
    }

    [Fact]
    public void NativeQuickdrawEndRequiresTwoActualPinOccurrences()
    {
        Assert.True(BelayStationConnectionTrial.PinsAgree(10, 20, new long[] { 10, 10, 20, 20 }, true));
        Assert.False(BelayStationConnectionTrial.PinsAgree(10, 20, new long[] { 10, 20, 20 }, true));
        Assert.False(BelayStationConnectionTrial.PinsAgree(10, 20, new long[] { 10, 10, 20 }, true));
        Assert.False(BelayStationConnectionTrial.PinsAgree(10, 20, new long[] { 20 }, false));
    }

    [Fact]
    public void OtherQuickdrawFieldsCannotSubstituteForTheNativeAttachmentEnd()
    {
        Assert.False(BelayStationConnectionTrial.PinsAgree(10, 20, new long[] { 100, 11, 11, 21, 21, 200 }, true));
        Assert.False(BelayStationConnectionTrial.PinsAgree(10, 20, new long[] { 100, 10, 10, 21, 21, 200 }, true));
    }

    [Fact]
    public void MissingAmbiguousAndUnboundedPinReadbackIsRejected()
    {
        Assert.False(BelayStationConnectionTrial.PinsAgree(0, 20, new long[] { 20, 20 }, true));
        Assert.False(BelayStationConnectionTrial.PinsAgree(10, 10, new long[] { 10, 10, 10, 10 }, true));
        Assert.False(BelayStationConnectionTrial.PinsAgree(10, 20, null, true));
        Assert.False(BelayStationConnectionTrial.PinsAgree(10, 20, new long[129], true));
    }

    [Fact]
    public void LogicalRemovalWithoutMatchingPhysicalPinsCannotEnableReconnection()
    {
        var trial = NewTrial();
        trial.Observe(Removed(), new long[] { 2 }, false, true, true, true, false, .1);
        trial.Observe(Removed(), new long[] { 2 }, false, true, true, true, false, .5);
        Assert.Equal(StationConnectionStage.Unclipping, trial.Stage);
        Assert.False(trial.RequestConnection(.6));
    }

    [Fact]
    public void PassRequiresObservedDisconnectionThenAStableNativeReconnection()
    {
        var trial = Detached();
        Assert.True(trial.RequestConnection(.31));
        Observe(trial, Connected(), new long[] { 1, 2 }, true, .4);
        Assert.Equal(StationConnectionStage.Connecting, trial.Stage);
        Observe(trial, Connected(), new long[] { 1, 2 }, true, .6);
        Assert.Equal(StationConnectionStage.Confirming, trial.Stage);
        for (var i = 1; i <= 10; i++) Observe(trial, Connected(), new long[] { 1, 2 }, true, .6 + i * .5);
        Assert.Equal(StationConnectionStage.Passed, trial.Stage);
        Assert.False(trial.Active);
        Assert.Equal(5, trial.ConfirmedSeconds);
        trial.Fail(StationConnectionFailure.Context);
        Assert.Equal(StationConnectionStage.Passed, trial.Stage);
    }

    [Fact]
    public void AnAlreadyConnectedStationCannotMasqueradeAsACompletedConnectionTrial()
    {
        var trial = NewTrial();
        Observe(trial, Before(), new long[] { 1, 2 }, true, .5);
        Assert.False(trial.RequestConnection(.6));
        Observe(trial, Before(), new long[] { 1, 2 }, true, 2.9);
        Assert.Equal(StationConnectionStage.Unclipping, trial.Stage);
        Observe(trial, Before(), new long[] { 1, 2 }, true, 3.01);
        Assert.Equal(StationConnectionFailure.Timeout, trial.Failure);
    }

    [Fact]
    public void ASecondPressCannotReissueAQueuedRequest()
    {
        var trial = NewTrial();
        Assert.False(trial.RequestConnection(.01));
        Observe(trial, Removed(), new long[] { 2 }, false, .1);
        Assert.False(trial.RequestConnection(.2));
        Observe(trial, Removed(), new long[] { 2 }, false, .3);
        Assert.True(trial.RequestConnection(.31));
        Assert.False(trial.RequestConnection(.32));
    }

    [Theory]
    [InlineData(false, true, true, (int)StationConnectionFailure.BackupLost)]
    [InlineData(true, false, true, (int)StationConnectionFailure.StationLost)]
    [InlineData(true, true, false, (int)StationConnectionFailure.Context)]
    public void LostAnchorStationOrContextImmediatelyStopsTheTrial(bool backup, bool station, bool context, int failure)
    {
        var trial = Detached();
        trial.Observe(null, null, false, backup, station, context, false, .4);
        Assert.Equal(StationConnectionStage.Failed, trial.Stage);
        Assert.Equal((StationConnectionFailure)failure, trial.Failure);
        Assert.False(trial.RequestConnection(.5));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    public void InvalidOrBackwardObservationTimeCannotApproveOrContinue(double now)
    {
        var trial = NewTrial();
        Observe(trial, Before(), new long[] { 1, 2 }, true, now);
        Assert.Equal(StationConnectionFailure.Context, trial.Failure);
    }

    [Fact]
    public void ReplacedNativeRopeCannotReuseTheOriginalStationIdentity()
    {
        var trial = Detached();
        var replaced = new BelayTopologySnapshot(999, 2, new long[] { 20, 21 }, new long[] { 100, 2, 101, 102 }, 30, 1);
        Observe(trial, replaced, new long[] { 2 }, false, .4);
        Assert.Equal(StationConnectionFailure.RouteChanged, trial.Failure);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 1)]
    [InlineData(1, 3)]
    public void ReorderedAmbiguousOrDifferentPitonsCannotConfirmTheRoute(long first, long second)
    {
        var trial = Detached();
        Assert.True(trial.RequestConnection(.31));
        Observe(trial, Connected(), new[] { first, second }, true, .4);
        Assert.Equal(StationConnectionFailure.RouteChanged, trial.Failure);
    }

    [Fact]
    public void TemporaryUnclipTopologyCannotBecomeTheConfirmedDetachedRoute()
    {
        var trial = NewTrial();
        var incomplete = new BelayTopologySnapshot(1000, 2, new long[] { 20, 21, 22 }, new long[] { 100, 2, 101, 102 }, 30, 1);
        Observe(trial, incomplete, new long[] { 2 }, false, .1);
        Observe(trial, incomplete, new long[] { 2 }, false, .5);
        Assert.Equal(StationConnectionStage.Unclipping, trial.Stage);
        Observe(trial, Removed(), new long[] { 2 }, false, .6);
        Assert.Equal(StationConnectionStage.Unclipping, trial.Stage);
        Observe(trial, Removed(), new long[] { 2 }, false, .8);
        Assert.Equal(StationConnectionStage.Detached, trial.Stage);
    }

    [Fact]
    public void MissingSnapshotResetsSettlingRatherThanPreservingOldCandidateTime()
    {
        var trial = NewTrial();
        Observe(trial, Removed(), new long[] { 2 }, false, .1);
        Observe(trial, null, Array.Empty<long>(), false, .2);
        Observe(trial, Removed(), new long[] { 2 }, false, .3);
        Assert.Equal(StationConnectionStage.Unclipping, trial.Stage);
        Observe(trial, Removed(), new long[] { 2 }, false, .5);
        Assert.Equal(StationConnectionStage.Detached, trial.Stage);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ConnectionRequiresActualAttachmentAndMeasuredUsableStationRoute(bool attached, bool measured)
    {
        var trial = Detached();
        Assert.True(trial.RequestConnection(.31));
        trial.Observe(Connected(), new long[] { 1, 2 }, attached, true, true, true, measured, .5);
        trial.Observe(Connected(), new long[] { 1, 2 }, attached, true, true, true, measured, 1);
        Assert.Equal(StationConnectionStage.Connecting, trial.Stage);
        Observe(trial, Connected(), new long[] { 1, 2 }, true, 3.32);
        Assert.Equal(StationConnectionFailure.Timeout, trial.Failure);
    }

    [Fact]
    public void IncompleteReclipPartsCannotStartTheFiveSecondConfirmation()
    {
        var trial = Detached();
        Assert.True(trial.RequestConnection(.31));
        var incomplete = new BelayTopologySnapshot(1000, 2, new long[] { 30, 31, 32, 33 }, new long[] { 100, 1, 2, 101, 102, 103 }, 30, 2);
        Observe(trial, incomplete, new long[] { 1, 2 }, true, .5);
        Observe(trial, incomplete, new long[] { 1, 2 }, true, .9);
        Assert.Equal(StationConnectionStage.Connecting, trial.Stage);
        Observe(trial, Connected(), new long[] { 1, 2 }, true, 1);
        Observe(trial, Connected(), new long[] { 1, 2 }, true, 1.2);
        Assert.Equal(StationConnectionStage.Confirming, trial.Stage);
        Assert.Equal(0, trial.ConfirmedSeconds);
    }

    [Fact]
    public void ObservationGapRestartsConfirmationAndCannotCountAnUnobservedInterval()
    {
        var trial = Confirming();
        Observe(trial, Connected(), new long[] { 1, 2 }, true, 1);
        Observe(trial, Connected(), new long[] { 1, 2 }, true, 5);
        Assert.Equal(StationConnectionStage.Confirming, trial.Stage);
        Assert.Equal(0, trial.ConfirmedSeconds);
        for (var i = 1; i <= 10; i++) Observe(trial, Connected(), new long[] { 1, 2 }, true, 5 + i * .5);
        Assert.Equal(StationConnectionStage.Passed, trial.Stage);
    }

    [Fact]
    public void LosingConnectionAfterSettlingFailsInsteadOfRetainingAnOldGreenOutcome()
    {
        var trial = Confirming();
        Observe(trial, Removed(), new long[] { 2 }, false, 1);
        Assert.Equal(StationConnectionFailure.RouteChanged, trial.Failure);
        Observe(trial, Connected(), new long[] { 1, 2 }, true, 10);
        Assert.Equal(StationConnectionStage.Failed, trial.Stage);
    }

    [Fact]
    public void AFailedDetachedTrialCanRetryOnlyTheSameMissingStationRoute()
    {
        var trial = Detached();
        trial.Fail(StationConnectionFailure.NativeRejected);
        Assert.False(trial.ResumeDetached(Connected(), new long[] { 1, 2 }, true, 1));
        Assert.False(trial.ResumeDetached(Removed(), new long[] { 3 }, false, 1));
        Assert.True(trial.ResumeDetached(Removed(), new long[] { 2 }, false, 1));
        Assert.Equal(StationConnectionStage.Detached, trial.Stage);
        Assert.Equal(StationConnectionFailure.None, trial.Failure);
        Assert.True(trial.RequestConnection(1.1));
    }

    [Fact]
    public void DetachedUserConfirmationAndTheEntireAttemptHaveBoundedLifetimes()
    {
        var trial = Detached();
        Observe(trial, Removed(), new long[] { 2 }, false, 30.31);
        Assert.Equal(StationConnectionFailure.Timeout, trial.Failure);
        var waiting = Confirming();
        Observe(waiting, Connected(), new long[] { 1, 2 }, true, 60.01);
        Assert.Equal(StationConnectionFailure.Timeout, waiting.Failure);
    }

    [Fact]
    public void InvalidInitialRouteCannotAuthorizeAnUnclipRequest()
    {
        Assert.Throws<ArgumentException>(() => new BelayStationConnectionTrial(null, 1, 2, 0));
        Assert.Throws<ArgumentException>(() => new BelayStationConnectionTrial(Before(), 2, 2, 0));
        Assert.Throws<ArgumentException>(() => new BelayStationConnectionTrial(Before(), 1, 3, 0));
        Assert.Throws<ArgumentException>(() => new BelayStationConnectionTrial(Before(), 1, 2, double.NaN));
    }

    private static BelayStationConnectionTrial NewTrial() => new(Before(), 1, 2, 0);
    private static BelayStationConnectionTrial Detached()
    {
        var trial = NewTrial();
        Observe(trial, Removed(), new long[] { 2 }, false, .1);
        Observe(trial, Removed(), new long[] { 2 }, false, .3);
        Assert.Equal(StationConnectionStage.Detached, trial.Stage);
        return trial;
    }
    private static BelayStationConnectionTrial Confirming()
    {
        var trial = Detached(); trial.RequestConnection(.31);
        Observe(trial, Connected(), new long[] { 1, 2 }, true, .4);
        Observe(trial, Connected(), new long[] { 1, 2 }, true, .6);
        Assert.Equal(StationConnectionStage.Confirming, trial.Stage);
        return trial;
    }
    private static void Observe(BelayStationConnectionTrial trial, BelayTopologySnapshot snapshot, long[] order, bool attached, double now)
        => trial.Observe(snapshot, order, attached, true, true, true, true, now);
    private static BelayTopologySnapshot Before() => new(1000, 2, new long[] { 10, 11, 12 }, new long[] { 100, 1, 2, 101, 102 }, 30, 2);
    private static BelayTopologySnapshot Removed() => new(1000, 2, new long[] { 20, 21 }, new long[] { 100, 2, 101, 102 }, 30, 1);
    private static BelayTopologySnapshot Connected() => new(1000, 2, new long[] { 30, 31, 32 }, new long[] { 100, 1, 2, 101, 102 }, 30, 2);
}
