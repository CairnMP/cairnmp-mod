using System;
using System.Collections.Generic;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private static ManualBelayAdapter _sharedDamageOwner;
    private readonly HarmonyLib.Harmony _sharedDamageHarmony = new("CairnMultiplayerMod.PartnerFallDamage");
    private readonly HashSet<(Guid Session, uint Fall, bool Belayer)> _partnerDamageAttempts = new();
    private bool _sharedDamageInstalled, _sharedDamageUnpatchPending, _applyingPartnerDamage;
    private int _sharedDamageCallbackDepth;
    private Guid _sharedDamageSession;
    private BelayFallTrialTracker _partnerFallTrial;
    private PartnerFallCapture _pendingPartnerFall;
    private uint _partnerFallSerial, _partnerNativeCalls;
    private float _partnerNativeRequested, _partnerNativeLoss, _partnerTrialMaxHp;
    private int _partnerReportedAttempt = -1;
    private double _partnerWallSince = double.NaN, _nextSharedDamageInstall, _nextSharedDamageLog;

    private sealed class PartnerFallCapture
    {
        internal PartnerFallObservation Observation;
        internal BelayFallObservationSample Sample;
        internal float MaximumAdditionalFraction;
        internal double Until;
    }

    private sealed class PartnerNativeDamageState
    {
        internal ManualBelayAdapter Owner;
        internal Guid Session;
        internal long Survival;
        internal float Hp;
    }

    public bool SharedDamageEnabled => Enabled && ModConfig.BelaySharedDamageExperiment?.Value == true;

    private bool IsPartnerClimber(Guid session)
        => SharedDamageEnabled && session != Guid.Empty && _lease?.Session == session
            && _control != null && _ropeControl != null && !_localExercise && !_stopping
            && _partnerStation.HasValue && !_lease.IsExpired(Now) && ContextValid;

    private void TickSharedDamage()
    {
        try
        {
            if (_sharedDamageUnpatchPending && !_applyingPartnerDamage && _sharedDamageCallbackDepth == 0)
            {
                _sharedDamageHarmony.UnpatchSelf();
                _sharedDamageInstalled = _sharedDamageUnpatchPending = false;
            }
            var session = _lease?.Session ?? Guid.Empty;
            if (!IsPartnerClimber(session)) { ResetPartnerFallCapture(); return; }
            if (_sharedDamageSession != session)
            {
                ResetPartnerFallCapture();
                _sharedDamageSession = session;
                _partnerFallTrial = new(guided: false);
            }
            if (!_sharedDamageInstalled)
            {
                if (Now < _nextSharedDamageInstall) return;
                _nextSharedDamageInstall = Now + 5;
                try
                {
                    _sharedDamageHarmony.Patch(AccessTools.Method(typeof(SurvivalManager), nameof(SurvivalManager.InflictDamage)),
                        prefix: new HarmonyMethod(typeof(ManualBelayAdapter), nameof(BeforePartnerNativeDamage)),
                        postfix: new HarmonyMethod(typeof(ManualBelayAdapter), nameof(AfterPartnerNativeDamage)));
                    _sharedDamageInstalled = true;
                    ModLog.Info("[BelayDamage] Partner native fall observer installed; damage requires host approval.");
                }
                catch { _sharedDamageHarmony.UnpatchSelf(); throw; }
            }
            _sharedDamageUnpatchPending = false;
            _sharedDamageOwner = this;
            var now = Now;
            var sample = ReadPartnerFallSample(out var reason);
            if (_pendingPartnerFall != null && (now > _pendingPartnerFall.Until
                || !sample.ContextValid || !sample.Suspended || sample.PersonallyAnchored))
            {
                ModLog.Info("[BelayDamage] Pending catch invalidated or expired; no delayed damage will be applied.");
                _pendingPartnerFall = null;
            }
            if (_partnerFallTrial.Active && sample.MaxHp != _partnerTrialMaxHp)
                _partnerFallTrial.Cancel("The local maximum health changed during the catch.");
            _partnerFallTrial.Observe(sample, now);
            if (_partnerFallTrial.HasOutcome && _partnerReportedAttempt != _partnerFallTrial.Attempt)
            {
                _partnerReportedAttempt = _partnerFallTrial.Attempt;
                if (_partnerFallTrial.Stage == BelayFallTrialStage.Passed && _partnerFallTrial.HasMeasurement
                    && _partnerFallTrial.LengthSource == BelayFallLengthSource.ManualStationBudget
                    && _partnerFallTrial.ReferenceStationRecorded && sample.Suspended && !sample.PersonallyAnchored
                    && sample.RouteReady && sample.MaxHp == _partnerTrialMaxHp && _partnerFallSerial < uint.MaxValue)
                {
                    var target = BelayDamageMath.TargetFraction(_partnerFallTrial.Drop, _partnerFallTrial.RopeLength);
                    var credit = Math.Clamp(_partnerFallTrial.ObservedFallHpLoss / sample.MaxHp, 0, 1);
                    if (float.IsFinite(target) && float.IsFinite(credit))
                    {
                        var observation = new PartnerFallObservation(session, ++_partnerFallSerial,
                            _partnerFallTrial.Drop, _partnerFallTrial.RopeLength, credit);
                        _pendingPartnerFall = new() { Observation = observation, Sample = sample,
                            MaximumAdditionalFraction = Math.Max(0, target - credit), Until = now + 10 };
                        ModLog.Info(FormattableString.Invariant($"[BelayDamage] Catch ready; fall={observation.Fall}, height={observation.Height:F3}, referenceLength={observation.Length:F3}, targetFraction={target:F5}, nativeCredit={credit:F5}, approvalWindow=10s; controller budget estimate."));
                    }
                }
            }
            var wall = _pendingPartnerFall == null && _partnerFallTrial.Ready && sample.ObservationAvailable
                && sample.RouteReady && !sample.Falling && !sample.Suspended && !sample.PersonallyAnchored;
            if (_partnerFallTrial.Active || !wall) _partnerWallSince = double.NaN;
            else
            {
                if (double.IsNaN(_partnerWallSince)) _partnerWallSince = now;
                if (now - _partnerWallSince >= .35 && _partnerFallTrial.Arm(sample, now, out _))
                { _partnerTrialMaxHp = sample.MaxHp; _partnerWallSince = double.NaN; }
            }
            if (now >= _nextSharedDamageLog)
            {
                _nextSharedDamageLog = now + 2;
                ModLog.Info(FormattableString.Invariant($"[BelayDamage] Capture state={_partnerFallTrial.Stage}, routeReady={sample.RouteReady}, suspended={sample.Suspended}, nativeCalls={_partnerNativeCalls}; {reason}"));
            }
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("belay.partner-fall-tick", exception);
            ResetPartnerFallCapture();
        }
    }

    private BelayFallObservationSample ReadPartnerFallSample(out string reason)
    {
        reason = "Partner control is unavailable.";
        var session = _lease?.Session ?? Guid.Empty;
        if (!IsPartnerClimber(session)) return default;
        var survival = SurvivalManager.Instance;
        var setup = GameSetup.GetCurrentGameSetup();
        var hp = survival != null ? survival.CurrentHp : float.NaN;
        var maxHp = survival != null ? survival.CurrentMaxHp : float.NaN;
        var available = _sharedDamageInstalled && ReferenceEquals(_sharedDamageOwner, this)
            && setup != null && !setup.UseEnergyInsteadOfHealth
            && float.IsFinite(hp) && hp > 0 && float.IsFinite(maxHp) && maxHp > 0 && hp <= maxHp;
        var recorded = _localStation != null && _localStationPiton != null && LocalStationMatches(_lifeline, out _);
        var length = float.NaN;
        var ready = available && recorded && !_localRoutePending && !_rebinding
            && LocalStationStatus.HasValue && LocalStationStatus.Value.Valid && MappingMatches()
            && TryReadPartnerRoute(out length, out reason);
        var fixedLength = ready ? LocalStationStatus.Value.FixedLength : float.NaN;
        var released = ready ? _ropeControl.Released : float.NaN;
        var total = fixedLength + released;
        if (!float.IsFinite(fixedLength) || fixedLength < 0 || !float.IsFinite(total) || total <= 0 || total > 200)
        { fixedLength = released = total = float.NaN; ready = false; }
        var suspended = _lifeline.SwingHasPivotAndExtremity();
        var taut = false;
        if (available && suspended && _rope?.IsInitialized == true)
        {
            var pivot = _lifeline.SwingPivotInstance;
            if (pivot != null)
            {
                var real = pivot.SwingRealDistance; var limit = pivot.SwingEffectDistance;
                taut = float.IsFinite(real) && real > 0 && float.IsFinite(limit) && limit > 0
                    && real >= limit - .05f && _rope.IsLastRopePartInTension(true);
            }
        }
        if (ready) reason = "Exact native pin path ready.";
        return new(available, true, ready, recorded, _ropeControl.Braked, _lifeline.pawn.IsFalling,
            suspended, taut, PersonalAnchor(_lifeline) != null, Id(_rope), Id(_localStationPiton),
            Id(_lifeline.GetLastSecuringPiton()), _partnerRouteRevision, _lifeline.harness.GetAttachPosition().y,
            length, hp, maxHp, _partnerNativeCalls, _partnerNativeRequested, _partnerNativeLoss,
            float.IsFinite(_restLength) && Math.Abs(_restLength - _ropeControl.Released) <= .05f,
            _ropeControl.Slack, available, recorded, total, fixedLength, released);
    }

    public PartnerFallObservation? PollPartnerFall(Guid session)
    {
        if (!IsPartnerClimber(session) || _pendingPartnerFall == null
            || _pendingPartnerFall.Observation.Session != session || Now > _pendingPartnerFall.Until) return null;
        return _pendingPartnerFall.Observation;
    }

    public PartnerDamageResult ApplyPartnerDamage(PartnerDamageRequest request)
    {
        if (_applyingPartnerDamage || !SharedDamageEnabled || request.Session == Guid.Empty || request.Fall == 0
            || !float.IsFinite(request.Fraction) || request.Fraction < 0
            || request.Fraction > (request.Belayer ? BelayDamageMath.BelayerShare : .95f))
            return RejectPartnerDamage("Invalid, disabled or reentrant partner damage request.");
        var key = (request.Session, request.Fall, request.Belayer);
        if (_partnerDamageAttempts.Contains(key) || _partnerDamageAttempts.Count >= 4096)
            return RejectPartnerDamage("This damage decision was consumed or the bounded history is full.");
        var attempted = false;
        var hpBefore = float.NaN; var maxHp = float.NaN;
        var fraction = request.Fraction;
        SurvivalManager survival = null;
        try
        {
            if (!ContextValid || _rolePreparing || _roleAnchor != null)
                return RejectPartnerDamage("Gameplay or role ownership changed before damage application.");
            var setup = GameSetup.GetCurrentGameSetup();
            survival = SurvivalManager.Instance;
            if (setup == null || setup.UseEnergyInsteadOfHealth || survival == null)
                return RejectPartnerDamage("Partner damage requires normal local health.");
            hpBefore = survival.CurrentHp; maxHp = survival.CurrentMaxHp;
            if (!float.IsFinite(hpBefore) || hpBefore <= 0 || !float.IsFinite(maxHp) || maxHp <= 0 || hpBefore > maxHp)
                return RejectPartnerDamage("Local health is unavailable.");
            var height = 0f;
            if (request.Belayer)
            {
                if (_stationSession != request.Session || !HasStation || !StationValid)
                    return RejectPartnerDamage("The receiving belayer no longer owns this anchored station.");
            }
            else
            {
                var capture = _pendingPartnerFall;
                if (!IsPartnerClimber(request.Session) || capture == null
                    || capture.Observation.Session != request.Session || capture.Observation.Fall != request.Fall
                    || Now > capture.Until || _sharedDamageSession != request.Session)
                    return RejectPartnerDamage("The local catch expired or no longer belongs to this session.");
                var current = ReadPartnerFallSample(out _);
                var previous = capture.Sample;
                if (!current.ContextValid || !current.ObservationAvailable || !current.RouteReady
                    || !current.Suspended || current.PersonallyAnchored || current.MaxHp != maxHp || maxHp != previous.MaxHp
                    || current.Rope != previous.Rope || current.Station != previous.Station
                    || current.LastPiton != previous.LastPiton || current.Revision != previous.Revision
                    || current.DamageCalls != previous.DamageCalls || current.RequestedFallDamage != previous.RequestedFallDamage
                    || current.ObservedFallHpLoss != previous.ObservedFallHpLoss)
                    return RejectPartnerDamage("The captured route, health scale or native fall credit changed during approval.");
                if (fraction > capture.MaximumAdditionalFraction + .0001f)
                    return RejectPartnerDamage("The approved addition exceeds this native catch's remaining target.");
                fraction = Math.Min(fraction, capture.MaximumAdditionalFraction);
                height = capture.Observation.Height;
            }
            // Consume before native callbacks. Neither scene reset nor an
            // exception permits a second application of the same host decision.
            _partnerDamageAttempts.Add(key);
            if (!request.Belayer) _pendingPartnerFall = null;
            if (fraction <= 0)
                return new(false, true, 0, 0, hpBefore, hpBefore, "No additional damage was needed; decision consumed.");
            attempted = true;
            _applyingPartnerDamage = true;
            try { survival.InflictDamage(fraction * maxHp, DamageSource.Fall, height, null); }
            finally { _applyingPartnerDamage = false; }
            var hpAfter = ReadPartnerDamageHealth(survival);
            var observed = float.IsFinite(hpAfter) ? Math.Clamp((hpBefore - hpAfter) / maxHp, 0, 1) : float.NaN;
            ModLog.Info(FormattableString.Invariant($"[BelayDamage] Native dose complete; fall={request.Fall}, belayer={request.Belayer}, requestedFraction={fraction:F5}, observedFraction={observed:F5}, hpBefore={hpBefore:F3}, hpAfter={hpAfter:F3}"));
            return new(true, true, fraction, observed, hpBefore, hpAfter,
                float.IsFinite(hpAfter) ? "Native partner damage call completed; game protections apply."
                    : "Native partner damage call completed; health readback unavailable.");
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("belay.partner-damage-apply", exception);
            var hpAfter = attempted ? ReadPartnerDamageHealth(survival) : float.NaN;
            var observed = attempted && float.IsFinite(hpAfter) && maxHp > 0
                ? Math.Clamp((hpBefore - hpAfter) / maxHp, 0, 1) : float.NaN;
            return new(attempted, false, attempted ? fraction : 0, observed, hpBefore, hpAfter,
                "Partner damage was interrupted; no retry of a consumed decision.");
        }
    }

    private static PartnerDamageResult RejectPartnerDamage(string reason)
        => new(false, false, 0, float.NaN, float.NaN, float.NaN, reason);

    private static float ReadPartnerDamageHealth(SurvivalManager expected)
    {
        try
        {
            var current = SurvivalManager.Instance;
            if (expected == null || current == null || Id(current) != Id(expected)) return float.NaN;
            var hp = expected.CurrentHp; var maxHp = expected.CurrentMaxHp;
            return float.IsFinite(maxHp) && maxHp > 0 && float.IsFinite(hp) && hp >= 0 && hp <= maxHp ? hp : float.NaN;
        }
        catch (Exception exception)
        { ModLog.SuppressedException("belay.partner-damage-health", exception); return float.NaN; }
    }

    private static void BeforePartnerNativeDamage(SurvivalManager __instance, DamageSource __1, out PartnerNativeDamageState __state)
    {
        __state = null;
        try
        {
            var owner = _sharedDamageOwner;
            if (__1 != DamageSource.Fall || owner == null || owner._applyingPartnerDamage || __instance == null
                || !owner.IsPartnerClimber(owner._sharedDamageSession)) return;
            var current = SurvivalManager.Instance;
            var setup = GameSetup.GetCurrentGameSetup();
            if (current == null || Id(current) != Id(__instance) || setup == null || setup.UseEnergyInsteadOfHealth) return;
            var hp = __instance.CurrentHp;
            if (!float.IsFinite(hp) || hp <= 0) return;
            __state = new() { Owner = owner, Session = owner._sharedDamageSession, Survival = Id(__instance), Hp = hp };
            owner._sharedDamageCallbackDepth++;
        }
        catch (Exception exception) { ModLog.SuppressedException("belay.partner-fall-before", exception); }
    }

    private static void AfterPartnerNativeDamage(SurvivalManager __instance, float __0, DamageSource __1, PartnerNativeDamageState __state)
    {
        var owner = __state?.Owner;
        if (owner == null) return;
        try
        {
            if (__1 != DamageSource.Fall || __instance == null || Id(__instance) != __state.Survival
                || owner._sharedDamageSession != __state.Session) return;
            var hp = ReadPartnerDamageHealth(__instance);
            if (!float.IsFinite(hp)) return;
            if (owner._partnerNativeCalls < uint.MaxValue) owner._partnerNativeCalls++;
            owner._partnerNativeRequested = Math.Min(1_000_000, owner._partnerNativeRequested + (float.IsFinite(__0) && __0 > 0 ? __0 : 0));
            owner._partnerNativeLoss = Math.Min(1_000_000, owner._partnerNativeLoss + Math.Max(0, __state.Hp - hp));
        }
        catch (Exception exception) { ModLog.SuppressedException("belay.partner-fall-after", exception); }
        finally { if (owner._sharedDamageCallbackDepth > 0) owner._sharedDamageCallbackDepth--; }
    }

    private void ResetPartnerFallCapture()
    {
        if (ReferenceEquals(_sharedDamageOwner, this)) _sharedDamageOwner = null;
        _sharedDamageSession = Guid.Empty;
        _partnerFallTrial = null; _pendingPartnerFall = null;
        _partnerReportedAttempt = -1; _partnerWallSince = double.NaN;
        _partnerNativeCalls = 0; _partnerNativeRequested = _partnerNativeLoss = 0;
        if (_sharedDamageInstalled) _sharedDamageUnpatchPending = true;
    }

    private void ShutdownSharedDamage()
    {
        ResetPartnerFallCapture();
        if (_sharedDamageInstalled && !_applyingPartnerDamage && _sharedDamageCallbackDepth == 0)
        {
            _sharedDamageHarmony.UnpatchSelf();
            _sharedDamageInstalled = _sharedDamageUnpatchPending = false;
        }
    }
}
