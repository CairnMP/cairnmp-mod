using System;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Game.Roping;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;
using NumericsVector3 = System.Numerics.Vector3;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Remote-input experiment: a climber retains their own real protection route.
/// This does not reroute their rope through the belayer's station or create a tether.
/// </summary>
internal sealed partial class ManualBelayAdapter : IManualBelayApi
{
    private static ManualBelayAdapter _owner;
    private readonly Func<bool> _remoteEnabled;
    private readonly HarmonyLib.Harmony _harmony = new("CairnMultiplayerMod.ManualBelay");
    private bool _installed, _stopping, _holdApplied;
    private Lifeline _lifeline, _stationLifeline;
    private LogicalRope _rope;
    private long _piton, _pivot, _stationPiton;
    private Vector3 _stationPosition;
    private BelayStationAnchor _stationAnchor;
    private BelayBrakeControl _control;
    private BelayInputLease _lease;
    private uint _appliedSequence;
    private float _actual, _real;
    private double _started;
    private bool _hasLoggedApplication, _lastLoggedBrake;
    private BelayTopologyTracker _topology;
    private double _nextTopologyPoll, _lastValidTopology;
    private bool _rebinding, _localExercise;
    private uint _localSequence;
    private double _nextLocalIntent;
    private Vector3 _localPreviewStation;
    public SharedBelayRoutePreview? LocalRoutePreview { get; private set; }
    internal static bool IsControlling => _owner?._control != null;

    internal ManualBelayAdapter(Func<bool> remoteEnabled)
        => _remoteEnabled = remoteEnabled ?? throw new ArgumentNullException(nameof(remoteEnabled));

    public bool Enabled => _remoteEnabled();
    public ManualBelayStatus Status => new(_control != null && !_stopping, _control?.Attached == true,
        _control?.Braked == true, _lease?.Sequence ?? 0, _appliedSequence, _actual, _real,
        _topology?.Current?.ProtectionCount ?? 0, _topology?.Revision ?? 0, _rebinding);
    public bool IsLocalExercise => _localExercise && _control != null;
    public bool HasStation => _stationAnchor != null;

    private static double Now => Time.realtimeSinceStartupAsDouble;
    private static long Id(UnityEngine.Object value) => value == null ? 0 : value.Pointer.ToInt64();
    private static Lifeline LocalLifeline()
    {
        var player = LocalPlayerInterop.TryGetMCGameObject();
        return player?.GetComponent<Lifeline>() ?? player?.GetComponentInChildren<Lifeline>(true);
    }

    private static bool ContextValid => TryGetActiveContext(out _);

    private static bool TryGetActiveContext(out string reason)
    {
        reason = "Return focus to the game before starting the exercise.";
        if (!Application.isFocused) return false;
        reason = "Unpause the game before starting the exercise.";
        if (!(Time.timeScale > 0)) return false;
        reason = "Wait for the local character to finish loading.";
        var survival = SurvivalManager.Instance;
        if (survival == null) return false;
        reason = "The character must be alive before starting the exercise.";
        if (!(survival.CurrentHp > 0)) return false;
        reason = "";
        return true;
    }

    public bool BeginStation(out string reason)
    {
        if (HasStation)
        {
            if (StationValid) { reason = "Station already defined on your current anchor."; return true; }
            EndStation();
            reason = "The previous station is no longer anchored. Define the current anchor again.";
            return false;
        }
        if (!CanBeginStation(out reason)) return false;
        var lifeline = LocalLifeline();
        var piton = lifeline.OffBelayPiton;
        var position = lifeline.harness.GetAttachPosition();
        var anchor = new BelayStationAnchor(Id(lifeline), Id(lifeline.securingRope), Id(piton), ToNumerics(piton.transform.position));
        _stationLifeline = lifeline;
        _stationPiton = Id(piton);
        _stationPosition = position;
        _stationAnchor = anchor;
        ModLog.Info("[BelayStation] Defined on the player's existing native piton anchor.");
        reason = "Station defined. Remain anchored; you can remove the station in the partner panel.";
        return true;
    }

    public bool CanBeginStation(out string reason)
    {
        reason = "Anchor yourself on a piton and remain still.";
        if (!Enabled || !ContextValid || _control != null || RopeInterop.IsNativeBelayEngaged) return false;
        var lifeline = LocalLifeline();
        var piton = lifeline != null ? lifeline.OffBelayPiton : null;
        return piton != null && piton.pitonHp > 0 && lifeline.securingRope != null
            && lifeline.pawn != null && lifeline.harness != null
            && !lifeline.pawn.IsFalling && Vector3.Distance(lifeline.harness.GetAttachPosition(), piton.transform.position) <= 1.5f;
    }

    public bool CanClimb(out string reason)
    {
        reason = "Enable the belay experiment first.";
        if (!(Enabled || LocalExerciseEnabled)) return false;
        if (!TryGetActiveContext(out reason)) return false;
        reason = "End the current rope control before starting another.";
        if (_control != null) return false;
        reason = "Remove your belay station before becoming the climber.";
        if (HasStation) return false;
        reason = "Disconnect the existing cooperative rope first.";
        if (RopeInterop.IsNativeBelayEngaged) return false;
        var lifeline = LocalLifeline();
        return TrySnapshot(lifeline, out _, out reason);
    }

    public bool StationValid
    {
        get
        {
            try
            {
                if (!HasStation || !Enabled || !ContextValid || _stationLifeline == null || _stationLifeline.pawn == null
                    || _stationLifeline.harness == null || RopeInterop.IsNativeBelayEngaged) return false;
                var piton = _stationLifeline.OffBelayPiton;
                return piton != null && Id(piton) == _stationPiton && piton.pitonHp > 0
                    && _stationAnchor.Matches(Id(_stationLifeline), Id(_stationLifeline.securingRope), Id(piton),
                        piton.pitonHp, ToNumerics(piton.transform.position))
                    && !_stationLifeline.pawn.IsFalling
                    && Vector3.Distance(_stationLifeline.harness.GetAttachPosition(), _stationPosition) <= .25f;
            }
            catch (Exception ex)
            {
                ModLog.SuppressedException("belay.station-validity", ex);
                return false;
            }
        }
    }

    public void EndStation()
    {
        if (HasStation) ModLog.Info("[BelayStation] Removed; personal native anchoring unchanged.");
        _stationLifeline = null; _stationPiton = 0; _stationAnchor = null;
    }

    public bool BeginClimber(Guid session, out string reason)
        => BeginClimber(session, false, out reason);

    private bool BeginClimber(Guid session, bool localExercise, out string reason)
    {
        reason = "Enable the selected belay experiment first.";
        if (!(localExercise ? LocalExerciseEnabled : Enabled)) return false;
        reason = "The belay session identifier is invalid.";
        if (session == Guid.Empty) return false;
        if (!TryGetActiveContext(out reason)) return false;
        reason = "End the current rope control before starting another.";
        if (_control != null || (_owner != null && !ReferenceEquals(_owner, this))) return false;
        reason = "End your anchored belayer role before controlling the climbing rope.";
        if (_stationLifeline != null) return false;
        reason = "Disconnect the existing cooperative rope before starting this exercise.";
        if (RopeInterop.IsNativeBelayEngaged) return false;
        var lifeline = LocalLifeline();
        if (!TrySnapshot(lifeline, out var snapshot, out reason)) return false;
        if (!TryPrepareLocalRope(lifeline, snapshot, localExercise, out var ropeControl, out reason)) return false;
        Install();
        _lifeline = lifeline; _rope = lifeline.securingRope;
        _topology = new BelayTopologyTracker();
        _topology.Observe(snapshot, false, Now);
        BindTopology(snapshot);
        _lease = new BelayInputLease(session, Now);
        _started = Now;
        _stopping = _holdApplied = false;
        _hasLoggedApplication = false;
        _appliedSequence = 0; _actual = _real = 0;
        _pivot = lifeline.SwingHasPivotAndExtremity() ? Id(lifeline.SwingPivotInstance) : 0;
        _localExercise = localExercise; _localSequence = 0; _nextLocalIntent = 0;
        _ropeControl = ropeControl;
        _localRopeNotice = null;
        _climbLimitQueries = _climbLimitBlocks = 0;
        _climbMovementBlocked = false;
        _nextClimbLimitLog = 0;
        _nextRopeSample = Now;
        if (_ropeControl != null)
        {
            _localObiRope = Id(_rope.ropeParts[_rope.ropeParts.Count - 1].obiRope);
            ModLog.Info("[BelayRope] Started; capacity=" + _ropeControl.Capacity + ", released=" + _ropeControl.Released);
        }
        LocalRoutePreview = null;
        if (localExercise)
        {
            var firstPiton = lifeline.GetFirstSecuringPiton();
            _localPreviewStation = firstPiton != null ? firstPiton.transform.position : lifeline.harness.GetAttachPosition();
        }
        _nextTopologyPoll = _lastValidTopology = Now;
        _owner = this;
        if (localExercise && _localStation != null)
            ModLog.Info("[BelayStation] Rehearsal started on the captured native route; no partner endpoint attached.");
        ModLog.Info("[ManualBelay] Acquired protection; session=" + session + ", end=" + snapshot.Available
            + ", pitons=" + snapshot.ProtectionCount);
        reason = "";
        return true;
    }

    public bool ToggleLocalExercise(out string reason)
    {
        if (IsLocalExercise) { Stop(_lease.Session); reason = "Local exercise ended."; return true; }
        return BeginClimber(Guid.NewGuid(), true, out reason);
    }

    public void ToggleLocalBrake()
    {
        if (!IsLocalExercise || _stopping || _localSequence == uint.MaxValue) return;
        if (_ropeControl != null && !StepLocalRope(0, _pivot != 0)) return;
        SetBrake(_lease.Session, ++_localSequence, !_control.Braked);
    }

    public bool TryPreviewRoute(WorldPosition station, out SharedBelayRoutePreview preview)
    {
        preview = default;
        if (_control == null || _rebinding || !MappingMatches()) return false;
        var points = new System.Collections.Generic.List<BelayProtectionPoint>();
        foreach (var holder in _rope.ropeHolders)
        {
            var quickdraw = holder.TryCast<LogicalQuickdraw>();
            if (quickdraw?.Piton == null) continue;
            var position = holder.Position;
            points.Add(new BelayProtectionPoint(unchecked((uint)quickdraw.Piton.GetInstanceID()), new NumericsVector3(position.x, position.y, position.z)));
        }
        var body = _lifeline.harness.GetAttachPosition();
        if (!BelayRoutePlanner.TryPlan(new NumericsVector3(station.X, station.Y, station.Z), points,
            new NumericsVector3(body.x, body.y, body.z), _rope.MaxLengthMeters, out var plan)) return false;
        preview = new SharedBelayRoutePreview(plan.FixedLength, plan.AvailableToClimber,
            plan.ClimberDistance, plan.ProtectionCount, plan.ReachesClimber);
        return true;
    }

    private static bool TrySnapshot(Lifeline lifeline, out BelayTopologySnapshot snapshot)
        => TrySnapshot(lifeline, out snapshot, out _);

    private static bool TrySnapshot(Lifeline lifeline, out BelayTopologySnapshot snapshot, out string reason)
    {
        snapshot = null;
        reason = "The local character's lifeline is not available yet.";
        if (lifeline == null) return false;
        var rope = lifeline.securingRope;
        reason = "Wait for the protection rope to initialize.";
        if (rope == null || !rope.IsInitialized) return false;
        reason = "The character or harness is not available yet.";
        if (lifeline.pawn == null || lifeline.harness == null) return false;
        reason = "Connect your protection rope to a piton first.";
        if (!lifeline.IsSecured(1)) return false;
        var piton = lifeline.GetLastSecuringPiton();
        reason = "The last protection piton is missing or broken.";
        if (piton == null || piton.pitonHp <= 0) return false;
        var partCount = rope.ropeParts?.Count ?? 0;
        if (partCount < 2 || partCount > 64)
        {
            reason = $"Protection route is not ready: {partCount} segments (expected 2 to 64).";
            return false;
        }
        var holderCount = rope.ropeHolders?.Count ?? 0;
        if (holderCount < 1 || holderCount > 66)
        {
            reason = $"Protection route is not ready: {holderCount} holders (expected 1 to 66).";
            return false;
        }
        var parts = new long[rope.ropeParts.Count];
        for (var i = 0; i < parts.Length; i++) parts[i] = Id(rope.ropeParts[i]);
        var last = rope.ropeParts[parts.Length - 1];
        reason = "Wait for the final protection segment's physics to load.";
        if (last?.obiRope == null || !last.obiRope.isLoaded) return false;
        var holders = new long[rope.ropeHolders.Count];
        var protectionCount = 0;
        for (var i = 0; i < holders.Length; i++)
        {
            var holder = rope.ropeHolders[i];
            if (holder == null)
            {
                reason = $"Protection route is updating: holder {i + 1} of {holders.Length} is unavailable.";
                return false;
            }
            holders[i] = holder.Pointer.ToInt64();
            if (holder.TryCast<LogicalQuickdraw>()?.Piton != null) protectionCount++;
        }
        var available = rope.GetRemainingSlackMeters(false);
        if (!float.IsFinite(rope.MaxLengthMeters) || available > rope.MaxLengthMeters)
        {
            reason = $"Invalid rope capacity: remaining {available:F2} m, maximum {rope.MaxLengthMeters:F2} m.";
            return false;
        }
        snapshot = new BelayTopologySnapshot(Id(rope), Id(piton), parts, holders, available, protectionCount);
        if (!snapshot.TryValidate(out var validationReason))
        {
            reason = validationReason + $" Route: {protectionCount} pitons, {parts.Length} segments, "
                + $"{holders.Length} holders, {available:F2} m remaining.";
            return false;
        }
        reason = "";
        return true;
    }

    private void BindTopology(BelayTopologySnapshot snapshot)
    {
        var braked = _control?.Braked == true;
        _control = new BelayBrakeControl(snapshot.Available);
        if (braked) _control.ToggleBrake();
        _piton = snapshot.LastPiton;
        _appliedSequence = 0; _actual = _real = 0; _hasLoggedApplication = false;
        _rebinding = false;
    }

    public bool SetBrake(Guid session, uint sequence, bool braked)
    {
        if (_control == null || _stopping || !_lease.TryAccept(session, sequence, braked, Now)) return false;
        if (_control.Braked != braked)
        {
            _control.ToggleBrake();
            _ropeControl?.SetBrake(braked);
            if (!braked) _climbMovementBlocked = false;
            ModLog.Info("[ManualBelay] Requested session=" + session + ", sequence=" + sequence + ", brake=" + braked);
        }
        return true;
    }

    public void Stop(Guid session)
    {
        if (_control == null || _lease.Session != session || _stopping) return;
        _stopping = true;
        if (_ropeControl != null)
        {
            ReleaseLocalRopeGoal();
            _localRopeNotice ??= "Local rope exercise ended. Native rope control resumed; restart only when stable.";
            // Ending the experiment hands off at current geometry, never at a
            // previously paid-out length that could pull the suspended body.
            _control = new BelayBrakeControl(_control.Available);
            _ropeControl = null;
        }
        ModLog.Info("[ManualBelay] Holding and ending session=" + session);
        // While suspended, catch at current geometry on the next physics step,
        // then drop ownership. Never restore the older, shorter joint radius.
        if (_control.Braked == false) _control.ToggleBrake();
        if (_pivot == 0) ClearControl();
    }

    public void Tick()
    {
        if (HasStation && !StationValid) EndStation();
        if (_unpatchPending && _control == null)
        {
            _harmony.UnpatchSelf();
            _installed = _unpatchPending = false;
        }
        if (_control == null) return;
        try
        {
            if (_holdApplied || !RopeIdentityValid()) { ClearControl(); return; }
            var hasSwing = _lifeline.SwingHasPivotAndExtremity();
            if (_pivot != 0 && !hasSwing)
            {
                if (_stopping || !_topology.RearmAfterSuspension()) { ClearControl(); return; }
                // A completed catch returns to wall climbing. Keep consent/intent,
                // but never reuse the old joint or its radius for the next fall.
                _pivot = 0;
                _ropeControl?.EndSuspension();
                BindTopology(_topology.Current);
            }
            else if (_pivot != 0 && Id(_lifeline.SwingPivotInstance) != _pivot) { ClearControl(); return; }
            if (Now >= _nextTopologyPoll)
            {
                _nextTopologyPoll = Now + .1;
                if (!TrySnapshot(_lifeline, out var snapshot))
                {
                    _rebinding = true;
                    if (hasSwing || Now - _lastValidTopology > 1) { ClearControl(); return; }
                }
                else
                {
                    var change = _topology.Observe(snapshot, hasSwing, Now);
                    if (change == BelayTopologyChange.Invalid) { ClearControl(); return; }
                    _lastValidTopology = Now;
                    _rebinding = change == BelayTopologyChange.Settling;
                    if (change == BelayTopologyChange.Rebound)
                    {
                        if (_ropeControl != null)
                        {
                            EndLocalRope("Protection route changed. Restart the fixed-route exercise when stable.");
                            return;
                        }
                        BindTopology(snapshot);
                        ModLog.Info("[ManualBelay] Protection route rebound; revision=" + _topology.Revision
                            + ", pitons=" + snapshot.ProtectionCount + ", end=" + snapshot.Available);
                    }
                    if (!ObserveLocalRopeCapacity(snapshot.Available)) return;
                }
                if (_localExercise)
                    LocalRoutePreview = TryPreviewRoute(new WorldPosition(_localPreviewStation.x, _localPreviewStation.y, _localPreviewStation.z), out var plan)
                        ? plan : null;
            }
            if (!(_localExercise ? LocalExerciseEnabled : Enabled)
                || !ContextValid || _lease.IsExpired(Now) || Now - _started >= 120)
            { Stop(_lease.Session); return; }
            if (_ropeControl != null)
            {
                if (ModConfig.BelayRopeExperiment?.Value != true) { Stop(_lease.Session); return; }
                if (!hasSwing && !StepLocalRope(Time.deltaTime, false)) return;
                SampleLocalRope();
            }
            if (_localExercise && !_stopping && Now >= _nextLocalIntent)
            {
                _nextLocalIntent = Now + .5;
                if (_localSequence == uint.MaxValue) { Stop(_lease.Session); return; }
                SetBrake(_lease.Session, ++_localSequence, _control.Braked);
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private bool RopeIdentityValid()
    {
        if (_lifeline == null || _rope == null || Id(_lifeline.securingRope) != Id(_rope)
            || RopeInterop.IsNativeBelayEngaged) return false;
        return true;
    }

    private bool MappingMatches()
        => MappingMatches(out _);

    private bool MappingMatches(out string reason)
    {
        var route = _topology?.Current;
        reason = "Protection snapshot unavailable.";
        if (route == null) return false;
        reason = "Protection route is settling.";
        if (_rebinding) return false;
        reason = "Native rope identity or cooperative-rope ownership changed.";
        if (!RopeIdentityValid()) return false;
        reason = "Native lifeline no longer reports a securing piton.";
        if (!_lifeline.IsSecured(1)) return false;
        reason = "Last securing piton changed.";
        if (Id(_lifeline.GetLastSecuringPiton()) != route.LastPiton) return false;
        if (_localExercise && !LocalStationMatches(_lifeline, out reason)) return false;
        reason = "Protection segment count changed.";
        if (_rope.ropeParts?.Count != route.Parts.Length) return false;
        reason = "Protection holder count changed.";
        if (_rope.ropeHolders?.Count != route.Holders.Length) return false;
        for (var i = 0; i < route.Parts.Length; i++)
            if (Id(_rope.ropeParts[i]) != route.Parts[i]) { reason = $"Protection segment {i + 1} changed."; return false; }
        for (var i = 0; i < route.Holders.Length; i++)
            if (_rope.ropeHolders[i] == null || _rope.ropeHolders[i].Pointer.ToInt64() != route.Holders[i])
            { reason = $"Protection holder {i + 1} changed."; return false; }
        reason = "";
        return true;
    }

    private void Install()
    {
        if (_installed) return;
        try
        {
            _harmony.Patch(AccessTools.Method(typeof(Lifeline), nameof(Lifeline.SwingSetEffectDistance)),
                prefix: new HarmonyMethod(typeof(ManualBelayAdapter), nameof(BeforeDistance)));
            _harmony.Patch(AccessTools.Method(typeof(SwingPivot), nameof(SwingPivot.FixedUpdate)),
                postfix: new HarmonyMethod(typeof(ManualBelayAdapter), nameof(AfterPhysics)));
            _harmony.Patch(AccessTools.Method(typeof(Piton), nameof(Piton.ShouldDetach)),
                postfix: new HarmonyMethod(typeof(ManualBelayAdapter), nameof(AfterPitonDecision)));
            _harmony.Patch(AccessTools.Method(typeof(ObiRopeLengthController), nameof(ObiRopeLengthController.SetRopeLengthInstant)),
                prefix: new HarmonyMethod(typeof(ManualBelayAdapter), nameof(BeforeLocalRopeLength)),
                postfix: new HarmonyMethod(typeof(ManualBelayAdapter), nameof(AfterLocalRopeLength)));
            _harmony.Patch(AccessTools.Method(typeof(ObiRopeLengthController), nameof(ObiRopeLengthController.RefreshRopeLength)),
                prefix: new HarmonyMethod(typeof(ManualBelayAdapter), nameof(BeforeLocalRopeRefresh)),
                postfix: new HarmonyMethod(typeof(ManualBelayAdapter), nameof(AfterLocalRopeLength)));
            _harmony.Patch(AccessTools.Method(typeof(Harness), nameof(Harness.LifelineCanGoToward)),
                postfix: new HarmonyMethod(typeof(ManualBelayAdapter), nameof(AfterClimbingPermission)));
            _installed = true;
        }
        catch { _harmony.UnpatchSelf(); throw; }
    }

    private static void AfterPitonDecision(Piton __instance, bool __result)
    {
        var owner = _owner;
        if (__result && owner?._control != null && Id(__instance) == owner._piton)
            owner.ClearControl(); // Observe the native decision once; never reroll it.
    }

    private static void BeforeDistance(Lifeline __instance, ref float __0)
    {
        var owner = _owner;
        if (owner?._control == null || owner._control.Attached || Id(__instance) != Id(owner._lifeline)) return;
        try
        {
            if (!owner.MappingMatches() || !TrySnapshot(__instance, out var snapshot)) { owner.ClearControl(); return; }
            if (!owner.ObserveLocalRopeCapacity(snapshot.Available)) return;
            // Refresh the geometric stock once when a new suspension starts.
            // Never chase a moving capacity estimate while the body is loaded.
            owner.BindTopology(snapshot);
            var pivot = __instance.SwingPivotInstance;
            if (pivot == null) return;
            var real = pivot.SwingRealDistance;
            if (!float.IsFinite(real) || real <= 0) real = __0;
            if (!owner._control.TryAttach(__0, real, out var target)) { owner.ClearControl(); return; }
            owner._pivot = Id(pivot);
            if (owner._ropeControl != null && !owner.TryBeginRopeSuspension(real, out target)) return;
            __0 = target;
        }
        catch (Exception ex) { owner.Fail(ex); }
    }

    private static void AfterPhysics(SwingPivot __instance)
    {
        var owner = _owner;
        if (owner?._control == null || Id(__instance) != owner._pivot) return;
        try
        {
            if (!owner.MappingMatches() || !owner._lifeline.SwingHasPivotAndExtremity()
                || Id(owner._lifeline.SwingPivotInstance) != owner._pivot) { owner.ClearControl(); return; }
            if (owner._lease.IsExpired(Now)) owner.Stop(owner._lease.Session);
            var real = __instance.SwingRealDistance;
            float target;
            var valid = owner._ropeControl != null
                ? owner.TryStepRopeSuspension(real, out target)
                : owner._control.Attached ? owner._control.TryStep(real, out target)
                    : owner._control.TryAttach(Math.Min(real, owner._control.Available), real, out target);
            if (!valid) { owner.ClearControl(); return; }
            if (Math.Abs(__instance.SwingEffectDistance - target) > .001f) __instance.SetSwingEffectDistance(target);
            owner._real = real;
            owner._actual = __instance.SwingEffectDistance;
            if (float.IsFinite(owner._actual) && Math.Abs(owner._actual - target) <= .01f)
            {
                owner._appliedSequence = owner._lease.Sequence;
                if (!owner._hasLoggedApplication || owner._lastLoggedBrake != owner._control.Braked)
                {
                    owner._hasLoggedApplication = true;
                    owner._lastLoggedBrake = owner._control.Braked;
                    ModLog.Info("[ManualBelay] Applied session=" + owner._lease.Session + ", sequence=" + owner._appliedSequence
                        + ", brake=" + owner._control.Braked + ", limit=" + owner._actual + ", real=" + real);
                }
            }
            if (owner._stopping) owner._holdApplied = true;
        }
        catch (Exception ex) { owner.Fail(ex); }
    }

    private void Fail(Exception ex)
    {
        ModLog.Warning("[ManualBelay] Native control stopped: " + ex.Message);
        ClearControl();
    }

    private void ClearControl()
    {
        ReleaseLocalRopeGoal();
        if (ReferenceEquals(_owner, this)) _owner = null;
        _control = null; _lease = null; _lifeline = null; _rope = null; _pivot = 0;
        if (_ropeControl != null)
        {
            _localRopeNotice ??= "Rope exercise ended because native ownership changed. Check protection before restarting.";
            ModLog.Info("[BelayRope] Ended; " + _localRopeNotice);
        }
        _ropeControl = null; _localObiRope = 0;
        _topology = null; _localExercise = _rebinding = false;
        LocalRoutePreview = null;
        _stopping = _holdApplied = false;
        // Unpatch outside a native callback; Tick continues even outside multiplayer.
        if (_installed) _unpatchPending = true;
    }

    private bool _unpatchPending;
    public void ResetScene() { EndStation(); ClearControl(); ClearLocalStation(); }
    public void Shutdown()
    {
        ResetScene();
        if (_installed) _harmony.UnpatchSelf();
        _installed = _unpatchPending = false;
    }
}
