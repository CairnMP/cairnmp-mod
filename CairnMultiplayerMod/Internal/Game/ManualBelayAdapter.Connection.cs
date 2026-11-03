using System;
using System.Collections.Generic;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private BelayStationConnectionTrial _connectionTrial;
    private LocalStationConnectionStatus? _connectionNotice;
    private Lifeline _connectionLifeline;
    private Piton _connectionBackup;
    private LogicalQuickdraw _connectionQuickdraw;
    private BelayStationAnchor _connectionBackupAnchor;
    private HashSet<long> _connectionPlaced;
    private Vector3 _connectionBody;
    private double _nextConnectionPoll;
    private double _nextConnectionSample;
    private string _connectionDetail;
    private bool ConnectionActive => _connectionTrial?.Active == true;
    private bool _connectionPinsAgree;
    private LocalStationConnectionContext _connectionContext;
    private double _nextConnectionContext;
    private string _connectionLifecycleDetail;
    public LocalStationConnectionContext StationConnectionContext => _connectionContext;
    public LocalStationConnectionStatus? StationConnectionStatus => _connectionTrial == null ? _connectionNotice
        : new(_connectionTrial.Stage, _connectionTrial.Failure, _connectionTrial.ConfirmedSeconds, _connectionDetail ?? "");

    public bool AdvanceLocalStationConnection(out string reason)
    {
        if (ConnectionActive)
        {
            TickStationConnection(true);
            reason = "Wait for the current native transition. Remain personally anchored at B.";
            if (!ConnectionActive || _connectionTrial.Stage != StationConnectionStage.Detached) return false;
            if (!_connectionTrial.RequestConnection(Now)) return false;
            try
            {
                // A is already registered in the lifeline. Let native geometric
                // insertion choose the segment; do not force it onto the end.
                _connectionLifeline.pawn.AddQuickdrawToSecuringRope(_connectionQuickdraw, false, false);
                _connectionDetail = reason = "Native connection requested once. Keep B attached and wait for measured confirmation.";
                LogStationConnection();
                return true;
            }
            catch (Exception ex) { FailStationConnection(StationConnectionFailure.NativeRejected, ex.Message); reason = ex.Message; return false; }
        }
        try
        {
            if (_connectionTrial?.Stage == StationConnectionStage.Failed && _connectionLifeline != null
                && TryReadConnection(out var detached, out var order, out var attached, out var nativeReady, out _, out _) && nativeReady
                && _connectionTrial.ResumeDetached(detached, order, attached, Now))
                return AdvanceLocalStationConnection(out reason);
        }
        catch (Exception ex) { ClearStationConnection(); reason = ex.Message; return RejectConnection(reason); }
        ClearStationConnection();
        reason = "Station connection context unavailable.";
        try
        {
            if (!CanRunStationConnection(out reason)) return RejectConnection(reason);
            if (!TryGetActiveContext(out reason)) return RejectConnection(reason);
            reason = "End rope control first. Record A with Ctrl+F10, then anchor yourself at a distinct B.";
            if (_control != null || _stationLifeline != null || Roping.RopeInterop.IsNativeBelayEngaged) return RejectConnection(reason);
            var lifeline = LocalLifeline();
            if (_localStation == null || !LocalStationMatches(lifeline, out reason)) return RejectConnection(reason);
            var backup = lifeline.OffBelayPiton;
            reason = "Remain personally anchored at healthy B, different from recorded A, with exactly A -> B on the rope.";
            if (backup == null || Id(backup) == Id(_localStationPiton) || backup.pitonHp <= 0
                || lifeline.pawn == null || lifeline.pawn.IsFalling || lifeline.SwingHasPivotAndExtremity()
                || lifeline.harness == null || Vector3.Distance(lifeline.harness.GetAttachPosition(), backup.transform.position) > 1.5f
                || Vector3.Distance(_localStationPiton.transform.position, backup.transform.position) > 6) return RejectConnection(reason);
            if (!TrySnapshot(lifeline, out var before, out reason)) return RejectConnection(reason);
            var initialOrder = ConnectionOrder(lifeline);
            reason = "The trial requires exactly recorded A then personally anchored B, on a stable native rope.";
            if (initialOrder.Length != 2 || initialOrder[0] != Id(_localStationPiton) || initialOrder[1] != Id(backup)
                || before.LastPiton != Id(backup) || before.Parts.Length < 3 || before.Holders.Length < 3) return RejectConnection(reason);
            _connectionLifeline = lifeline; _connectionBackup = backup; _connectionQuickdraw = _localStationPiton.Quickdraw;
            _connectionBackupAnchor = new BelayStationAnchor(Id(lifeline), Id(lifeline.securingRope), Id(backup), ToNumerics(backup.transform.position));
            _connectionBody = lifeline.harness.GetAttachPosition();
            _connectionPlaced = ConnectionPlaced(lifeline);
            if (!TryReadConnection(out _, out _, out var stationAttached, out var ready, out _, out reason) || !stationAttached || !ready)
                return RejectConnection(reason);
            _connectionTrial = new BelayStationConnectionTrial(before, Id(_localStationPiton), Id(backup), Now);
            _nextConnectionPoll = Now;
            _nextConnectionSample = Now;
            ModLog.Info("[StationConnection] Begin; rope=" + before.Rope + ", A=" + Id(_localStationPiton)
                + ", B=" + Id(backup) + ", parts=" + before.Parts.Length + ", holders=" + before.Holders.Length);
            if (!lifeline.securingRope.RequestRemoveQuickdraw(_connectionQuickdraw))
            { reason = "Native unclip request was refused."; FailStationConnection(StationConnectionFailure.NativeRejected, reason); return false; }
            _connectionDetail = reason = "Only A's rope unclip was requested. Personal anchor B and the native piton collection will be checked throughout.";
            LogStationConnection();
            return true;
        }
        catch (Exception ex) { FailStationConnection(StationConnectionFailure.NativeRejected, ex.Message); reason = ex.Message; return false; }
    }

    private bool RejectConnection(string reason)
    {
        _connectionNotice = new(StationConnectionStage.Failed, StationConnectionFailure.Setup, 0,
            string.IsNullOrEmpty(reason) ? "Record station A before preparing B." : reason);
        return false;
    }

    private bool TryReadConnection(out BelayTopologySnapshot snapshot, out long[] order, out bool attached,
        out bool measured, out StationConnectionFailure failure, out string reason)
    {
        snapshot = null; order = Array.Empty<long>(); attached = measured = false;
        _connectionPinsAgree = false;
        failure = StationConnectionFailure.Context; reason = "The offline trial context changed.";
        if (!CanRunStationConnection(out reason) || _control != null || _stationLifeline != null || Roping.RopeInterop.IsNativeBelayEngaged) return false;
        if (!TryGetActiveContext(out reason)) return false;
        var lifeline = _connectionLifeline;
        failure = StationConnectionFailure.BackupLost; reason = "B is no longer the same healthy personal anchor. No further rope requests were issued.";
        if (lifeline == null || Id(LocalLifeline()) != Id(lifeline) || _connectionBackup == null
            || lifeline.harness == null || lifeline.pawn == null || lifeline.pawn.IsFalling || lifeline.SwingHasPivotAndExtremity()
            || Id(lifeline.OffBelayPiton) != Id(_connectionBackup)
            || !_connectionBackupAnchor.Matches(Id(lifeline), Id(lifeline.securingRope), Id(_connectionBackup),
                _connectionBackup.pitonHp, ToNumerics(_connectionBackup.transform.position))
            || Vector3.Distance(lifeline.harness.GetAttachPosition(), _connectionBody) > .25f
            || _connectionBackup.Quickdraw == null || !lifeline.securingRope.IsAttached(_connectionBackup.Quickdraw.Cast<IRopeHolder>())) return false;
        failure = StationConnectionFailure.StationLost; reason = "Recorded A or its original quickdraw was lost, moved, broken or replaced.";
        if (!LocalStationMatches(lifeline, out reason) || _connectionQuickdraw == null || _localStationPiton == null
            || Id(_localStationPiton.Quickdraw) != Id(_connectionQuickdraw)) return false;
        failure = StationConnectionFailure.RouteChanged; reason = "The native piton collection changed. No further requests were issued.";
        var placed = ConnectionPlaced(lifeline);
        if (_connectionPlaced == null || placed == null || !_connectionPlaced.SetEquals(placed)) return false;
        attached = lifeline.securingRope.IsAttached(_connectionQuickdraw.Cast<IRopeHolder>());
        if (!TrySnapshot(lifeline, out snapshot, out reason)) return true; // B is held; allow native splitting to settle.
        order = ConnectionOrder(lifeline);
        _connectionPinsAgree = ConnectionPinsAgree(lifeline, attached);
        measured = _connectionPinsAgree && (!attached || TryMeasureLocalStation(lifeline, snapshot.Available, out _, out reason));
        if (!_connectionPinsAgree) reason = "Wait for loaded native rope parts and matching quickdraw pin colliders.";
        return true;
    }

    private bool ConnectionPinsAgree(Lifeline lifeline, bool stationAttached)
    {
        var parts = lifeline.securingRope.ropeParts;
        var colliders = new List<long>();
        if (parts == null || parts.Count > 64) return false;
        foreach (var part in parts)
        {
            if (part?.obiRope == null || !part.obiRope.isLoaded || part.pinConstraintHandler == null) return false;
            colliders.Add(Id(part.pinConstraintHandler.GetAttachCollider(RopeSide.Begin)));
            colliders.Add(Id(part.pinConstraintHandler.GetAttachCollider(RopeSide.End)));
        }
        var backup = _connectionBackup.Quickdraw;
        return BelayStationConnectionTrial.PinsAgree(Id(_connectionQuickdraw.attachBegin), Id(_connectionQuickdraw.attachEnd),
            Id(backup.attachBegin), Id(backup.attachEnd), colliders, stationAttached);
    }

    private static long[] ConnectionOrder(Lifeline lifeline)
    {
        var result = new List<long>();
        var holders = lifeline.securingRope.ropeHolders;
        if (holders == null || holders.Count > 66) return Array.Empty<long>();
        for (var i = 0; i < holders.Count; i++)
        {
            var piton = holders[i]?.TryCast<LogicalQuickdraw>()?.Piton;
            if (piton != null) result.Add(Id(piton));
        }
        return result.ToArray();
    }

    private static HashSet<long> ConnectionPlaced(Lifeline lifeline)
    {
        var placed = lifeline.PlacedPitons;
        if (placed == null || placed.Count > 128) return null;
        var result = new HashSet<long>();
        for (var i = 0; i < placed.Count; i++)
            if (placed[i]?.Piton == null || !result.Add(Id(placed[i].Piton))) return null;
        return result;
    }

    private void TickStationConnection(bool force = false)
    {
        if (!ConnectionActive || (!force && Now < _nextConnectionPoll)) return;
        _nextConnectionPoll = Now + .1;
        try
        {
            var previous = _connectionTrial.Stage;
            if (!TryReadConnection(out var snapshot, out var order, out var attached, out var measured, out var failure, out var reason))
            { FailStationConnection(failure, reason); return; }
            _connectionTrial.Observe(snapshot, order, attached, true, true, true, measured, Now);
            if (Now >= _nextConnectionSample)
            {
                _nextConnectionSample = Now + .5;
                ModLog.Info("[StationConnection] Sample; stage=" + _connectionTrial.Stage + ", order=" + string.Join(",", order)
                    + ", parts=" + snapshot?.Parts.Length + ", holders=" + snapshot?.Holders.Length
                    + ", attachedA=" + attached + ", pinsMatch=" + _connectionPinsAgree + ", measured=" + measured
                    + ", backupValid=True, placed=" + _connectionPlaced.Count);
            }
            if (_connectionTrial.Stage != previous) { _connectionDetail = reason; LogStationConnection(); }
        }
        catch (Exception ex) { FailStationConnection(StationConnectionFailure.NativeRejected, ex.Message); }
    }

    private void FailStationConnection(StationConnectionFailure failure, string reason)
    {
        _connectionDetail = reason;
        if (_connectionTrial == null) _connectionNotice = new(StationConnectionStage.Failed, failure, 0, reason);
        else _connectionTrial.Fail(failure);
        LogStationConnection();
    }

    private void LogStationConnection() => ModLog.Info("[StationConnection] stage=" + StationConnectionStatus?.Stage
        + ", failure=" + StationConnectionStatus?.Failure + "; " + StationConnectionStatus?.Detail);

    private void ClearStationConnection()
    {
        _connectionTrial = null; _connectionNotice = null;
        ReleaseConnectionReferences();
    }

    private void RefreshStationConnectionContext(bool force = false)
    {
        var enabled = ModConfig.BelayStationConnectionExperiment?.Value == true;
        var offline = _localConnectionAllowed();
        if (!force && enabled == _connectionContext.Enabled && offline == _connectionContext.Offline
            && Now < _nextConnectionContext) return;
        _nextConnectionContext = Now + .25;
        var state = CairnGameLifecycleState.Unknown;
        var detail = "not queried";
        var nativeInGame = enabled && offline && GameLifecycleService.TryGetGameLifecycle(out state, out detail)
            && state == CairnGameLifecycleState.InGame;
        var current = new LocalStationConnectionContext(enabled, offline, nativeInGame);
        if (enabled && (current != _connectionContext || detail != _connectionLifecycleDetail))
            ModLog.Info("[StationConnection] Context; enabled=" + enabled + ", offline=" + offline
                + ", nativeInGame=" + nativeInGame + ", lifecycle=" + state + "; " + detail);
        _connectionLifecycleDetail = detail;
        _connectionContext = current;
    }

    private bool CanRunStationConnection(out string reason)
    {
        RefreshStationConnectionContext(true);
        reason = "Enable the offline station connection trial first.";
        if (!_connectionContext.Enabled) return false;
        reason = "Leave Multiplayer and enter Story directly before this solo trial.";
        if (!_connectionContext.Offline) return false;
        reason = "Wait for the native Story gameplay to finish loading. The solo trial does not use multiplayer player state.";
        if (!_connectionContext.CanRun) return false;
        reason = "";
        return true;
    }

    private void ReleaseConnectionReferences()
    {
        _connectionLifeline = null; _connectionBackup = null; _connectionQuickdraw = null;
        _connectionBackupAnchor = null; _connectionPlaced = null;
    }
}
