using System;
using System.Collections.Generic;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Internal.Diagnostics;

internal enum BelayStationAttachmentStage { Connecting, Confirming, Ready, Failed }

/// <summary>Confirms insertion ahead of the original protected route; never changes native rope state.</summary>
internal sealed class BelayStationAttachment
{
    internal const double TransitionSeconds = 3;
    internal const double ConfirmationSeconds = 5;
    internal const double MaximumSeconds = 10;
    private readonly BelayTopologySnapshot _before;
    private readonly long[] _order;
    private readonly long _station, _stationHolder;
    private readonly double _started;
    private double _last, _candidateSince, _confirmedSince;
    private BelayTopologySnapshot _candidate;
    internal BelayStationAttachmentStage Stage { get; private set; }
    internal StationConnectionFailure Failure { get; private set; }

    internal BelayStationAttachment(BelayTopologySnapshot before, IReadOnlyList<long> order,
        long station, long stationHolder, double now)
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
        _before = new BelayTopologySnapshot(before.Rope, before.LastPiton, before.Parts, before.Holders, before.Available, before.ProtectionCount);
        _station = station; _stationHolder = stationHolder; _started = _last = now;
    }

    internal void Observe(BelayTopologySnapshot snapshot, IReadOnlyList<long> order, bool pinsReady, double now)
    {
        if (Stage is BelayStationAttachmentStage.Ready or BelayStationAttachmentStage.Failed) return;
        if (!double.IsFinite(now) || now < _last) { Fail(StationConnectionFailure.Context); return; }
        var gap = now - _last; _last = now;
        if (now - _started > MaximumSeconds || (Stage == BelayStationAttachmentStage.Connecting && now - _started > TransitionSeconds))
        { Fail(StationConnectionFailure.Timeout); return; }
        if (snapshot?.IsValid != true) { ResetConfirmation(); return; }
        if (snapshot.Rope != _before.Rope || snapshot.LastPiton != _before.LastPiton || !KnownOrder(order))
        { Fail(StationConnectionFailure.RouteChanged); return; }
        var connected = order.Count == _order.Length + 1 && snapshot.ProtectionCount == _before.ProtectionCount + 1
            && snapshot.Parts.Length == _before.Parts.Length + 1 && OriginalHoldersRetained(snapshot.Holders) && pinsReady;
        if (!connected) { ResetConfirmation(); return; }
        if (_candidate == null || !_candidate.Matches(snapshot) || gap > 1)
        { _candidate = snapshot; _candidateSince = _confirmedSince = now; return; }
        if (now - _candidateSince < BelayTopologyTracker.SettleSeconds) return;
        if (Stage == BelayStationAttachmentStage.Connecting)
        { Stage = BelayStationAttachmentStage.Confirming; _confirmedSince = now; return; }
        if (now - _confirmedSince >= ConfirmationSeconds) Stage = BelayStationAttachmentStage.Ready;
    }

    internal void Fail(StationConnectionFailure failure)
    {
        if (Stage is BelayStationAttachmentStage.Ready or BelayStationAttachmentStage.Failed) return;
        Failure = failure; Stage = BelayStationAttachmentStage.Failed; _candidate = null;
    }

    private void ResetConfirmation() { _candidate = null; _confirmedSince = _last; }

    private bool KnownOrder(IReadOnlyList<long> order)
    {
        if (order == null) return false;
        var offset = order.Count == _order.Length ? 0 : order.Count == _order.Length + 1 && order[0] == _station ? 1 : -1;
        if (offset < 0) return false;
        for (var i = 0; i < _order.Length; i++) if (_order[i] != order[i + offset]) return false;
        return true;
    }

    private bool OriginalHoldersRetained(long[] holders)
    {
        if (holders.Length != _before.Holders.Length + 1) return false;
        var old = 0; var stationOccurrences = 0;
        foreach (var holder in holders)
        {
            if (holder == _stationHolder) { stationOccurrences++; continue; }
            if (old >= _before.Holders.Length || holder != _before.Holders[old++]) return false;
        }
        return stationOccurrences == 1 && old == _before.Holders.Length;
    }
}
