using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.Internal.Diagnostics;

internal enum BelayStationAttachmentFailure
{
    None, Setup, Context, BackupLost, StationLost, RouteChanged, Timeout, NativeRejected,
    StationRequired, BackupRequired, RouteNotReady, NativePartsNotReady, NativePinsMismatch
}

internal enum BelayStationAttachmentStage { Connecting, Confirming, Ready, Failed }

/// <summary>Confirms a single station insertion while retaining the original protected route.</summary>
internal sealed class BelayStationAttachment
{
    internal const double TransitionSeconds = 3;
    internal const double ConfirmationSeconds = 5;
    internal const double MaximumSeconds = 10;
    private readonly BelayTopologySnapshot _before;
    private readonly long[] _order;
    private readonly long _station, _stationHolder;
    private readonly long _robotHolder;
    private readonly int _robotOccurrences;
    private readonly double _started;
    private double _last, _candidateSince, _confirmedSince;
    private BelayTopologySnapshot _candidate;
    internal BelayStationAttachmentStage Stage { get; private set; }
    internal BelayStationAttachmentFailure Failure { get; private set; }

    internal BelayStationAttachment(BelayTopologySnapshot before, IReadOnlyList<long> order,
        long station, long stationHolder, double now, long robotHolder = 0)
    {
        if (before?.IsValid != true || before.Parts.Length >= 64 || before.Holders.Length >= 66
            || order == null || order.Count != before.ProtectionCount || order.Count >= 63
            || station == 0 || stationHolder == 0 || !double.IsFinite(now)) throw new ArgumentException("A protected route with room for one station is required.");
        var seen = new HashSet<long> { station };
        _order = new long[order.Count];
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == 0 || !seen.Add(order[i])) throw new ArgumentException("The station must be absent from an unambiguous route.");
            _order[i] = order[i];
        }
        if (_order.Length == 0 || _order[_order.Length - 1] != before.LastPiton
            || Array.IndexOf(before.Holders, stationHolder) >= 0) throw new ArgumentException("The backup must remain the last protection.");
        _robotHolder = robotHolder;
        if (robotHolder != 0)
        {
            foreach (var holder in before.Holders) if (holder == robotHolder) _robotOccurrences++;
            if (_robotOccurrences == 0) throw new ArgumentException("The native robot holder must belong to the original route.");
        }
        _before = new BelayTopologySnapshot(before.Rope, before.LastPiton, before.Parts, before.Holders, before.Available, before.ProtectionCount);
        _station = station; _stationHolder = stationHolder; _started = _last = now;
    }

    internal void Observe(BelayTopologySnapshot snapshot, IReadOnlyList<long> order, bool pinsReady, double now)
    {
        if (Stage == BelayStationAttachmentStage.Failed) return;
        if (!double.IsFinite(now) || now < _last) { Fail(BelayStationAttachmentFailure.Context); return; }
        var gap = now - _last; _last = now;
        if (Stage == BelayStationAttachmentStage.Ready)
        {
            if (!pinsReady || snapshot?.IsValid != true || !_candidate.Matches(snapshot)
                || !KnownOrder(order, out _)) Fail(BelayStationAttachmentFailure.RouteChanged);
            return;
        }
        if (now - _started > MaximumSeconds || (Stage == BelayStationAttachmentStage.Connecting && now - _started > TransitionSeconds))
        { Fail(BelayStationAttachmentFailure.Timeout); return; }
        if (snapshot?.IsValid != true) { ResetConfirmation(); return; }
        if (snapshot.Rope != _before.Rope || !KnownOrder(order, out var terminal)
            || snapshot.LastPiton != (terminal ? _station : _before.LastPiton))
        { Fail(BelayStationAttachmentFailure.RouteChanged); return; }
        var connected = order.Count == _order.Length + 1 && snapshot.ProtectionCount == _before.ProtectionCount + 1
            && snapshot.Parts.Length == _before.Parts.Length + 1 && OriginalHoldersRetained(snapshot.Holders)
            && (!terminal || TerminalRouteMatches(snapshot)) && pinsReady;
        if (!connected) { ResetConfirmation(); return; }
        if (_candidate == null || !_candidate.Matches(snapshot) || gap > 1)
        { _candidate = snapshot; _candidateSince = _confirmedSince = now; return; }
        if (now - _candidateSince < BelayTopologyTracker.SettleSeconds) return;
        if (Stage == BelayStationAttachmentStage.Connecting)
        { Stage = BelayStationAttachmentStage.Confirming; _confirmedSince = now; return; }
        if (now - _confirmedSince >= ConfirmationSeconds) Stage = BelayStationAttachmentStage.Ready;
    }

    internal void Fail(BelayStationAttachmentFailure failure)
    {
        if (Stage == BelayStationAttachmentStage.Failed) return;
        Failure = failure; Stage = BelayStationAttachmentStage.Failed; _candidate = null;
    }

    private void ResetConfirmation() { _candidate = null; _confirmedSince = _last; }

    private bool KnownOrder(IReadOnlyList<long> order, out bool terminal)
    {
        terminal = false;
        if (order == null) return false;
        var offset = 0;
        if (order.Count != _order.Length)
        {
            if (order.Count != _order.Length + 1) return false;
            if (order[0] == _station) offset = 1;
            else if (order[order.Count - 1] == _station) terminal = true;
            else return false;
        }
        for (var i = 0; i < _order.Length; i++) if (_order[i] != order[i + offset]) return false;
        return true;
    }

    private bool TerminalRouteMatches(BelayTopologySnapshot snapshot)
    {
        // Native geometry can place an accepted initial or replacement station
        // at the final protection. The personal backup stays attached separately;
        // require the retained protection walk plus that station before the harness.
        // Repeated native root holders do not add physical rope parts. Their
        // identity and multiplicity are checked by OriginalHoldersRetained.
        if (_before.Parts.Length != _order.Length + 1) return false;
        var previous = snapshot.Holders.Length - 2;
        while (previous >= 0 && _robotHolder != 0 && snapshot.Holders[previous] == _robotHolder) previous--;
        return previous >= 0 && snapshot.Holders[previous] == _stationHolder;
    }

    private bool OriginalHoldersRetained(long[] holders)
    {
        if (holders.Length != _before.Holders.Length + 1) return false;
        var old = 0; var stationOccurrences = 0; var robotOccurrences = 0;
        foreach (var holder in holders)
        {
            if (holder == _stationHolder) { stationOccurrences++; continue; }
            // Only the explicitly identified native robot may move within the
            // walk. Every occurrence and all other ordered holders must remain.
            if (holder == _robotHolder) { robotOccurrences++; continue; }
            while (old < _before.Holders.Length && _before.Holders[old] == _robotHolder) old++;
            if (old >= _before.Holders.Length || holder != _before.Holders[old++]) return false;
        }
        while (old < _before.Holders.Length && _before.Holders[old] == _robotHolder) old++;
        return stationOccurrences == 1 && robotOccurrences == _robotOccurrences && old == _before.Holders.Length;
    }
    internal static bool PinsAgree(long stationCollider, long backupCollider,
        IReadOnlyList<long> colliders, bool stationAttached)
    {
        if (stationCollider == 0 || backupCollider == 0 || stationCollider == backupCollider
            || colliders == null || colliders.Count > 128) return false;
        var stationPins = 0; var backupPins = 0;
        foreach (var collider in colliders)
        {
            if (collider == stationCollider) stationPins++;
            if (collider == backupCollider) backupPins++;
        }
        // Adjacent native rope parts both pin to a quickdraw's GetAttachEnd().
        // One occurrence is an incomplete split, not a confirmed connection.
        return backupPins >= 2 && (stationAttached ? stationPins >= 2 : stationPins == 0);
    }
}
