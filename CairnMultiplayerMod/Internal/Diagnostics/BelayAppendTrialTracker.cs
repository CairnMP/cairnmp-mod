using System;

namespace CairnMultiplayerMod.Internal.Diagnostics;

internal enum BelayAppendTrialStage
{
    RecordStation, StartExercise, FeedSlack, WaitForLength, CloseBrake,
    OpenBrake, AddProtection, UpdatingRoute, HoldStill, Passed, Retry
}

internal enum BelayAppendTrialFailure
{
    None, MissingStation, StationCleared, WrongStartRoute, UnpreparedAppend,
    BrakeOpened, ExtraProtection, RopeInput, Suspended, ContextLost, Ended,
    InvalidMeasurement, RejectedAction
}

internal readonly record struct BelayAppendTrialSample(bool Active, bool HasStation,
    bool StationValid, int Intermediates, uint Revision, bool Rebinding, bool Braked,
    bool Suspended, bool HasRope, float Slack, int Motion, bool LengthApplied, bool ContextValid);

/// <summary>Observation-only feedback for one offline, braked protection addition.</summary>
internal sealed class BelayAppendTrialTracker
{
    internal const float RequiredSlack = 3.5f;
    internal const double HoldSeconds = 5;
    internal BelayAppendTrialStage Stage { get; private set; }
    internal BelayAppendTrialFailure Failure { get; private set; }
    internal bool HasOutcome => Stage is BelayAppendTrialStage.Passed or BelayAppendTrialStage.Retry;
    internal double RemainingSeconds { get; private set; } = HoldSeconds;
    internal float Slack { get; private set; }
    internal int Intermediates { get; private set; }
    internal int Attempt { get; private set; }
    private bool _wasActive, _feedReady, _armed, _appendStarted;
    private uint _revision;
    private double _lastObservation = -1, _stableSince = double.NaN;

    internal void StationChanged(bool recorded)
    {
        Reset();
        if (recorded) Stage = BelayAppendTrialStage.StartExercise;
        else Fail(BelayAppendTrialFailure.StationCleared);
    }

    internal void RejectAction()
    {
        if (!HasOutcome) Fail(BelayAppendTrialFailure.RejectedAction);
    }

    internal void Observe(BelayAppendTrialSample sample, double now)
    {
        var wasActive = _wasActive;
        if (sample.Active && !wasActive)
        {
            Reset();
            if (Attempt < int.MaxValue) Attempt++;
            _revision = sample.Revision;
            if (!sample.HasStation || !sample.StationValid) Fail(BelayAppendTrialFailure.MissingStation);
            else if (sample.Intermediates != 1) Fail(BelayAppendTrialFailure.WrongStartRoute);
        }
        _wasActive = sample.Active;
        if (HasOutcome) return; // Results remain until a new exercise or station action.
        if (!double.IsFinite(now) || now < 0 || now < _lastObservation)
        { Fail(BelayAppendTrialFailure.InvalidMeasurement); return; }
        var gap = _lastObservation < 0 ? 0 : now - _lastObservation;
        _lastObservation = now;
        if (!sample.Active)
        {
            if (wasActive) Fail(BelayAppendTrialFailure.Ended);
            else Stage = sample.HasStation ? BelayAppendTrialStage.StartExercise : BelayAppendTrialStage.RecordStation;
            return;
        }
        Intermediates = sample.Intermediates;
        if (sample.HasRope) Slack = sample.Slack;
        if (!sample.ContextValid) { Fail(BelayAppendTrialFailure.ContextLost); return; }
        if (!sample.HasStation || !sample.StationValid) { Fail(BelayAppendTrialFailure.MissingStation); return; }
        if (sample.Suspended) { Fail(BelayAppendTrialFailure.Suspended); return; }
        if (sample.HasRope && (!float.IsFinite(sample.Slack) || sample.Slack < 0))
        { Fail(BelayAppendTrialFailure.InvalidMeasurement); return; }
        if (_appendStarted && !sample.Braked) { Fail(BelayAppendTrialFailure.BrakeOpened); return; }
        if (_armed && sample.Braked && sample.Motion != 0) { Fail(BelayAppendTrialFailure.RopeInput); return; }
        if (sample.Rebinding)
        {
            if (!_armed || !sample.Braked) { Fail(BelayAppendTrialFailure.UnpreparedAppend); return; }
            _appendStarted = true;
            ResetHold();
            Stage = BelayAppendTrialStage.UpdatingRoute;
            return;
        }
        if (sample.Intermediates != 1)
        {
            if (sample.Intermediates != 2) { Fail(BelayAppendTrialFailure.ExtraProtection); return; }
            if (!_armed || !sample.Braked || sample.Revision <= _revision)
            { Fail(BelayAppendTrialFailure.UnpreparedAppend); return; }
            _appendStarted = true;
            if (!sample.HasRope || !sample.LengthApplied)
            { ResetHold(); Stage = BelayAppendTrialStage.WaitForLength; return; }
            if (gap > 1) ResetHold(); // A long unobserved gap cannot count toward a pass.
            if (double.IsNaN(_stableSince)) _stableSince = now;
            RemainingSeconds = Math.Max(0, HoldSeconds - (now - _stableSince));
            Stage = RemainingSeconds <= 0 ? BelayAppendTrialStage.Passed : BelayAppendTrialStage.HoldStill;
            return;
        }
        ResetHold();
        if (!sample.HasRope) { Stage = BelayAppendTrialStage.WaitForLength; return; }
        if (!sample.Braked)
        {
            _armed = false;
            _feedReady = sample.Slack >= RequiredSlack && sample.LengthApplied && sample.Motion == 0;
            Stage = sample.Slack < RequiredSlack ? BelayAppendTrialStage.FeedSlack
                : _feedReady ? BelayAppendTrialStage.CloseBrake : BelayAppendTrialStage.WaitForLength;
            return;
        }
        if (!_armed)
        {
            if (!_feedReady || sample.Motion != 0)
            { _feedReady = false; Stage = BelayAppendTrialStage.OpenBrake; return; }
            _armed = true;
        }
        Stage = BelayAppendTrialStage.AddProtection;
    }

    private void Reset()
    {
        Stage = BelayAppendTrialStage.RecordStation;
        Failure = BelayAppendTrialFailure.None;
        _wasActive = _feedReady = _armed = _appendStarted = false;
        _lastObservation = -1;
        Slack = 0; Intermediates = 0;
        ResetHold();
    }

    private void ResetHold() { _stableSince = double.NaN; RemainingSeconds = HoldSeconds; }
    private void Fail(BelayAppendTrialFailure failure)
    {
        Failure = failure;
        Stage = BelayAppendTrialStage.Retry;
        ResetHold();
    }
}
