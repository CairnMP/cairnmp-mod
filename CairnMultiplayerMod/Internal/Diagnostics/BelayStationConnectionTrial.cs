using System;
using System.Collections.Generic;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>Observes a native A-unclip/reclip while the distinct B anchor remains held. Never issues rope commands.</summary>
internal sealed class BelayStationConnectionTrial
{
    internal const double TransitionSeconds = 3;
    internal const double ConfirmationSeconds = 5;
    private readonly BelayTopologySnapshot _before;
    private readonly long _station, _backup;
    private BelayTopologySnapshot _candidate;
    private double _started, _deadline, _last, _candidateSince, _confirmedSince;
    internal StationConnectionStage Stage { get; private set; } = StationConnectionStage.Unclipping;
    internal StationConnectionFailure Failure { get; private set; }
    internal float ConfirmedSeconds { get; private set; }
    internal bool Active => Stage is not (StationConnectionStage.Passed or StationConnectionStage.Failed);

    internal BelayStationConnectionTrial(BelayTopologySnapshot before, long station, long backup, double now)
    {
        if (before?.IsValid != true || before.ProtectionCount != 2 || before.Parts.Length < 3
            || before.Holders.Length < 3 || station == 0 || backup == 0 || station == backup
            || before.LastPiton != backup || !double.IsFinite(now)) throw new ArgumentException("Expected a stable A -> B route.");
        _before = new BelayTopologySnapshot(before.Rope, before.LastPiton, before.Parts, before.Holders, before.Available, before.ProtectionCount);
        _station = station; _backup = backup; _started = _last = now; _deadline = now + TransitionSeconds;
    }

    internal bool RequestConnection(double now)
    {
        if (Stage != StationConnectionStage.Detached || !ValidTime(now) || now > _deadline) return false;
        Stage = StationConnectionStage.Connecting; _deadline = now + TransitionSeconds; _candidate = null; _last = now;
        return true;
    }

    internal bool ResumeDetached(BelayTopologySnapshot snapshot, IReadOnlyList<long> order, bool stationAttached, double now)
    {
        if (Stage != StationConnectionStage.Failed || !ValidTime(now) || stationAttached || !DetachedRoute(snapshot, order)) return false;
        Stage = StationConnectionStage.Detached; Failure = StationConnectionFailure.None; ConfirmedSeconds = 0;
        _started = _last = now; _deadline = now + 30; _candidate = null;
        return true;
    }

    internal void Observe(BelayTopologySnapshot snapshot, IReadOnlyList<long> order, bool stationAttached,
        bool backupValid, bool stationValid, bool contextValid, bool routeMeasured, double now)
    {
        if (!Active) return;
        if (!contextValid || !ValidTime(now)) { Fail(StationConnectionFailure.Context); return; }
        var gap = now - _last; _last = now;
        if (!backupValid) { Fail(StationConnectionFailure.BackupLost); return; }
        if (!stationValid) { Fail(StationConnectionFailure.StationLost); return; }
        if (now - _started > 60 || (Stage != StationConnectionStage.Confirming && now > _deadline))
        { Fail(StationConnectionFailure.Timeout); return; }
        if (snapshot == null || !snapshot.IsValid) { _candidate = null; ConfirmedSeconds = 0; return; }
        if (snapshot.Rope != _before.Rope || order == null || order.Count != snapshot.ProtectionCount
            || !KnownOrder(order)) { Fail(StationConnectionFailure.RouteChanged); return; }
        if (Stage == StationConnectionStage.Unclipping)
        {
            if (stationAttached || !routeMeasured || !DetachedRoute(snapshot, order)) { _candidate = null; return; }
            if (!Settled(snapshot, now)) return;
            Stage = StationConnectionStage.Detached; _deadline = now + 30; _candidate = null;
            return;
        }
        if (Stage == StationConnectionStage.Detached)
        {
            if (stationAttached || !routeMeasured || !DetachedRoute(snapshot, order)) Fail(StationConnectionFailure.RouteChanged);
            return;
        }
        var connected = stationAttached && order.Count == 2 && snapshot.LastPiton == _backup
            && snapshot.Parts.Length == _before.Parts.Length && snapshot.Holders.Length == _before.Holders.Length && routeMeasured;
        if (Stage == StationConnectionStage.Connecting)
        {
            if (!connected) { _candidate = null; return; }
            if (!Settled(snapshot, now)) return;
            Stage = StationConnectionStage.Confirming; _confirmedSince = now; ConfirmedSeconds = 0;
            return;
        }
        if (!connected) { Fail(StationConnectionFailure.RouteChanged); return; }
        if (gap > 1 || _candidate == null || !_candidate.Matches(snapshot))
        { _candidate = snapshot; _confirmedSince = now; }
        ConfirmedSeconds = (float)Math.Min(ConfirmationSeconds, now - _confirmedSince);
        if (ConfirmedSeconds >= ConfirmationSeconds) Stage = StationConnectionStage.Passed;
    }

    internal void Fail(StationConnectionFailure failure)
    {
        if (!Active) return;
        Stage = StationConnectionStage.Failed; Failure = failure; ConfirmedSeconds = 0; _candidate = null;
    }

    private bool ValidTime(double now) => double.IsFinite(now) && now >= _last;

    internal static bool PinsAgree(long stationBegin, long stationEnd, long backupBegin, long backupEnd,
        IReadOnlyList<long> colliders, bool stationAttached)
    {
        if (stationBegin == 0 || stationEnd == 0 || backupBegin == 0 || backupEnd == 0 || colliders == null || colliders.Count > 128
            || stationBegin == backupBegin || stationBegin == backupEnd || stationEnd == backupBegin || stationEnd == backupEnd) return false;
        var sb = 0; var se = 0; var bb = 0; var be = 0;
        foreach (var collider in colliders)
        {
            if (collider == stationBegin) sb++;
            if (collider == stationEnd) se++;
            if (collider == backupBegin) bb++;
            if (collider == backupEnd) be++;
        }
        var backup = backupBegin == backupEnd ? bb >= 2 : bb > 0 && be > 0;
        var station = stationBegin == stationEnd ? sb >= 2 : sb > 0 && se > 0;
        return backup && (stationAttached ? station : sb == 0 && se == 0);
    }
    private bool DetachedRoute(BelayTopologySnapshot snapshot, IReadOnlyList<long> order)
        => snapshot?.IsValid == true && snapshot.Rope == _before.Rope && snapshot.LastPiton == _backup
            && order?.Count == 1 && order[0] == _backup && snapshot.ProtectionCount == 1
            && snapshot.Parts.Length == _before.Parts.Length - 1 && snapshot.Holders.Length == _before.Holders.Length - 1;
    private bool KnownOrder(IReadOnlyList<long> order)
        => (order.Count == 1 && order[0] == _backup) || (order.Count == 2 && order[0] == _station && order[1] == _backup);
    private bool Settled(BelayTopologySnapshot snapshot, double now)
    {
        if (_candidate == null || !_candidate.Matches(snapshot)) { _candidate = snapshot; _candidateSince = now; return false; }
        return now - _candidateSince >= BelayTopologyTracker.SettleSeconds;
    }
}
