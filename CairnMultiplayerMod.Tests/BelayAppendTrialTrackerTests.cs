using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayAppendTrialTrackerTests
{
    private static BelayAppendTrialSample Climbing => new(true, true, true, 1, 1,
        false, false, false, true, 0, 0, true, true);

    [Fact]
    public void SetupShowsStationRecordingThenTheSecondPitonInstruction()
    {
        var guide = new BelayAppendTrialTracker();
        guide.Observe(default, 0);
        Assert.Equal(BelayAppendTrialStage.RecordStation, guide.Stage);
        guide.StationChanged(true);
        guide.Observe(Climbing with { Active = false }, 1);
        Assert.Equal(BelayAppendTrialStage.StartExercise, guide.Stage);
    }

    [Fact]
    public void ClearingThePreviousStationShowsARetainedRetryUntilItIsRecordedAgain()
    {
        var guide = new BelayAppendTrialTracker();
        guide.StationChanged(false);
        guide.Observe(default, 100);
        Assert.Equal(BelayAppendTrialStage.Retry, guide.Stage);
        Assert.Equal(BelayAppendTrialFailure.StationCleared, guide.Failure);
        guide.StationChanged(true);
        Assert.False(guide.HasOutcome);
        Assert.Equal(BelayAppendTrialStage.StartExercise, guide.Stage);
    }

    [Fact]
    public void TheRecordedSecondAttemptWithoutAStationFailsImmediately()
    {
        var guide = new BelayAppendTrialTracker();
        guide.Observe(Climbing with { HasStation = false, StationValid = false }, 0);
        Assert.Equal(BelayAppendTrialFailure.MissingStation, guide.Failure);
        guide.Observe(Climbing with { Active = false }, 100);
        Assert.Equal(BelayAppendTrialStage.Retry, guide.Stage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void ANewAttemptRequiresExactlyOneIntermediateBeforeTheAddition(int intermediates)
    {
        var guide = new BelayAppendTrialTracker();
        guide.Observe(Climbing with { Intermediates = intermediates }, 0);
        Assert.Equal(BelayAppendTrialFailure.WrongStartRoute, guide.Failure);
    }

    [Fact]
    public void FeedingRequiresEnoughSlackReleasedArrowsAndNativeLengthConfirmation()
    {
        var guide = new BelayAppendTrialTracker();
        guide.Observe(Climbing, 0);
        Assert.Equal(BelayAppendTrialStage.FeedSlack, guide.Stage);
        guide.Observe(Climbing with { Slack = 4, Motion = 1 }, 1);
        Assert.Equal(BelayAppendTrialStage.WaitForLength, guide.Stage);
        guide.Observe(Climbing with { Slack = 4, LengthApplied = false }, 2);
        Assert.Equal(BelayAppendTrialStage.WaitForLength, guide.Stage);
        guide.Observe(Climbing with { Slack = 4 }, 3);
        Assert.Equal(BelayAppendTrialStage.CloseBrake, guide.Stage);
    }

    [Fact]
    public void ClosingTooEarlyCanBeCorrectedBeforeClippingWithoutRestarting()
    {
        var guide = new BelayAppendTrialTracker();
        guide.Observe(Climbing, 0);
        guide.Observe(Climbing with { Braked = true }, 1);
        Assert.Equal(BelayAppendTrialStage.OpenBrake, guide.Stage);
        guide.Observe(Climbing with { Slack = 4 }, 2);
        guide.Observe(Climbing with { Slack = 4, Braked = true }, 3);
        Assert.Equal(BelayAppendTrialStage.AddProtection, guide.Stage);
    }

    [Fact]
    public void ConsumingSlackByClimbingDoesNotUndoAnAlreadyPreparedBrake()
    {
        var guide = Armed();
        guide.Observe(Climbing with { Braked = true, Slack = 1.2f }, 2);
        Assert.Equal(BelayAppendTrialStage.AddProtection, guide.Stage);
        Assert.False(guide.HasOutcome);
    }

    [Fact]
    public void TheRecordedFreeAdditionCannotPassTheBrakedTrial()
    {
        var guide = new BelayAppendTrialTracker();
        guide.Observe(Climbing with { Slack = 4 }, 0);
        guide.Observe(Climbing with { Rebinding = true, HasRope = false }, 1);
        Assert.Equal(BelayAppendTrialFailure.UnpreparedAppend, guide.Failure);
        guide.Observe(Added(), 2);
        Assert.Equal(BelayAppendTrialStage.Retry, guide.Stage);
    }

    [Fact]
    public void TemporaryPlacementAndLengthReadbackCannotStartTheFiveSecondCountdown()
    {
        var guide = Armed();
        guide.Observe(Climbing with { Braked = true, Rebinding = true, HasRope = false }, 2);
        Assert.Equal(BelayAppendTrialStage.UpdatingRoute, guide.Stage);
        guide.Observe(Added() with { LengthApplied = false }, 3);
        Assert.Equal(BelayAppendTrialStage.WaitForLength, guide.Stage);
        Assert.Equal(5, guide.RemainingSeconds);
        guide.Observe(Added(), 4);
        Assert.Equal(BelayAppendTrialStage.HoldStill, guide.Stage);
        Assert.Equal(5, guide.RemainingSeconds);
    }

    [Fact]
    public void FiveObservedSecondsPassAndTheResultRemainsAfterStoppingOrLeavingTheScene()
    {
        var guide = Armed();
        for (var i = 0; i <= 10; i++) guide.Observe(Added(), 2 + i * .5);
        Assert.Equal(BelayAppendTrialStage.Passed, guide.Stage);
        guide.Observe(default, 100);
        guide.RejectAction();
        Assert.Equal(BelayAppendTrialStage.Passed, guide.Stage);
        Assert.Equal(2, guide.Intermediates);
        Assert.Equal(0, guide.RemainingSeconds);
    }

    [Fact]
    public void ANewExerciseClearsThePreviousResultAndChecksSetupAgain()
    {
        var guide = Armed();
        for (var i = 0; i <= 10; i++) guide.Observe(Added(), 2 + i * .5);
        guide.Observe(default, 8);
        guide.Observe(Climbing, 9);
        Assert.Equal(BelayAppendTrialStage.FeedSlack, guide.Stage);
        Assert.Equal(2, guide.Attempt);
        Assert.False(guide.HasOutcome);
    }

    [Fact]
    public void StoppingEarlyKeepsAnExplicitRetryResult()
    {
        var guide = Armed();
        guide.Observe(Added(), 2);
        guide.Observe(default, 3);
        Assert.Equal(BelayAppendTrialFailure.Ended, guide.Failure);
        guide.Observe(default, 1000);
        Assert.Equal(BelayAppendTrialStage.Retry, guide.Stage);
    }

    [Fact]
    public void AReadbackInterruptionRestartsTheCountdown()
    {
        var guide = Armed();
        guide.Observe(Added(), 2);
        guide.Observe(Added(), 3);
        guide.Observe(Added() with { LengthApplied = false }, 4);
        guide.Observe(Added(), 5);
        Assert.Equal(5, guide.RemainingSeconds);
    }

    [Fact]
    public void UnobservedTimeDoesNotProduceAnAutomaticPass()
    {
        var guide = Armed();
        guide.Observe(Added(), 2);
        guide.Observe(Added(), 20);
        Assert.Equal(BelayAppendTrialStage.HoldStill, guide.Stage);
        Assert.Equal(5, guide.RemainingSeconds);
    }

    [Fact]
    public void OpeningTheBrakeDuringTheUpdateRequiresARetry()
    {
        var guide = Armed();
        guide.Observe(Climbing with { Braked = true, Rebinding = true }, 2);
        guide.Observe(Added() with { Braked = false }, 3);
        Assert.Equal(BelayAppendTrialFailure.BrakeOpened, guide.Failure);
    }

    [Fact]
    public void ExtraPitonsCannotPassAsTheRequestedSingleAddition()
    {
        var guide = Armed();
        guide.Observe(Added() with { Intermediates = 3 }, 2);
        Assert.Equal(BelayAppendTrialFailure.ExtraProtection, guide.Failure);
    }

    [Fact]
    public void AStaleRevisionCannotAcknowledgeTheNewRoute()
    {
        var guide = Armed();
        guide.Observe(Added() with { Revision = 1 }, 2);
        Assert.Equal(BelayAppendTrialFailure.UnpreparedAppend, guide.Failure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void RopeCommandsAfterArmingInvalidateThisControlledTrial(int motion)
    {
        var guide = Armed();
        guide.Observe(Climbing with { Braked = true, Motion = motion }, 2);
        Assert.Equal(BelayAppendTrialFailure.RopeInput, guide.Failure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStageCannotSurviveALostStationOrContext(bool loseStation)
    {
        var guide = Armed();
        var sample = Added();
        guide.Observe(loseStation ? sample with { HasStation = false } : sample with { ContextValid = false }, 2);
        Assert.Equal(loseStation ? BelayAppendTrialFailure.MissingStation : BelayAppendTrialFailure.ContextLost, guide.Failure);
    }

    [Fact]
    public void FallingCannotPassTheNoFallTrial()
    {
        var guide = Armed();
        guide.Observe(Added() with { Suspended = true }, 2);
        Assert.Equal(BelayAppendTrialFailure.Suspended, guide.Failure);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    [InlineData(.5d)]
    public void InvalidOrReversedTimeFailsInsteadOfCountingTowardSuccess(double now)
    {
        var guide = Armed();
        guide.Observe(Added(), now);
        Assert.Equal(BelayAppendTrialFailure.InvalidMeasurement, guide.Failure);
    }

    private static BelayAppendTrialTracker Armed()
    {
        var guide = new BelayAppendTrialTracker();
        guide.Observe(Climbing with { Slack = 4 }, 0);
        guide.Observe(Climbing with { Slack = 4, Braked = true }, 1);
        return guide;
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1f)]
    public void InvalidSlackCannotArmTheBrake(float slack)
    {
        var guide = new BelayAppendTrialTracker();
        guide.Observe(Climbing with { Slack = slack }, 0);
        Assert.Equal(BelayAppendTrialFailure.InvalidMeasurement, guide.Failure);
    }

    private static BelayAppendTrialSample Added() => Climbing with
        { Intermediates = 2, Revision = 2, Braked = true, Slack = 1 };
}
