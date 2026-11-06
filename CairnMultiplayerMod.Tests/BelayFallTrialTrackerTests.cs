using System;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayFallTrialTrackerTests
{
    private static BelayFallObservationSample Wall => new(true, true, true, true,
        true, false, false, false, false, 11, 22, 33, 4, 30, 10, 100, 100, 7, 12, 8, true, 1.5f,
        StationRecorded: false);

    private static BelayFallObservationSample ManualWall => Wall with
        { StationRecorded = true, ReleasedRopeLength = 10, FixedPathLength = 2, ReleasedFinalLength = 8 };

    [Fact]
    public void ReadinessNeverArmsOrPassesWithoutAnExplicitRequest()
    {
        var trial = new BelayFallTrialTracker();
        trial.Observe(default, 0);
        Assert.Equal(BelayFallTrialStage.Setup, trial.Stage);
        trial.Observe(Wall, .1);
        Assert.True(trial.Ready);
        Assert.Equal(BelayFallTrialStage.Ready, trial.Stage);
        Assert.False(trial.Active);
        Assert.False(trial.HasOutcome);
        Assert.Equal(0, trial.Attempt);
        trial.Observe(Loaded(), .2);
        Assert.False(trial.Ready);
        Assert.False(trial.HasMeasurement);
    }

    [Fact]
    public void AShortFallPassesOnlyAfterOneContinuouslyObservedSecond()
    {
        var trial = Armed();
        trial.Observe(Falling(), .25);
        Assert.Equal(BelayFallTrialStage.Falling, trial.Stage);
        trial.Observe(Loaded(), .5);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.True(trial.HasMeasurement);
        Assert.Equal(2, trial.Drop);
        Assert.Equal(10, trial.RopeLength);
        Assert.Equal(.2f, trial.Factor);
        Assert.Equal(1, trial.RemainingSeconds);
        Assert.Equal(.5, trial.CapturedAt);
        trial.Observe(Loaded(), 1);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(.5, trial.RemainingSeconds);
        trial.Observe(Loaded(), 1.499999);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.False(trial.HasOutcome);
        Assert.True(trial.RemainingSeconds > 0);
        trial.Observe(Loaded(), 1.5);
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
        Assert.Equal(0, trial.RemainingSeconds);
        Assert.Contains("damage curve NOT validated", trial.Detail);
    }

    [Fact]
    public void FallHeightUsesTheLastWallSampleBeforeTheOnsetSample()
    {
        var trial = Armed();
        trial.Observe(Wall with { Height = 31 }, .1);
        trial.Observe(Falling() with { Height = 30.6f }, .2);
        trial.Observe(Loaded() with { Height = 28.5f }, .3);
        Assert.Equal(2.5f, trial.Drop);
        Assert.Equal(.25f, trial.Factor);
    }

    [Fact]
    public void NativeDepartureLengthAndLastUnloadedLengthRemainSeparateFromLoadedReadings()
    {
        var trial = Armed();
        trial.Observe(Falling() with { RopeLength = 12 }, .2);
        trial.Observe(Loaded() with { RopeLength = 18 }, .4);
        Assert.Equal(10, trial.RopeLength);
        Assert.Equal(10, trial.DepartureRopeLength);
        Assert.Equal(12, trial.PreLoadRopeLength);
        Assert.Equal(.2f, trial.Factor);
        trial.Observe(Loaded() with { RopeLength = 25, Height = 20 }, .6);
        Assert.Equal(10, trial.RopeLength);
        Assert.Equal(12, trial.PreLoadRopeLength);
        Assert.Equal(2, trial.Drop);
    }

    [Fact]
    public void ATautWallSampleUpdatesDepartureLengthWithoutReplacingTheLastUnloadedReading()
    {
        var trial = Armed();
        trial.Observe(Wall with { RopeTaut = true, RopeLength = 20 }, .2);
        trial.Observe(Loaded() with { RopeLength = 25 }, .4);
        Assert.Equal(20, trial.RopeLength);
        Assert.Equal(20, trial.DepartureRopeLength);
        Assert.Equal(10, trial.PreLoadRopeLength);
        Assert.Equal(.1f, trial.Factor);
    }

    [Fact]
    public void PassiveR28EstimateUsesDepartureLengthRatherThanShrinkingNativeRestLength()
    {
        var trial = new BelayFallTrialTracker(guided: false);
        var departure = Wall with { Height = 0, RopeLength = 1.611f, ControlActive = false,
            ObservationAvailable = true, StationRecorded = false };
        Assert.True(trial.Arm(departure, 0, out _));
        trial.Observe(departure with { Falling = true, Height = -1, RopeLength = 1.005f }, .25);
        trial.Observe(departure with { Falling = true, Suspended = true, RopeTaut = true,
            Height = -2.883f, RopeLength = .9f }, .5);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(BelayFallLengthSource.NativeDepartureEstimate, trial.LengthSource);
        Assert.False(trial.ReferenceStationRecorded);
        Assert.Equal(2.883f, trial.Drop);
        Assert.Equal(1.611f, trial.DepartureRopeLength);
        Assert.Equal(1.005f, trial.PreLoadRopeLength);
        Assert.Equal(1.611f, trial.RopeLength);
        Assert.InRange(trial.Factor, 1.78f, 1.80f);
        Assert.True(float.IsNaN(trial.DepartureBudgetLength));
    }

    [Fact]
    public void AShortFallCanHaveAHighEstimatedRatioWithoutClampingTheMeasuredGeometry()
    {
        var trial = new BelayFallTrialTracker(guided: false);
        var departure = Wall with { Height = 0, RopeLength = .202f, ControlActive = false,
            ObservationAvailable = true, StationRecorded = false };
        Assert.True(trial.Arm(departure, 0, out _));
        trial.Observe(departure with { Falling = true, Suspended = true, RopeTaut = true,
            Height = -.393f, RopeLength = .15f }, .25);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(.393f, trial.Drop);
        Assert.Equal(.202f, trial.RopeLength);
        Assert.InRange(trial.Factor, 1.94f, 1.95f);
        Assert.Equal(0u, trial.NativeDamageCalls);
        Assert.Equal(0, trial.HpLoss);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARecordedManualStationUsesTheLastUnloadedPaidOutBudgetInEitherMode(bool guided)
    {
        var trial = new BelayFallTrialTracker(guided);
        var departure = ManualWall with { RopeLength = 4 };
        Assert.True(trial.Arm(departure, 0, out _));
        trial.Observe(departure with { Falling = true, Height = 29, Braked = false,
            RopeLength = 3, ReleasedRopeLength = 12, ReleasedFinalLength = 10 }, .25);
        var caught = departure with { Falling = true, Suspended = true, RopeTaut = true,
            Height = 28, RopeLength = 2, ReleasedRopeLength = 20, ReleasedFinalLength = 18 };
        trial.Observe(caught, .5);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(BelayFallLengthSource.ManualStationBudget, trial.LengthSource);
        Assert.True(trial.ReferenceStationRecorded);
        Assert.Equal(4, trial.DepartureRopeLength);
        Assert.Equal(3, trial.PreLoadRopeLength);
        Assert.Equal(10, trial.DepartureBudgetLength);
        Assert.Equal(2, trial.DepartureFixedPathLength);
        Assert.Equal(10, trial.PreLoadReleasedFinalLength);
        Assert.Equal(12, trial.RopeLength);
        Assert.Equal(2f / 12, trial.Factor);
        ObserveThrough(trial, caught with { ReleasedRopeLength = 30, ReleasedFinalLength = 28, RopeLength = 1 }, 1, 1.5);
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
        Assert.Equal(12, trial.RopeLength);
        Assert.Equal(4, trial.DepartureRopeLength);
        Assert.Equal(3, trial.PreLoadRopeLength);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualReferenceFreezesTheLastWallFixedPathInsteadOfItsShrinkingFallReading(bool guided)
    {
        var trial = new BelayFallTrialTracker(guided);
        var initial = ManualWall with { FixedPathLength = 4, ReleasedFinalLength = 2, ReleasedRopeLength = 6 };
        Assert.True(trial.Arm(initial, 0, out _));
        var lastWall = initial with { FixedPathLength = 5, ReleasedRopeLength = 7 };
        trial.Observe(lastWall, .1);
        trial.Observe(lastWall with { Falling = true, Height = 29, FixedPathLength = 3,
            ReleasedRopeLength = 5, RopeLength = 6 }, .25);
        trial.Observe(lastWall with { Falling = true, Suspended = true, RopeTaut = true,
            Height = 28, FixedPathLength = 2, ReleasedRopeLength = 4, RopeLength = 5 }, .5);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(BelayFallLengthSource.ManualStationBudget, trial.LengthSource);
        Assert.Equal(5, trial.DepartureFixedPathLength);
        Assert.Equal(2, trial.PreLoadReleasedFinalLength);
        Assert.Equal(7, trial.DepartureBudgetLength);
        Assert.Equal(7, trial.RopeLength);
        Assert.Equal(2f / 7, trial.Factor);
    }

    [Theory]
    [InlineData(11f)]
    [InlineData(6f)]
    public void FreePayoutOrTakeInUpdatesOnlyTheFinalBudgetBeforeLoad(float lastReleasedFinal)
    {
        var trial = new BelayFallTrialTracker(guided: false);
        var departure = ManualWall with { Braked = false };
        Assert.True(trial.Arm(departure, 0, out _));
        trial.Observe(departure with { Falling = true, Height = 29, FixedPathLength = 1,
            ReleasedFinalLength = 11, ReleasedRopeLength = 12 }, .1);
        trial.Observe(departure with { Falling = true, Height = 28.5f, FixedPathLength = .5f,
            ReleasedFinalLength = lastReleasedFinal, ReleasedRopeLength = .5f + lastReleasedFinal }, .3);
        var caught = departure with { Falling = true, Suspended = true, RopeTaut = true, Height = 28,
            FixedPathLength = 0, ReleasedFinalLength = 20, ReleasedRopeLength = 20 };
        trial.Observe(caught, .5);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(2, trial.DepartureFixedPathLength);
        Assert.Equal(lastReleasedFinal, trial.PreLoadReleasedFinalLength);
        Assert.Equal(2 + lastReleasedFinal, trial.RopeLength);
        Assert.Equal(2 / (2 + lastReleasedFinal), trial.Factor);
        ObserveThrough(trial, caught with { FixedPathLength = 20, ReleasedFinalLength = 30,
            ReleasedRopeLength = 50 }, 1, 1.5);
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
        Assert.Equal(2, trial.DepartureFixedPathLength);
        Assert.Equal(lastReleasedFinal, trial.PreLoadReleasedFinalLength);
        Assert.Equal(2 + lastReleasedFinal, trial.RopeLength);
    }

    [Theory]
    [InlineData(float.NaN, 8f, 10f)]
    [InlineData(float.PositiveInfinity, 8f, 10f)]
    [InlineData(-1f, 8f, 7f)]
    [InlineData(2f, float.NaN, 10f)]
    [InlineData(2f, 0f, 2f)]
    [InlineData(2f, -1f, 1f)]
    [InlineData(199f, 2f, 200f)]
    [InlineData(201f, 1f, 200f)]
    [InlineData(2f, 8f, float.NaN)]
    [InlineData(2f, 8f, 0f)]
    [InlineData(2f, 8f, 201f)]
    public void InvalidManualBudgetComponentsCannotSilentlyFallBackToANativeEstimate(
        float fixedPath, float releasedFinal, float rawTotal)
    {
        var trial = new BelayFallTrialTracker(guided: false);
        var invalid = ManualWall with { FixedPathLength = fixedPath,
            ReleasedFinalLength = releasedFinal, ReleasedRopeLength = rawTotal };
        Assert.False(trial.Arm(invalid, 0, out var reason));
        Assert.Contains("rope-budget components", reason);
        Assert.False(trial.Active);
        Assert.True(trial.Arm(ManualWall, 1, out _));
        trial.Observe(ManualWall with { Falling = true, Height = 29 }, 1.25);
        trial.Observe(invalid with { Falling = true, Suspended = true, RopeTaut = true,
            Height = 28, Hp = 90, DamageCalls = 8, RequestedFallDamage = 22, ObservedFallHpLoss = 18 }, 1.5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.Equal(BelayFallLengthSource.ManualStationBudget, trial.LengthSource);
        Assert.False(trial.HasMeasurement);
        Assert.Equal(1u, trial.NativeDamageCalls);
        Assert.Equal(90, trial.HpAfter);
    }

    [Fact]
    public void AnOverlongFrozenFixedPlusFinalReferenceCannotProduceAMeasurement()
    {
        var trial = new BelayFallTrialTracker(guided: false);
        var departure = ManualWall with { FixedPathLength = 5, ReleasedFinalLength = 3, ReleasedRopeLength = 8 };
        Assert.True(trial.Arm(departure, 0, out _));
        trial.Observe(departure with { Falling = true, Height = 29, FixedPathLength = 1,
            ReleasedFinalLength = 198, ReleasedRopeLength = 199 }, .25);
        trial.Observe(departure with { Suspended = true, RopeTaut = true, Height = 28,
            FixedPathLength = 1, ReleasedFinalLength = 198, ReleasedRopeLength = 199 }, .5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
        Assert.Equal(5, trial.DepartureFixedPathLength);
        Assert.Equal(198, trial.PreLoadReleasedFinalLength);
    }

    [Theory]
    [InlineData(false, true, 8f)]
    [InlineData(true, false, 8f)]
    public void ARouteWithoutBothManualControlAndARecordedStationRemainsAnEstimate(
        bool manualControl, bool stationRecorded, float budget)
    {
        var trial = new BelayFallTrialTracker(guided: false);
        var departure = Wall with { ControlActive = manualControl, ObservationAvailable = true,
            StationRecorded = stationRecorded, ReleasedRopeLength = budget };
        Assert.True(trial.Arm(departure, 0, out _));
        trial.Observe(departure with { Falling = true, Suspended = true, RopeTaut = true, Height = 28 }, .25);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(BelayFallLengthSource.NativeDepartureEstimate, trial.LengthSource);
        Assert.Equal(stationRecorded, trial.ReferenceStationRecorded);
        Assert.Equal(10, trial.RopeLength);
        Assert.Equal(.2f, trial.Factor);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FreeWallPreparationCanRebaselineLengthSourcesButAFallCannot(bool manualFirst, bool falling)
    {
        var trial = new BelayFallTrialTracker(guided: false);
        var departure = manualFirst ? ManualWall : Wall;
        Assert.True(trial.Arm(departure, 0, out _));
        var changed = departure with { Falling = falling, Suspended = falling, RopeTaut = falling,
            Height = falling ? 28 : 30, StationRecorded = !manualFirst,
            ReleasedRopeLength = manualFirst ? float.NaN : 12,
            FixedPathLength = manualFirst ? float.NaN : 2, ReleasedFinalLength = manualFirst ? float.NaN : 10 };
        trial.Observe(changed, .25);
        if (!falling)
        {
            Assert.Equal(BelayFallTrialStage.Armed, trial.Stage);
            Assert.Equal(manualFirst ? BelayFallLengthSource.NativeDepartureEstimate
                : BelayFallLengthSource.ManualStationBudget, trial.LengthSource);
            trial.Observe(changed with { Falling = true, Suspended = true, RopeTaut = true, Height = 28 }, .5);
            Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
            Assert.Equal(manualFirst ? 10 : 12, trial.RopeLength);
            return;
        }
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
        Assert.Equal(manualFirst ? BelayFallLengthSource.ManualStationBudget
            : BelayFallLengthSource.NativeDepartureEstimate, trial.LengthSource);
        Assert.Contains("length reference changed", trial.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuidedExplicitArmingKeepsItsLengthSourceFixedDuringWallPreparation(bool manualFirst)
    {
        var trial = new BelayFallTrialTracker();
        var departure = manualFirst ? ManualWall : Wall;
        Assert.True(trial.Arm(departure, 0, out _));
        trial.Observe(departure with { StationRecorded = !manualFirst, ReleasedRopeLength = manualFirst ? float.NaN : 12,
            FixedPathLength = manualFirst ? float.NaN : 2, ReleasedFinalLength = manualFirst ? float.NaN : 10 }, .25);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
        Assert.Equal(manualFirst ? BelayFallLengthSource.ManualStationBudget
            : BelayFallLengthSource.NativeDepartureEstimate, trial.LengthSource);
    }

    [Fact]
    public void PostCaptureSourceChangesCannotReplaceTheMeasuredLengthOrItsOrigin()
    {
        var trial = new BelayFallTrialTracker(guided: false);
        var departure = ManualWall with { RopeLength = 4 };
        Assert.True(trial.Arm(departure, 0, out _));
        trial.Observe(departure with { Falling = true, RopeLength = 3, ReleasedRopeLength = 12,
            ReleasedFinalLength = 10, Height = 29 }, .25);
        trial.Observe(departure with { Suspended = true, RopeTaut = true, Height = 28, RopeLength = 2 }, .5);
        ObserveThrough(trial, departure with { ControlActive = false, ObservationAvailable = true,
            StationRecorded = false, ReleasedRopeLength = float.NaN, RopeLength = 20 }, 1, 1.5);
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
        Assert.Equal(BelayFallLengthSource.ManualStationBudget, trial.LengthSource);
        Assert.True(trial.ReferenceStationRecorded);
        Assert.Equal(4, trial.DepartureRopeLength);
        Assert.Equal(3, trial.PreLoadRopeLength);
        Assert.Equal(10, trial.DepartureBudgetLength);
        Assert.Equal(2, trial.DepartureFixedPathLength);
        Assert.Equal(10, trial.PreLoadReleasedFinalLength);
        Assert.Equal(12, trial.RopeLength);
    }

    [Fact]
    public void FreeSwingAndAnUnsuspendedTautRopeCannotCaptureTheLoad()
    {
        var trial = Armed();
        trial.Observe(Loaded() with { Braked = false }, .2);
        Assert.Equal(BelayFallTrialStage.Falling, trial.Stage);
        Assert.False(trial.HasMeasurement);
        trial.Observe(Falling() with { RopeTaut = true }, .4);
        Assert.False(trial.HasMeasurement);
        trial.Observe(Loaded() with { RopeTaut = false }, .6);
        Assert.False(trial.HasMeasurement);
        trial.Observe(Loaded(), .8);
        Assert.True(trial.HasMeasurement);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
    }

    [Fact]
    public void SuspensionCanRevealAFallWhenTheNativeFallingFlagWasNotSampled()
    {
        var trial = Armed();
        trial.Observe(Loaded() with { Falling = false }, .25);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(2, trial.Drop);
    }

    [Fact]
    public void BrakeAndSwingReplayCannotReplaceTheFirstMeasurementOrRestartTheHold()
    {
        var trial = Armed();
        trial.Observe(Loaded(), .5);
        trial.Observe(Wall with { Braked = false, Height = 18, RopeLength = 25, Revision = 5 }, .75);
        trial.Observe(Loaded() with { Height = 14, RopeLength = 30, Revision = 6 }, 1);
        trial.Observe(Wall with { Height = 30, Revision = 7 }, 1.25);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        trial.Observe(Loaded() with { Height = 10, RopeLength = 40, Revision = 8 }, 1.5);
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
        Assert.Equal(2, trial.Drop);
        Assert.Equal(10, trial.RopeLength);
        Assert.Equal(.2f, trial.Factor);
    }

    [Fact]
    public void HealthDifferenceRequestedDamageAndHookObservedLossRemainDistinct()
    {
        var trial = Armed();
        var caught = Loaded() with { Hp = 92, DamageCalls = 9, RequestedFallDamage = 18,
            ObservedFallHpLoss = 11 };
        trial.Observe(caught, .5);
        ObserveThrough(trial, caught with { Hp = 94 }, 1, 1.5);
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
        Assert.Equal(100, trial.HpBefore);
        Assert.Equal(94, trial.HpAfter);
        Assert.Equal(6, trial.HpLoss);
        Assert.Equal(2u, trial.NativeDamageCalls);
        Assert.Equal(6, trial.RequestedDamage);
        Assert.Equal(3, trial.ObservedFallHpLoss);
    }

    [Fact]
    public void NativeDamageCountersUseTheArmBaselineIncludingCallsBeforeTheCatch()
    {
        var trial = Armed();
        var beforeFall = Wall with { DamageCalls = 8, RequestedFallDamage = 14, ObservedFallHpLoss = 9 };
        trial.Observe(beforeFall, .2);
        trial.Observe(beforeFall with { Falling = true, Height = 29 }, .4);
        trial.Observe(beforeFall with { Suspended = true, RopeTaut = true, Height = 28,
            DamageCalls = 9, RequestedFallDamage = 17, ObservedFallHpLoss = 10 }, .6);
        Assert.Equal(2u, trial.NativeDamageCalls);
        Assert.Equal(5, trial.RequestedDamage);
        Assert.Equal(2, trial.ObservedFallHpLoss);
    }

    [Fact]
    public void AMeasuredCatchCanPassWithoutDamageAndDoesNotValidateADamageCurve()
    {
        var trial = Passed();
        Assert.Equal(0u, trial.NativeDamageCalls);
        Assert.Equal(0, trial.HpLoss);
        Assert.Equal(0, trial.RequestedDamage);
        Assert.Equal(0, trial.ObservedFallHpLoss);
        Assert.Contains("Measurement recorded", trial.Detail);
        Assert.Contains("NOT validated", trial.Detail);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.2f)]
    [InlineData(-1f)]
    public void AnInsufficientOrUpwardFirstTensionWaitsForTheFallRatherThanEndingIt(float drop)
    {
        var trial = Armed();
        var small = Loaded() with { Height = Wall.Height - drop };
        trial.Observe(small, .25);
        Assert.Equal(BelayFallTrialStage.Falling, trial.Stage);
        Assert.False(trial.HasOutcome);
        Assert.False(trial.HasMeasurement);
        Assert.Equal(0, trial.RopeLength);
        ObserveThrough(trial, small, .75, 14.75);
        Assert.Equal(BelayFallTrialStage.Falling, trial.Stage);
        trial.Observe(small, 15.25);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
        Assert.Equal(0, trial.Drop);
        Assert.Contains("15 seconds", trial.Detail);
    }

    [Theory]
    [InlineData(.002f, 3.55f, 2.092f, 1.688f)]
    [InlineData(-.001f, 4.285f, 3.373f, 2.936f)]
    public void ADepartureTensionCanUnloadThenRecordTheLaterMeasurableNativeCatch(
        float initialDrop, float actualDrop, float initialLength, float unloadedLength)
    {
        var trial = new BelayFallTrialTracker(guided: false);
        Assert.True(trial.Arm(Wall with { RopeLength = initialLength }, 0, out _));
        trial.Observe(Loaded() with { Height = Wall.Height - initialDrop, RopeLength = initialLength }, .1);
        Assert.Equal(BelayFallTrialStage.Falling, trial.Stage);
        Assert.False(trial.HasMeasurement);
        trial.Observe(Falling() with { Height = Wall.Height - 1, RopeLength = unloadedLength }, .3);
        trial.Observe(Loaded() with { Height = Wall.Height - actualDrop, RopeLength = unloadedLength + 1 }, .5);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(Wall.Height - (Wall.Height - actualDrop), trial.Drop);
        Assert.Equal(initialLength, trial.RopeLength);
        Assert.Equal(unloadedLength, trial.PreLoadRopeLength);
        Assert.Equal(trial.Drop / initialLength, trial.Factor);
        ObserveThrough(trial, Loaded() with { RopeLength = unloadedLength + 1 }, 1, 1.5);
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
    }

    [Fact]
    public void NativeDamageAfterATinyDepartureLoadIsNotLostBeforeTheLaterCatch()
    {
        var trial = new BelayFallTrialTracker(guided: false);
        var before = Wall with { Hp = 94.328f, DamageCalls = 0, RequestedFallDamage = 0,
            ObservedFallHpLoss = 0, RopeLength = 3.891f };
        Assert.True(trial.Arm(before, 0, out _));
        trial.Observe(before with { Falling = true, Suspended = true, RopeTaut = true,
            Height = Wall.Height - .014f }, .1);
        Assert.False(trial.HasOutcome);
        trial.Observe(before with { Falling = true, Suspended = true, Height = Wall.Height - 3 }, .3);
        var caught = before with { Falling = true, Suspended = true, RopeTaut = true,
            Height = Wall.Height - 6.724f, Hp = 76.744f, DamageCalls = 1,
            RequestedFallDamage = 17.584f, ObservedFallHpLoss = 17.584f };
        trial.Observe(caught, .5);
        ObserveThrough(trial, caught, 1, 1.5);
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
        Assert.Equal(1u, trial.NativeDamageCalls);
        Assert.Equal(17.584f, trial.RequestedDamage);
        Assert.Equal(17.584f, trial.ObservedFallHpLoss);
        Assert.Equal(94.328f - 76.744f, trial.HpLoss);
        Assert.Equal(3.891f, trial.RopeLength);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEarlyTautPivotMustUnloadBeforeTheLaterMeasurableCatchCanBeRecorded(bool loseSuspension)
    {
        var trial = new BelayFallTrialTracker(guided: false);
        Assert.True(trial.Arm(Wall with { RopeLength = 3.373f }, 0, out _));
        trial.Observe(Loaded() with { Height = Wall.Height + .001f, RopeLength = 3.373f }, .1);
        trial.Observe(Loaded() with { Height = Wall.Height - .246f, RopeLength = 3.315f }, .2);
        trial.Observe(Loaded() with { Height = Wall.Height - .4f, RopeLength = 3.2f }, .3);
        Assert.Equal(BelayFallTrialStage.Falling, trial.Stage);
        Assert.False(trial.HasMeasurement);
        Assert.False(trial.HasOutcome);
        trial.Observe(Falling() with { Height = Wall.Height - 2, RopeLength = 2.936f,
            Suspended = !loseSuspension, RopeTaut = loseSuspension }, .4);
        trial.Observe(Loaded() with { Height = Wall.Height - 4.285f, RopeLength = 3.048f }, .5);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(Wall.Height - (Wall.Height - 4.285f), trial.Drop);
        Assert.Equal(3.373f, trial.RopeLength);
        Assert.Equal(2.936f, trial.PreLoadRopeLength);
        Assert.Equal(trial.Drop / 3.373f, trial.Factor);
    }

    [Fact]
    public void AnUninterruptedDepartureTensionCannotBecomeAMeasurementByCrossingTheMinimumDrop()
    {
        var trial = Armed();
        trial.Observe(Loaded() with { Height = Wall.Height - .002f }, .25);
        var stillTaut = Loaded() with { Hp = 90, DamageCalls = 8, RequestedFallDamage = 22,
            ObservedFallHpLoss = 18 };
        ObserveThrough(trial, stillTaut, .75, 14.75);
        Assert.Equal(BelayFallTrialStage.Falling, trial.Stage);
        Assert.False(trial.HasMeasurement);
        trial.Observe(stillTaut, 15.25);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
        Assert.Equal(0, trial.Drop);
        Assert.Equal(90, trial.HpAfter);
        Assert.Equal(1u, trial.NativeDamageCalls);
        Assert.Equal(10, trial.ObservedFallHpLoss);
    }

    [Fact]
    public void ANewAttemptDoesNotRetainThePreviousDepartureUnloadRequirement()
    {
        var trial = Armed();
        trial.Observe(Loaded() with { Height = Wall.Height - .002f }, .25);
        trial.Cancel("Start a fresh fall");
        Assert.True(trial.Arm(Wall, 1, out _));
        trial.Observe(Loaded(), 1.25);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(2, trial.Drop);
    }

    [Fact]
    public void WaitingOnTheWallTimesOutWithoutInventingAFall()
    {
        var trial = Armed();
        ObserveThrough(trial, Wall, .5, 44.5);
        Assert.Equal(BelayFallTrialStage.Armed, trial.Stage);
        trial.Observe(Wall, 45);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
        Assert.Contains("No fall", trial.Detail);
    }

    [Fact]
    public void AFreeSwingWithoutABrakedLoadTimesOutAfterFifteenSeconds()
    {
        var trial = Armed();
        var free = Loaded() with { Braked = false };
        trial.Observe(free, .5);
        ObserveThrough(trial, free, 1, 15);
        Assert.Equal(BelayFallTrialStage.Falling, trial.Stage);
        trial.Observe(free, 15.5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FocusOrExerciseLossRetainsAnInconclusiveResultAcrossRestarts(bool loseFocus)
    {
        var trial = Armed();
        trial.Observe(Loaded(), .25);
        trial.Observe(loseFocus ? Loaded() with { ContextValid = false }
            : Loaded() with { ControlActive = false }, .5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.True(trial.HasMeasurement);
        var detail = trial.Detail;
        trial.Observe(default, 100);
        trial.Observe(Wall with { Rope = 99, Revision = 20 }, 101);
        trial.Cancel("Later teardown");
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.Equal(detail, trial.Detail);
        Assert.Equal(2, trial.Drop);
        Assert.Equal(1, trial.Attempt);
    }

    [Fact]
    public void APassSurvivesSceneExitExerciseRestartAndCancellationUntilExplicitArm()
    {
        var trial = Passed();
        trial.Observe(default, 200);
        trial.Observe(Wall with { Rope = 99 }, 201);
        trial.Cancel("Scene reset");
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
        Assert.Equal(2, trial.Drop);
        Assert.True(trial.Arm(Wall with { Rope = 99, Revision = 22 }, 202, out _));
        Assert.Equal(2, trial.Attempt);
        Assert.Equal(BelayFallTrialStage.Armed, trial.Stage);
        Assert.False(trial.HasMeasurement);
        Assert.False(trial.HasOutcome);
        Assert.Equal(0, trial.Drop);
    }

    [Fact]
    public void RetainedResultsStillShowCurrentReadinessForTheNextExplicitAttempt()
    {
        var trial = Passed();
        var detail = trial.Detail;
        var finalSample = trial.LastSample;
        trial.Observe(default, 100);
        Assert.False(trial.Ready);
        Assert.Contains("offline", trial.Readiness);
        trial.Observe(Wall with { Rope = 99 }, 101);
        Assert.True(trial.Ready);
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
        Assert.Equal(detail, trial.Detail);
        Assert.Equal(finalSample, trial.LastSample);
        Assert.Equal(2, trial.Drop);
    }

    [Fact]
    public void ARejectedExplicitNewAttemptCannotDisplayThePreviousPass()
    {
        var trial = Passed();
        Assert.False(trial.Arm(Wall with { RouteReady = false }, 3, out var reason));
        Assert.False(trial.HasOutcome);
        Assert.False(trial.HasMeasurement);
        Assert.False(trial.Active);
        Assert.Equal(BelayFallTrialStage.Setup, trial.Stage);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Equal(reason, trial.Detail);
    }

    [Fact]
    public void CancellationIsAnOutcomeOnlyForAnActiveTrialAndNeverErasesAnOutcome()
    {
        var trial = new BelayFallTrialTracker();
        trial.Cancel("Nothing running");
        Assert.False(trial.HasOutcome);
        Assert.True(trial.Arm(Wall, 0, out _));
        trial.Cancel("User cancelled");
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.Equal("User cancelled", trial.Detail);
        trial.Cancel("Teardown");
        Assert.Equal("User cancelled", trial.Detail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ArmingRejectsAnUnreadyNativePosition(int condition)
    {
        var sample = condition switch
        {
            0 => Wall with { ContextValid = false },
            1 => Wall with { ControlActive = false },
            2 => Wall with { RouteReady = false },
            3 => Wall with { HasStation = false },
            4 => Wall with { Braked = false },
            5 => Wall with { Falling = true },
            6 => Wall with { Suspended = true },
            7 => Wall with { PersonallyAnchored = true },
            _ => Wall with { Hp = 0 }
        };
        var trial = new BelayFallTrialTracker();
        Assert.False(trial.Arm(sample, 0, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.False(trial.Active);
        Assert.False(trial.HasOutcome);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(201f)]
    public void ArmingRejectsAnUnusableStationToHarnessLength(float length)
    {
        var trial = new BelayFallTrialTracker();
        Assert.False(trial.Arm(Wall with { RopeLength = length }, 0, out _));
        Assert.False(trial.HasOutcome);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.03f)]
    [InlineData(.999f)]
    public void AMerelyBrakedRopeNeedsEnoughSlackForAMeasurableFallBeforeArming(float slack)
    {
        var trial = new BelayFallTrialTracker();
        var sample = Wall with { Slack = slack };
        trial.Observe(sample, 0);
        Assert.False(trial.Ready);
        Assert.False(trial.Arm(sample, .1, out var reason));
        Assert.Contains("at least 1 m", reason);
        Assert.False(trial.Active);
        Assert.False(trial.HasOutcome);
    }

    [Fact]
    public void EnoughSlackWithoutNativeLengthConfirmationCannotArmTheTrial()
    {
        var trial = new BelayFallTrialTracker();
        Assert.False(trial.Arm(Wall with { LengthApplied = false }, 0, out var reason));
        Assert.Contains("rope-length confirmation", reason);
        Assert.False(trial.Active);
    }

    [Fact]
    public void ConfirmedSlackIsAnArmRequirementAndCanBeConsumedByTheFall()
    {
        var trial = new BelayFallTrialTracker();
        Assert.True(trial.Arm(Wall with { Slack = BelayFallTrialTracker.RequiredSlack }, 0, out _));
        var consumed = Loaded() with { Slack = 0, LengthApplied = false };
        trial.Observe(consumed, .5);
        ObserveThrough(trial, consumed, 1, 1.5);
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
        Assert.Equal(2, trial.Drop);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1f)]
    public void InvalidSlackCannotArmOrCompleteAnObservation(float slack)
    {
        var trial = new BelayFallTrialTracker();
        Assert.False(trial.Arm(Wall with { Slack = slack }, 0, out _));
        Assert.True(trial.Arm(Wall, 1, out _));
        trial.Observe(Loaded(), 1.25);
        trial.Observe(Loaded() with { Slack = slack }, 1.5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.True(trial.HasMeasurement);
        Assert.Equal(2, trial.Drop);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ACorruptNativeMeasurementCannotPassAnAlreadyCapturedTrial(int field)
    {
        var trial = Armed();
        trial.Observe(Loaded(), .25);
        var invalid = field switch
        {
            0 => Loaded() with { Height = float.NaN },
            1 => Loaded() with { RopeLength = float.NaN },
            2 => Loaded() with { Hp = float.NaN },
            3 => Loaded() with { MaxHp = float.PositiveInfinity },
            4 => Loaded() with { RequestedFallDamage = float.NaN },
            _ => Loaded() with { ObservedFallHpLoss = float.PositiveInfinity }
        };
        trial.Observe(invalid, .5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.True(trial.HasMeasurement);
        Assert.Equal(2, trial.Drop);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    public void InvalidArmingTimeDoesNotCreateAnActiveAttempt(double now)
    {
        var trial = new BelayFallTrialTracker();
        Assert.False(trial.Arm(Wall, now, out _));
        Assert.False(trial.Active);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(.1d)]
    [InlineData(.750001d)]
    public void AClockErrorOrGapCannotCountTowardTheHealthObservation(double now)
    {
        var trial = Armed();
        trial.Observe(Loaded(), .25);
        trial.Observe(Loaded(), now);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.True(trial.HasMeasurement);
        Assert.Equal(2, trial.Drop);
    }

    [Fact]
    public void DeathDuringTheHealthWindowRetainsTheMeasurementEvenIfContextIsLost()
    {
        var trial = Armed();
        trial.Observe(Loaded(), .25);
        trial.Observe(Loaded() with { Hp = 0, ContextValid = false, ControlActive = false,
            DamageCalls = 8, RequestedFallDamage = 112, ObservedFallHpLoss = 108 }, .5);
        Assert.Equal(BelayFallTrialStage.Failed, trial.Stage);
        Assert.True(trial.HasMeasurement);
        Assert.Equal(2, trial.Drop);
        Assert.Equal(0, trial.HpAfter);
        Assert.Equal(100, trial.HpLoss);
        Assert.Equal(1u, trial.NativeDamageCalls);
        trial.Observe(default, 100);
        Assert.Equal(BelayFallTrialStage.Failed, trial.Stage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FatalHealthMetadataSurvivesUnavailableGeometryWithOrWithoutAPreviousCatch(bool captured)
    {
        var trial = new BelayFallTrialTracker(guided: false);
        var before = Wall with { Hp = 9.845f, DamageCalls = 3, RequestedFallDamage = 76.326f,
            ObservedFallHpLoss = 76.326f, ControlActive = false, ObservationAvailable = true };
        Assert.True(trial.Arm(before, 0, out _));
        trial.Observe(before with { Falling = true, Suspended = captured, RopeTaut = captured, Height = 28 }, .25);
        trial.Observe(before with { ContextValid = false, ObservationAvailable = false, RouteReady = false,
            Height = float.NaN, RopeLength = float.NaN, Slack = float.NaN, Hp = 0, DamageCalls = 4,
            RequestedFallDamage = 76.326f + 20.961f, ObservedFallHpLoss = 76.326f + 9.845f }, .5);
        Assert.Equal(BelayFallTrialStage.Failed, trial.Stage);
        Assert.Equal(captured, trial.HasMeasurement);
        Assert.Equal(captured ? 2 : 0, trial.Drop);
        Assert.Equal(9.845f, trial.HpBefore);
        Assert.Equal(0, trial.HpAfter);
        Assert.Equal(1u, trial.NativeDamageCalls);
        Assert.Equal((76.326f + 20.961f) - 76.326f, trial.RequestedDamage);
        Assert.Equal((76.326f + 9.845f) - 76.326f, trial.ObservedFallHpLoss);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidHealthMetadataIsRetainedWhenContextOrRouteLossMakesTheFallInconclusive(bool loseContext)
    {
        var trial = Armed();
        trial.Observe(Falling(), .25);
        trial.Observe(Falling() with { ContextValid = !loseContext, RouteReady = false,
            Height = float.NaN, RopeLength = float.NaN, Hp = 90, DamageCalls = 8,
            RequestedFallDamage = 22, ObservedFallHpLoss = 18 }, .5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
        Assert.Equal(90, trial.HpAfter);
        Assert.Equal(1u, trial.NativeDamageCalls);
        Assert.Equal(10, trial.RequestedDamage);
        Assert.Equal(10, trial.ObservedFallHpLoss);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FatalInvalidCountersRetainTheirLastValidSummaryInsteadOfInventingDeltas(bool nonFinite)
    {
        var trial = Armed();
        var valid = Falling() with { Hp = 90, DamageCalls = 8, RequestedFallDamage = 22, ObservedFallHpLoss = 18 };
        trial.Observe(valid, .25);
        trial.Observe(valid with { ContextValid = false, Height = float.NaN, RopeLength = float.NaN, Hp = 0,
            DamageCalls = nonFinite ? 9u : 0u,
            RequestedFallDamage = nonFinite ? float.NaN : 22 }, .5);
        Assert.Equal(BelayFallTrialStage.Failed, trial.Stage);
        Assert.Equal(0, trial.HpAfter);
        Assert.Equal(1u, trial.NativeDamageCalls);
        Assert.Equal(10, trial.RequestedDamage);
        Assert.Equal(10, trial.ObservedFallHpLoss);
    }

    [Fact]
    public void AForeignRopeCannotSupplyHealthMetadataToTheFallBeingObserved()
    {
        var trial = Armed();
        trial.Observe(Falling(), .25);
        trial.Observe(Falling() with { Rope = 999, ContextValid = false, Height = float.NaN,
            RopeLength = float.NaN, Hp = 0, DamageCalls = 10, RequestedFallDamage = 100,
            ObservedFallHpLoss = 100 }, .5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.Equal(100, trial.HpAfter);
        Assert.Equal(0u, trial.NativeDamageCalls);
        Assert.Equal(0, trial.RequestedDamage);
        Assert.Equal(0, trial.ObservedFallHpLoss);
    }

    [Fact]
    public void DeathBeforeTheFirstLoadCannotInventAMeasurement()
    {
        var trial = Armed();
        trial.Observe(Falling(), .25);
        trial.Observe(Falling() with { Hp = 0, ContextValid = false }, .5);
        Assert.Equal(BelayFallTrialStage.Failed, trial.Stage);
        Assert.False(trial.HasMeasurement);
    }

    [Fact]
    public void AnEmptyEndSampleIsInconclusiveRatherThanAFalseDeath()
    {
        var trial = Armed();
        trial.Observe(Loaded(), .25);
        trial.Observe(default, .5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.Equal(100, trial.HpAfter);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void APreLoadRouteOrIdentityChangeInvalidatesTheTrial(int identity)
    {
        var trial = Armed();
        trial.Observe(Falling(), .2);
        var changed = identity switch
        {
            0 => Loaded() with { Rope = 12 },
            1 => Loaded() with { Station = 23 },
            2 => Loaded() with { LastPiton = 34 },
            _ => Loaded() with { Revision = 5 }
        };
        trial.Observe(changed, .4);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ANewRopeStationOrProtectionCannotFinishThePreviousHealthObservation(int identity)
    {
        var trial = Armed();
        trial.Observe(Loaded(), .25);
        var changed = identity switch
        {
            0 => Loaded() with { Rope = 12 },
            1 => Loaded() with { Station = 23 },
            _ => Loaded() with { LastPiton = 34 }
        };
        trial.Observe(changed, .5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.True(trial.HasMeasurement);
        Assert.Equal(2, trial.Drop);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NativeCounterResetsDuringTheHealthWindowCannotProduceAPass(int counter)
    {
        var trial = Armed();
        var increased = Loaded() with { DamageCalls = 9, RequestedFallDamage = 16, ObservedFallHpLoss = 10 };
        trial.Observe(increased, .25);
        var reset = counter switch
        {
            0 => increased with { DamageCalls = 8 },
            1 => increased with { RequestedFallDamage = 15 },
            _ => increased with { ObservedFallHpLoss = 9 }
        };
        trial.Observe(reset, .5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.Equal(2u, trial.NativeDamageCalls);
        Assert.Equal(4, trial.RequestedDamage);
        Assert.Equal(2, trial.ObservedFallHpLoss);
    }

    [Fact]
    public void ANewExplicitAttemptUsesFreshCounterAndHealthBaselines()
    {
        var trial = Passed();
        var next = Wall with { Rope = 12, Station = 23, LastPiton = 34, Revision = 1,
            Hp = 80, DamageCalls = 0, RequestedFallDamage = 0, ObservedFallHpLoss = 0 };
        Assert.True(trial.Arm(next, 20, out _));
        trial.Observe(next with { Suspended = true, RopeTaut = true, Height = 29,
            Hp = 78, DamageCalls = 1, RequestedFallDamage = 3, ObservedFallHpLoss = 2 }, 20.5);
        Assert.Equal(2, trial.Attempt);
        Assert.Equal(1, trial.Drop);
        Assert.Equal(80, trial.HpBefore);
        Assert.Equal(2, trial.HpLoss);
        Assert.Equal(1u, trial.NativeDamageCalls);
        Assert.Equal(3, trial.RequestedDamage);
        Assert.Equal(2, trial.ObservedFallHpLoss);
    }

    [Fact]
    public void ACounterWrapIsInconclusiveRatherThanAnEnormousDamageDelta()
    {
        var trial = new BelayFallTrialTracker();
        Assert.True(trial.Arm(Wall with { DamageCalls = uint.MaxValue }, 0, out _));
        trial.Observe(Loaded() with { DamageCalls = 0 }, .25);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.Equal(0u, trial.NativeDamageCalls);
        Assert.False(trial.HasMeasurement);
    }

    [Fact]
    public void FiniteInputsWhoseHeightDifferenceOverflowsCannotProduceAnInfiniteFactor()
    {
        var trial = new BelayFallTrialTracker();
        Assert.True(trial.Arm(Wall with { Height = float.MaxValue }, 0, out _));
        trial.Observe(Loaded() with { Height = -float.MaxValue }, .25);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
        Assert.True(float.IsFinite(trial.Drop));
        Assert.True(float.IsFinite(trial.Factor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostStationOrRouteDuringTheHoldRetainsAnInconclusiveMeasurement(bool station)
    {
        var trial = Armed();
        trial.Observe(Loaded(), .25);
        trial.Observe(station ? Loaded() with { HasStation = false }
            : Loaded() with { RouteReady = false }, .5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.True(trial.HasMeasurement);
        Assert.Equal(2, trial.Drop);
    }

    [Fact]
    public void FreeObservationArmsWithAnOpenBrakeZeroSlackAndNoLengthCommand()
    {
        var trial = new BelayFallTrialTracker(guided: false);
        var sample = Wall with { Braked = false, Slack = 0, LengthApplied = false };
        trial.Observe(sample, 0);
        Assert.False(trial.Guided);
        Assert.True(trial.Ready);
        Assert.True(trial.Arm(sample, .1, out var reason), reason);
        Assert.Equal(BelayFallTrialStage.Armed, trial.Stage);
        Assert.False(trial.HasOutcome);
        Assert.Equal(0, trial.RemainingSeconds);
    }

    [Fact]
    public void PassiveNativeObservationCanArmAndMeasureWithoutEnablingManualRopeControl()
    {
        var passive = Wall with { ControlActive = false, ObservationAvailable = true, StationRecorded = false,
            Braked = false, Slack = 0, LengthApplied = false };
        var guided = new BelayFallTrialTracker();
        Assert.False(guided.Arm(passive, 0, out _));
        var trial = new BelayFallTrialTracker(guided: false);
        Assert.True(trial.Arm(passive, 0, out _));
        var caught = passive with { Suspended = true, RopeTaut = true, Height = 28 };
        trial.Observe(caught, .5);
        ObserveThrough(trial, caught, 1, 1.5);
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
        Assert.Equal(2, trial.Drop);
        Assert.False(trial.LastSample.ControlActive);
    }

    [Fact]
    public void LosingPassiveObservationDuringTheFallLeavesAPersistentInconclusiveResult()
    {
        var passive = Wall with { ControlActive = false, ObservationAvailable = true };
        var trial = new BelayFallTrialTracker(guided: false);
        Assert.True(trial.Arm(passive, 0, out _));
        trial.Observe(passive with { Falling = true, Height = 29 }, .25);
        trial.Observe(passive with { ObservationAvailable = false, Falling = true, Height = 28 }, .5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        trial.Observe(Wall, 1);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
    }

    [Fact]
    public void AFreeOpenBrakeNativeCatchIncludingAnEndKnotCanRecordTheMeasurement()
    {
        var trial = FreeArmed();
        var caught = Loaded() with { Braked = false, Slack = 0, LengthApplied = false };
        trial.Observe(caught, .5);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(2, trial.Drop);
        ObserveThrough(trial, caught, 1, 1.5);
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
        Assert.Contains("damage curve NOT validated", trial.Detail);
    }

    [Fact]
    public void FreePayoutAndBrakeChangesWaitForActualNativeTensionThenFreezeTheFirstCatch()
    {
        var trial = FreeArmed();
        trial.Observe(Falling() with { Braked = false, RopeLength = 11, Slack = 2 }, .25);
        trial.Observe(Loaded() with { RopeTaut = false, Braked = true, RopeLength = 12 }, .5);
        Assert.False(trial.HasMeasurement);
        trial.Observe(Loaded() with { Braked = false, Height = 27, RopeLength = 15 }, .75);
        Assert.Equal(3, trial.Drop);
        Assert.Equal(10, trial.RopeLength);
        Assert.Equal(12, trial.PreLoadRopeLength);
        Assert.Equal(.3f, trial.Factor);
        trial.Observe(Loaded() with { Height = 20, RopeLength = 25 }, 1);
        Assert.Equal(3, trial.Drop);
        Assert.Equal(10, trial.RopeLength);
        Assert.Equal(12, trial.PreLoadRopeLength);
    }

    [Fact]
    public void FreePreparationCanChangeTheWholeRouteAndStartsHealthCountersAtTheLastWallSample()
    {
        var trial = FreeArmed();
        var next = Wall with { Rope = 41, Station = 42, LastPiton = 43, Revision = 20, Height = 42,
            RopeLength = 12, Hp = 80, DamageCalls = 0, RequestedFallDamage = 0, ObservedFallHpLoss = 0,
            Braked = false, Slack = 0, LengthApplied = false };
        trial.Observe(next, .25);
        Assert.Equal(BelayFallTrialStage.Armed, trial.Stage);
        Assert.Equal(80, trial.HpBefore);
        Assert.Equal(0u, trial.NativeDamageCalls);
        var caught = next with { Falling = true, Suspended = true, RopeTaut = true, Height = 40,
            Hp = 78, DamageCalls = 1, RequestedFallDamage = 3, ObservedFallHpLoss = 2 };
        trial.Observe(caught, .5);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(2, trial.Drop);
        Assert.Equal(12, trial.RopeLength);
        Assert.Equal(80, trial.HpBefore);
        Assert.Equal(78, trial.HpAfter);
        Assert.Equal(1u, trial.NativeDamageCalls);
        Assert.Equal(3, trial.RequestedDamage);
        Assert.Equal(2, trial.ObservedFallHpLoss);
    }

    [Fact]
    public void FreeObservationWaitsForAProtectionUpdateAndUsesItsNewStableRoute()
    {
        var trial = FreeArmed();
        trial.Observe(Wall with { RouteReady = false, RopeLength = float.NaN }, .25);
        Assert.Equal(BelayFallTrialStage.Armed, trial.Stage);
        Assert.False(trial.HasMeasurement);
        var next = Wall with { LastPiton = 44, Revision = 8, Height = 34, RopeLength = 14 };
        trial.Observe(next, .5);
        trial.Observe(next with { Falling = true, Suspended = true, RopeTaut = true, Height = 31 }, .75);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(3, trial.Drop);
        Assert.Equal(14, trial.RopeLength);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFreeFallCannotReuseTheOldBaselineAfterAnUnobservedProtectionUpdate(bool routeReady)
    {
        var trial = FreeArmed();
        trial.Observe(Wall with { RouteReady = false, RopeLength = float.NaN }, .25);
        trial.Observe(Loaded() with { RouteReady = routeReady }, .5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
    }

    [Fact]
    public void FreeArmingWhileAnchoredWaitsForReleaseAndAStableWallBeforeMeasuring()
    {
        var trial = new BelayFallTrialTracker(guided: false);
        Assert.True(trial.Arm(Wall with { PersonallyAnchored = true }, 0, out _));
        trial.Observe(Wall with { PersonallyAnchored = true, Height = 32 }, .25);
        Assert.Equal(BelayFallTrialStage.Armed, trial.Stage);
        trial.Observe(Wall with { Height = 33 }, .5);
        trial.Observe(Loaded() with { Height = 30 }, .75);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(3, trial.Drop);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreeObservationCannotCaptureAnAnchoredCatchOrAnImmediateReleaseWithoutAWallSample(bool stillAnchored)
    {
        var trial = new BelayFallTrialTracker(guided: false);
        Assert.True(trial.Arm(Wall with { PersonallyAnchored = true }, 0, out _));
        trial.Observe(Loaded() with { PersonallyAnchored = stillAnchored }, .25);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
    }

    [Fact]
    public void ANewPersonalAnchorDuringAFreeFallCannotBecomeACatchMeasurement()
    {
        var trial = FreeArmed();
        trial.Observe(Falling(), .25);
        trial.Observe(Loaded() with { PersonallyAnchored = true }, .5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
    }

    [Fact]
    public void FreeWallPreparationHasNoFortyFiveSecondDeadline()
    {
        var trial = FreeArmed();
        ObserveThrough(trial, Wall, .5, 60);
        Assert.Equal(BelayFallTrialStage.Armed, trial.Stage);
        Assert.False(trial.HasOutcome);
        trial.Observe(Loaded(), 60.5);
        Assert.Equal(BelayFallTrialStage.Observing, trial.Stage);
        Assert.Equal(2, trial.Drop);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void FreeArmingStillRequiresUsableNativeContextRouteAndMeasurements(int invalid)
    {
        var sample = invalid switch
        {
            0 => Wall with { ContextValid = false },
            1 => Wall with { ControlActive = false },
            2 => Wall with { HasStation = false },
            3 => Wall with { RouteReady = false },
            _ => Wall with { Slack = float.NaN }
        };
        var trial = new BelayFallTrialTracker(guided: false);
        Assert.False(trial.Arm(sample, 0, out _));
        Assert.False(trial.Active);
        Assert.False(trial.HasOutcome);
    }

    [Fact]
    public void AProtectionChangeAfterAFreeFallBeginsCannotSilentlyRebaselineIt()
    {
        var trial = FreeArmed();
        trial.Observe(Falling() with { Braked = false }, .25);
        trial.Observe(Loaded() with { LastPiton = 99, Revision = 8 }, .5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
    }

    [Fact]
    public void AFreeDamageCounterResetAfterTheFallStartsIsStillInvalid()
    {
        var trial = FreeArmed();
        trial.Observe(Falling(), .25);
        trial.Observe(Loaded() with { DamageCalls = 0 }, .5);
        Assert.Equal(BelayFallTrialStage.Inconclusive, trial.Stage);
        Assert.False(trial.HasMeasurement);
    }

    [Fact]
    public void FreeObservationKeepsItsResultWhenTheNextWallEpisodeBeginsUntilTheUiArmsAgain()
    {
        var trial = FreeArmed();
        trial.Observe(Loaded(), .5);
        ObserveThrough(trial, Loaded(), 1, 1.5);
        trial.Observe(Wall with { Rope = 99, Hp = 80 }, 3);
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
        Assert.Equal(100, trial.HpBefore);
        Assert.Equal(2, trial.Drop);
        Assert.True(trial.Ready);
        Assert.True(trial.Arm(Wall with { Rope = 99, Hp = 80 }, 3, out _));
        Assert.Equal(2, trial.Attempt);
        Assert.Equal(80, trial.HpBefore);
        Assert.False(trial.HasOutcome);
        Assert.False(trial.HasMeasurement);
    }

    private static BelayFallTrialTracker FreeArmed()
    {
        var trial = new BelayFallTrialTracker(guided: false);
        Assert.True(trial.Arm(Wall with { Braked = false, Slack = 0, LengthApplied = false }, 0, out var reason), reason);
        return trial;
    }

    private static BelayFallTrialTracker Armed()
    {
        var trial = new BelayFallTrialTracker();
        Assert.True(trial.Arm(Wall, 0, out var reason), reason);
        return trial;
    }

    private static BelayFallTrialTracker Passed()
    {
        var trial = Armed();
        trial.Observe(Loaded(), .5);
        ObserveThrough(trial, Loaded(), 1, 1.5);
        Assert.Equal(BelayFallTrialStage.Passed, trial.Stage);
        return trial;
    }

    private static BelayFallObservationSample Falling() => Wall with { Falling = true, Height = 29.75f };

    private static BelayFallObservationSample Loaded() => Wall with
        { Falling = true, Suspended = true, RopeTaut = true, Height = 28 };

    private static void ObserveThrough(BelayFallTrialTracker trial, BelayFallObservationSample sample,
        double first, double last)
    {
        for (var now = first; now <= last; now += .5) trial.Observe(sample, now);
    }
}
