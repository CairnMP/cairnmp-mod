using System;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

internal sealed partial class ManualBelayFeature
{
    private const float DamageReceiptSeconds = 10;
    private const float MinimumDamageClaimSeconds = 1;
    private const float DamageFractionTolerance = .001f;
    private HostCommand<ManualBelayDamageClaim> _damageClaim;
    private HostCommand<ManualBelayDamageReceipt> _damageReceipt;
    private HostEvent<ManualBelayDamageMessage> _damageChanged;
    private sealed class PendingSharedDamage
    {
        internal Guid Session;
        internal uint Fall;
        internal int Climber, Belayer;
        internal float NativeCredit, Extra, CreatedAt, Until;
    }
    private PendingSharedDamage _pendingSharedDamage;
    private Guid _hostDamageSession, _peerDamageSession;
    private float _hostDamageClaimAt = float.NaN;
    private uint _hostDamageFall, _localDamageFall, _approvedDamageFall, _belayerDamageFall;
    private PartnerFallObservation? _localDamageObservation;
    private float _localDamageUntil, _belayerApprovalUntil;
    private uint _belayerApprovedFall;
    private ManualBelayDamageMessage _queuedClimberDamage, _queuedBelayerDamage;

    private void RegisterPartnerDamage(FeatureBuilder feature)
    {
        _damageClaim = feature.HostCommand<ManualBelayDamageClaim>("damage-claim", JudgeDamageClaim);
        _damageReceipt = feature.HostCommand<ManualBelayDamageReceipt>("damage-receipt", JudgeDamageReceipt);
        _damageChanged = feature.HostEvent<ManualBelayDamageMessage>("damage-approved", (_, message) => ReceivePartnerDamage(message));
    }

    private bool DamageGameplayActive(float now)
        => float.IsFinite(now) && now >= 0 && IsBelayMode && IsConnected && Game.Belay.SharedDamageEnabled
            && Game.State.IsLocalPlayerInGame && Game.Input.IsWindowFocused && !Game.Input.IsPauseMenuActive
            && !Game.Time.IsPaused && !Game.State.IsLocalPlayerInCutscene;

    private bool TryDamageSession(Guid id, out SessionState session)
    {
        session = _hostSession;
        var now = Game.Time.UnscaledTime;
        return IsHost && DamageGameplayActive(now) && session != null && session.Id == id && session.Started
            && session.Swap == null && now < session.Until && ValidCapacity(session.RopeCapacity)
            && TryLocations(session.Belayer, session.Climber, out var station, out var climber)
            && ValidStation(session.Station, station) && WithinRopeRange(station, climber, session.RopeCapacity);
    }

    private bool HasPendingSharedDamage(Guid session)
        => _pendingSharedDamage?.Session == session && float.IsFinite(Game.Time.UnscaledTime)
            && Game.Time.UnscaledTime >= _pendingSharedDamage.CreatedAt && Game.Time.UnscaledTime <= _pendingSharedDamage.Until;

    private void JudgeDamageClaim(HostRequest<ManualBelayDamageClaim> request)
    {
        var claim = request.Message;
        if (!TryDamageSession(claim.Session, out var session) || request.FromPlayerId != session.Climber)
        { request.Reject("Shared damage requires the current climber and an active unchanged pair."); return; }
        var now = Game.Time.UnscaledTime;
        if (_hostDamageSession != session.Id)
        { _hostDamageSession = session.Id; _hostDamageFall = 0; _hostDamageClaimAt = float.NaN; _pendingSharedDamage = null; }
        if (_pendingSharedDamage != null && !HasPendingSharedDamage(session.Id)) _pendingSharedDamage = null;
        if (float.IsFinite(_hostDamageClaimAt) && now < _hostDamageClaimAt)
        { request.Reject("The shared damage claim clock reversed."); return; }
        if (float.IsFinite(_hostDamageClaimAt) && now - _hostDamageClaimAt < MinimumDamageClaimSeconds)
        { request.Reject("Wait one second between accepted fall claims."); return; }
        if (claim.Fall == 0 || claim.Fall <= _hostDamageFall || _pendingSharedDamage != null
            || !float.IsFinite(claim.Height) || claim.Height < .25f || claim.Height > 2 * session.RopeCapacity + BodyMargin
            || !float.IsFinite(claim.Length) || claim.Length <= 0 || claim.Length > session.RopeCapacity + BodyMargin
            || !FractionValid(claim.NativeLossFraction, 1))
        { request.Reject("The fall claim was repeated, pending, or outside its measured bounds."); return; }
        var extra = Math.Max(0, BelayDamageMath.TargetFraction(claim.Height, claim.Length) - claim.NativeLossFraction);
        if (!FractionValid(extra, .95f)) { request.Reject("The shared damage calculation was invalid."); return; }
        request.AfterCommit(() =>
        {
            // Reserve authority before publication: delivery may synchronously invoke a peer.
            _hostDamageFall = claim.Fall;
            _hostDamageClaimAt = now;
            _pendingSharedDamage = new() { Session = session.Id, Fall = claim.Fall, Climber = session.Climber,
                Belayer = session.Belayer, NativeCredit = claim.NativeLossFraction, Extra = extra,
                CreatedAt = now, Until = now + DamageReceiptSeconds };
            _damageChanged.Send(new() { Session = session.Id, Fall = claim.Fall, Climber = session.Climber,
                Belayer = session.Belayer, Fraction = extra, Phase = ManualBelayDamagePhase.ClimberApproval });
        });
    }

    private void JudgeDamageReceipt(HostRequest<ManualBelayDamageReceipt> request)
    {
        var receipt = request.Message;
        var pending = _pendingSharedDamage;
        if (!TryDamageSession(receipt.Session, out var session) || pending == null
            || !HasPendingSharedDamage(receipt.Session) || pending.Session != receipt.Session || pending.Fall != receipt.Fall
            || request.FromPlayerId != session.Climber || pending.Climber != session.Climber || pending.Belayer != session.Belayer
            || !FractionValid(receipt.ObservedFraction, pending.Extra + DamageFractionTolerance)
            || (!receipt.Completed && receipt.ObservedFraction != 0))
        { request.Reject("The damage receipt was unauthorized, stale, repeated, or outside its approval."); return; }
        request.AfterCommit(() =>
        {
            _pendingSharedDamage = null;
            if (!receipt.Completed)
            { FeatureLog.Info("[BelayDamage] Climber application incomplete; no shared dose; session=" + receipt.Session + ", fall=" + receipt.Fall); return; }
            var added = Math.Min(receipt.ObservedFraction, pending.Extra);
            var dose = BelayDamageMath.BelayerShare * Math.Clamp(pending.NativeCredit + added, 0, 1);
            _damageChanged.Send(new() { Phase = ManualBelayDamagePhase.BelayerDose, Session = pending.Session,
                Fall = pending.Fall, Climber = pending.Climber, Belayer = pending.Belayer, Fraction = dose });
        });
    }

    private static bool FractionValid(float value, float maximum)
        => float.IsFinite(value) && value >= 0 && value <= maximum;

    private void EnsurePeerDamageSession(Guid session)
    {
        if (_peerDamageSession == session) return;
        ClearPeerPartnerDamage();
        _peerDamageSession = session;
    }

    private bool LocalDamageContext(Guid session)
    {
        var now = Game.Time.UnscaledTime;
        // The piton registry is host-only. Peers use the agreed station's area
        // and their own native role below; the host still authorizes both commands.
        if (!DamageGameplayActive(now) || _active?.Session != session || _stopSent || _swap != null || _roleOfferPending
            || now >= _localUntil || !TryLocations(_active.Belayer, _active.Climber, out var station, out var climber)
            || !WithinStationArea(_active.Station, station) || !WithinRopeRange(station, climber, _active.RopeCapacity)) return false;
        if (_active.Belayer == LocalPlayerId) return Game.Belay.StationValid && OwnStationMatches(_active.Station);
        var status = Game.Belay.Status;
        return _active.Climber == LocalPlayerId && status.Active && !status.Rebinding && status.Rope.HasValue;
    }

    private void ReceivePartnerDamage(ManualBelayDamageMessage message)
    {
        if (_active == null || message.Session != _active.Session || message.Climber != _active.Climber
            || message.Belayer != _active.Belayer || message.Fall == 0
            || (LocalPlayerId != message.Climber && LocalPlayerId != message.Belayer)) return;
        EnsurePeerDamageSession(message.Session);
        var now = Game.Time.UnscaledTime;
        if (message.Phase == ManualBelayDamagePhase.ClimberApproval && FractionValid(message.Fraction, .95f))
        {
            if (message.Fall <= _approvedDamageFall) return;
            _approvedDamageFall = message.Fall; // A lost context cannot make this approval replayable.
            if (!LocalDamageContext(message.Session)) return;
            if (LocalPlayerId == message.Belayer)
            { _belayerApprovedFall = message.Fall; _belayerApprovalUntil = now + DamageReceiptSeconds; return; }
            if (!_localDamageObservation.HasValue || _localDamageObservation.Value.Fall != message.Fall
                || _localDamageObservation.Value.Session != message.Session || now > _localDamageUntil) return;
            _queuedClimberDamage = message;
        }
        else if (message.Phase == ManualBelayDamagePhase.BelayerDose && FractionValid(message.Fraction, .3f))
        {
            if (LocalPlayerId != message.Belayer || message.Fall <= _belayerDamageFall) return;
            _belayerDamageFall = message.Fall;
            if (!LocalDamageContext(message.Session) || _belayerApprovedFall != message.Fall || now > _belayerApprovalUntil) return;
            _queuedBelayerDamage = message;
        }
    }

    private void TickPartnerDamage(float now)
    {
        if (_pendingSharedDamage != null && !HasPendingSharedDamage(_pendingSharedDamage.Session)) _pendingSharedDamage = null;
        if (_active == null) return;
        EnsurePeerDamageSession(_active.Session);
        if (!LocalDamageContext(_active.Session))
        { _queuedClimberDamage = _queuedBelayerDamage = null; _localDamageObservation = null; return; }
        if (_localDamageObservation.HasValue && now > _localDamageUntil)
        { _localDamageObservation = null; _queuedClimberDamage = null; }
        if (_queuedClimberDamage != null)
        {
            var approved = _queuedClimberDamage;
            _queuedClimberDamage = null; // Consume before the native callback, including exceptions/reentrancy.
            var result = ApplyPartnerDose(approved, false);
            if (!LocalDamageContext(approved.Session)) { _localDamageObservation = null; return; }
            var completed = result.Completed && FractionValid(result.ObservedFraction, approved.Fraction + DamageFractionTolerance);
            _damageReceipt.Send(new() { Session = approved.Session, Fall = approved.Fall,
                Completed = completed, ObservedFraction = completed ? Math.Min(result.ObservedFraction, approved.Fraction) : 0 },
                (accepted, reason) =>
                {
                    if (_localDamageObservation?.Session == approved.Session && _localDamageObservation?.Fall == approved.Fall)
                        _localDamageObservation = null;
                    if (!accepted) FeatureLog.Info("[BelayDamage] Receipt discarded; " + reason);
                });
        }
        if (_queuedBelayerDamage != null)
        {
            var dose = _queuedBelayerDamage; _queuedBelayerDamage = null;
            if (now <= _belayerApprovalUntil) ApplyPartnerDose(dose, true);
        }
        if (_active?.Climber != LocalPlayerId || _localDamageObservation.HasValue) return;
        var observation = Game.Belay.PollPartnerFall(_active.Session);
        if (!observation.HasValue || observation.Value.Session != _active.Session
            || observation.Value.Fall == 0 || observation.Value.Fall <= _localDamageFall) return;
        var fall = observation.Value;
        _localDamageFall = fall.Fall;
        _localDamageObservation = fall;
        _localDamageUntil = now + DamageReceiptSeconds;
        _damageClaim.Send(new() { Session = fall.Session, Fall = fall.Fall, Height = fall.Height,
            Length = fall.Length, NativeLossFraction = fall.NativeLossFraction }, (accepted, reason) =>
            {
                if (accepted) return;
                if (_localDamageObservation?.Session == fall.Session && _localDamageObservation?.Fall == fall.Fall)
                    _localDamageObservation = null;
                FeatureLog.Info("[BelayDamage] Fall claim discarded; " + reason);
            });
    }

    private PartnerDamageResult ApplyPartnerDose(ManualBelayDamageMessage dose, bool belayer)
    {
        PartnerDamageResult result;
        try
        {
            // A zero dose still consumes the native observation/nonce, without InflictDamage.
            result = Game.Belay.ApplyPartnerDamage(new(dose.Session, dose.Fall, belayer, dose.Fraction));
        }
        catch (Exception exception)
        { result = new(true, false, dose.Fraction, 0, float.NaN, float.NaN, exception.GetType().Name); }
        if (result.Completed && (!FractionValid(result.ObservedFraction, dose.Fraction + DamageFractionTolerance)
            || !float.IsFinite(result.RequestedFraction) || Math.Abs(result.RequestedFraction - dose.Fraction) > DamageFractionTolerance
            || !float.IsFinite(result.HpBefore) || !float.IsFinite(result.HpAfter)))
            result = result with { Completed = false, Detail = "Native health readback was invalid; no retry." };
        var who = belayer ? "Belayer" : "Climber";
        var text = result.Completed
            ? dose.Fraction == 0 ? who + " impact: no additional damage required."
                : $"{who} impact: observed {result.ObservedFraction:P1} of maximum HP ({result.HpBefore:F1} -> {result.HpAfter:F1})."
            : who + " damage application incomplete; no retry. " + (belayer ? "" : "No belayer share. ") + result.Detail;
        Game.Hud.ShowMessage("belay-damage", text, 6);
        FeatureLog.Info(FormattableString.Invariant($"[BelayDamage] Local result; session={dose.Session}, fall={dose.Fall}, belayer={belayer}, requested={dose.Fraction:F4}, attempted={result.Attempted}, completed={result.Completed}, observed={result.ObservedFraction:F4}, hpBefore={result.HpBefore:F3}, hpAfter={result.HpAfter:F3}; {result.Detail}"));
        return result;
    }

    private void ClearPeerPartnerDamage()
    {
        _peerDamageSession = Guid.Empty;
        _localDamageFall = _approvedDamageFall = _belayerDamageFall = _belayerApprovedFall = 0;
        _localDamageObservation = null;
        _queuedClimberDamage = _queuedBelayerDamage = null;
        _localDamageUntil = _belayerApprovalUntil = 0;
        Game.Hud.HideMessage("belay-damage");
    }
}

internal sealed class ManualBelayDamageClaim : IPacket
{
    public Guid Session;
    public uint Fall;
    public float Height, Length, NativeLossFraction;
    public void Serialize(BinaryWriter writer)
    { writer.Write(Session.ToByteArray()); writer.Write(Fall); writer.Write(Height); writer.Write(Length); writer.Write(NativeLossFraction); }
    public void Deserialize(BinaryReader reader)
    {
        if (reader.BaseStream.Length - reader.BaseStream.Position != 32) throw new InvalidDataException("Invalid belay damage claim size.");
        Session = new Guid(reader.ReadBytes(16)); Fall = reader.ReadUInt32(); Height = reader.ReadSingle(); Length = reader.ReadSingle(); NativeLossFraction = reader.ReadSingle();
    }
}

internal sealed class ManualBelayDamageReceipt : IPacket
{
    public Guid Session;
    public uint Fall;
    public float ObservedFraction;
    public bool Completed;
    public void Serialize(BinaryWriter writer)
    { writer.Write(Session.ToByteArray()); writer.Write(Fall); writer.Write(ObservedFraction); writer.Write(Completed); }
    public void Deserialize(BinaryReader reader)
    {
        if (reader.BaseStream.Length - reader.BaseStream.Position != 25) throw new InvalidDataException("Invalid belay damage receipt size.");
        Session = new Guid(reader.ReadBytes(16)); Fall = reader.ReadUInt32(); ObservedFraction = reader.ReadSingle();
        var completed = reader.ReadByte(); if (completed > 1) throw new InvalidDataException("Invalid damage completion flag."); Completed = completed == 1;
    }
}

internal enum ManualBelayDamagePhase : byte { ClimberApproval, BelayerDose }

internal sealed class ManualBelayDamageMessage : IPacket
{
    public ManualBelayDamagePhase Phase;
    public Guid Session;
    public uint Fall;
    public int Climber, Belayer;
    public float Fraction;
    public void Serialize(BinaryWriter writer)
    { writer.Write((byte)Phase); writer.Write(Session.ToByteArray()); writer.Write(Fall); writer.Write(Climber); writer.Write(Belayer); writer.Write(Fraction); }
    public void Deserialize(BinaryReader reader)
    {
        if (reader.BaseStream.Length - reader.BaseStream.Position != 33) throw new InvalidDataException("Invalid belay damage event size.");
        Phase = (ManualBelayDamagePhase)reader.ReadByte(); Session = new Guid(reader.ReadBytes(16)); Fall = reader.ReadUInt32();
        Climber = reader.ReadInt32(); Belayer = reader.ReadInt32(); Fraction = reader.ReadSingle();
    }
}
