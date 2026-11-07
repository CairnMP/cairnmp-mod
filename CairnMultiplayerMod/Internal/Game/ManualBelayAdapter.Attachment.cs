using System;
using System.Collections.Generic;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Roping;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private sealed class StationAttachment
    {
        internal BelaySharedStation Station;
        internal Lifeline Lifeline;
        internal Piton Piton, Backup;
        internal LogicalQuickdraw Quickdraw;
        internal BelayStationAnchor Anchor, BackupAnchor;
        internal BelayTopologySnapshot Before;
        internal BelayStationAttachment Observer;
        internal HashSet<long> Placed;
        internal Vector3 Body;
        internal readonly List<(Piton Piton, LogicalQuickdraw Quickdraw, BelayStationAnchor Anchor, long Collider)> Protections = new();
        internal double NextPoll, NextLog;
    }

    private StationAttachment _attachment;
    private ManualBelayStatus AttachmentStatus => new(true, false, _lease.Braked, _lease.Sequence, 0, 0, 0,
        _attachment.Before.ProtectionCount, 0, true, new BelayRopeReadback(0, 0, 0, 0, false));

    private bool CanAttachStation(Lifeline lifeline, Piton station, out string reason)
    {
        reason = "To connect a new station, remain personally anchored at a separate healthy piton within your rope's reach. Keep this backup attached until connection is confirmed.";
        var backup = PersonalAnchor(lifeline);
        if (backup == null || backup.Quickdraw == null || station == null || station.Quickdraw == null
            || Id(backup) == Id(station) || backup.pitonHp <= 0
            || lifeline.pawn == null || lifeline.harness == null || lifeline.pawn.IsFalling || lifeline.SwingHasPivotAndExtremity()
            || Vector3.Distance(lifeline.harness.GetAttachPosition(), backup.transform.position) > 1.5f) return false;
        if (!TryStartSnapshot(lifeline, out var before, out reason)) return false;
        reason = "The climber's rope cannot reach the station while retaining the personal backup.";
        if (!BelayRoutePlanner.TryPlan(ToNumerics(station.Quickdraw.Cast<IRopeHolder>().Position),
            new[] { new BelayProtectionPoint(unchecked((uint)backup.GetInstanceID()), ToNumerics(backup.Quickdraw.Cast<IRopeHolder>().Position)) },
            ToNumerics(lifeline.harness.GetAttachPosition()), lifeline.securingRope.MaxLengthMeters, out var reach)
            || !reach.ReachesClimber) return false;
        reason = "Keep the personal backup as the last connected protection, with room for one more station.";
        return before.LastPiton == Id(backup) && before.ProtectionCount < 63 && before.Parts.Length < 64 && before.Holders.Length < 66;
    }

    private bool BeginStationAttachment(Guid session, BelaySharedStation reference, Piton station, out string reason)
    {
        reason = "The station attachment session is invalid.";
        if (session == Guid.Empty || _attachment != null) return false;
        var lifeline = LocalLifeline();
        if (!CanAttachStation(lifeline, station, out reason) || !TryStartSnapshot(lifeline, out var before, out reason)) return false;
        var backup = PersonalAnchor(lifeline);
        var placed = ConnectionPlaced(lifeline);
        reason = "The station and backup must already exist in the same synchronized native piton collection.";
        if (placed == null || !placed.Contains(Id(station)) || !placed.Contains(Id(backup))) return false;
        var state = new StationAttachment
        {
            Station = reference, Lifeline = lifeline, Piton = station, Backup = backup, Quickdraw = station.Quickdraw,
            Anchor = new BelayStationAnchor(Id(lifeline), Id(lifeline.securingRope), Id(station), ToNumerics(station.transform.position)),
            BackupAnchor = new BelayStationAnchor(Id(lifeline), Id(lifeline.securingRope), Id(backup), ToNumerics(backup.transform.position)),
            Before = before, Placed = placed, Body = lifeline.harness.GetAttachPosition()
        };
        var holders = lifeline.securingRope.ropeHolders;
        var colliders = new HashSet<long> { Id(state.Quickdraw.GetAttachEnd()) };
        long robotHolder = 0;
        reason = "Existing protection quickdraws must have distinct healthy native attachment colliders.";
        for (var i = 0; i < holders.Count; i++)
        {
            var holder = holders[i];
            if (holder?.TryCast<Il2CppTheGameBakers.Cairn.RobotPawnController>() != null)
            {
                var identity = holder.Pointer.ToInt64();
                if (robotHolder != 0 && robotHolder != identity)
                { reason = "The native route contains different Climbot holders; wait for it to settle."; return false; }
                robotHolder = identity;
            }
            var quickdraw = holder?.TryCast<LogicalQuickdraw>();
            if (quickdraw == null) continue;
            var piton = quickdraw.Piton;
            var collider = Id(quickdraw.GetAttachEnd());
            if (piton == null || !float.IsFinite(piton.pitonHp) || piton.pitonHp <= 0 || collider == 0 || !colliders.Add(collider)) return false;
            state.Protections.Add((piton, quickdraw,
                new BelayStationAnchor(Id(lifeline), before.Rope, Id(piton), ToNumerics(piton.transform.position)), collider));
        }
        reason = "Wait for all original protection pins to load before connecting the station.";
        if (colliders.Contains(0) || !AttachmentPinsReady(state, false)) return false;
        // The accepted pair retains its personal backup and every original pin.
        // Native insertion may put S before or after those protections, in both
        // initial connections and role exchanges; confirm the actual native path.
        state.Observer = new BelayStationAttachment(before, ConnectionOrder(lifeline), Id(station), state.Quickdraw.Pointer.ToInt64(), Now, robotHolder);
        _lease = new BelayInputLease(session, Now);
        _localRopeNotice = null;
        _attachment = state;
        try
        {
            // The synchronized native piton is already registered. Only add its
            // existing quickdraw; do not spawn/register a second piton or force an end insertion.
            lifeline.pawn.AddQuickdrawToSecuringRope(state.Quickdraw, false, false);
            ModLog.Info("[BelayAttachment] Requested once; session=" + session + ", owner=" + reference.PitonOwner
                + ", piton=" + reference.PitonId + ", previousProtections=" + before.ProtectionCount
                + ", previousParts=" + before.Parts.Length
                + ", previousHolders=" + string.Join(",", before.Holders) + ", previousLast=" + before.LastPiton);
            reason = "Connecting the station. Keep your personal backup attached and remain still until the native route is confirmed.";
            return true;
        }
        catch (Exception ex) { reason = ex.Message; CancelStationAttachment(reason); return false; }
    }

    private void TickStationAttachment()
    {
        var state = _attachment;
        if (state == null || Now < state.NextPoll) return;
        state.NextPoll = Now + .1;
        try
        {
            if (!Enabled || _lease.IsExpired(Now) || RopeInterop.IsNativeBelayEngaged
                || (_stationLifeline != null && !_rolePreparing)
                || (_rolePreparing && (_stationSession != _roleOldSession || !StationValid))
                || !TryGetActiveContext(out _))
            { CancelStationAttachment("The station context or fresh partner-input lease ended."); return; }
            var lifeline = state.Lifeline;
            if (lifeline == null || Id(LocalLifeline()) != Id(lifeline) || Id(lifeline.securingRope) != state.Before.Rope
                || lifeline.pawn == null || lifeline.harness == null || lifeline.pawn.IsFalling || lifeline.SwingHasPivotAndExtremity()
                || Id(PersonalAnchor(lifeline)) != Id(state.Backup)
                || Vector3.Distance(lifeline.harness.GetAttachPosition(), state.Body) > .25f
                || !state.BackupAnchor.Matches(Id(lifeline), state.Before.Rope, Id(state.Backup),
                    state.Backup.pitonHp, ToNumerics(state.Backup.transform.position)))
            { CancelStationAttachment("The original personal backup or stationary character context changed."); return; }
            if (!RopeInterop.TryResolvePitonReference(lifeline, state.Station.PitonOwner, state.Station.PitonId, _localPlayerId(), out var station)
                || Id(station) != Id(state.Piton) || Id(station.Quickdraw) != Id(state.Quickdraw)
                || !state.Anchor.Matches(Id(lifeline), state.Before.Rope, Id(station), station.pitonHp, ToNumerics(station.transform.position)))
            { CancelStationAttachment("The exact synchronized partner station was lost or replaced."); return; }
            var placed = ConnectionPlaced(lifeline);
            if (placed == null || !state.Placed.SetEquals(placed))
            { CancelStationAttachment("The native piton collection changed during station attachment."); return; }
            foreach (var protection in state.Protections)
                if (protection.Piton == null || !protection.Anchor.Matches(Id(lifeline), state.Before.Rope, Id(protection.Piton),
                        protection.Piton.pitonHp, ToNumerics(protection.Piton.transform.position))
                    || Id(protection.Piton.Quickdraw) != Id(protection.Quickdraw)
                    || !lifeline.securingRope.IsAttached(protection.Quickdraw.Cast<IRopeHolder>()))
                { CancelStationAttachment("An original protection was lost or replaced during station attachment."); return; }
            var pins = AttachmentPinsReady(state, true);
            TrySnapshot(lifeline, out var snapshot, out _);
            state.Observer.Observe(snapshot, ConnectionOrder(lifeline), pins, Now);
            if (Now >= state.NextLog)
            {
                state.NextLog = Now + .5;
                ModLog.Info("[BelayAttachment] Sample; stage=" + state.Observer.Stage + ", pins=" + pins
                    + ", parts=" + snapshot?.Parts.Length + ", protections=" + snapshot?.ProtectionCount
                    + ", last=" + snapshot?.LastPiton + ", holders=" + (snapshot == null ? "unavailable" : string.Join(",", snapshot.Holders))
                    + ", order=" + string.Join(",", ConnectionOrder(lifeline))
                    + ", originalBackup=True, collectionUnchanged=True");
            }
            if (state.Observer.Stage == BelayStationAttachmentStage.Failed)
            { CancelStationAttachment("The station route did not confirm: " + state.Observer.Failure); return; }
            if (state.Observer.Stage != BelayStationAttachmentStage.Ready) return;
            if (_rolePreparing) { _roleConnectionReady = true; return; }
            CompleteStationAttachment(state, _lease);
        }
        catch (Exception ex)
        {
            _localRopeNotice = "Station attachment failed: " + ex.Message;
            ModLog.Warning("[BelayAttachment] " + _localRopeNotice);
            CancelStationAttachment(ex.Message);
            if (_control != null) ClearControl("Station attachment control failed: " + ex.Message);
            else { _lease = null; ClearPartnerStation(); }
        }
    }

    private bool CompleteStationAttachment(StationAttachment state, BelayInputLease lease)
    {
        _attachment = null;
        if (!BindPartnerStation(lease.Session, state.Station, state.Piton, out var reason))
        { _lease = null; _localRopeNotice = reason; ModLog.Warning("[BelayAttachment] Control refused after connection: " + reason); return false; }
        _lease = lease; _lease.CancelMotion(); ApplyBrake(_lease.Braked);
        ModLog.Info("[BelayAttachment] Confirmed; five observed seconds, original protection retained; fresh input required for rope motion.");
        return true;
    }

    private static bool AttachmentPinsReady(StationAttachment state, bool stationAttached)
    {
        var parts = state.Lifeline.securingRope.ropeParts;
        if (parts == null || parts.Count < 1 || parts.Count > 64) return false;
        var pins = new List<long>();
        var physical = new BelayFallRopePart[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (part?.obiRope == null || !part.obiRope.isLoaded || part.pinConstraintHandler == null) return false;
            var begin = Id(part.pinConstraintHandler.GetAttachCollider(RopeSide.Begin));
            var end = Id(part.pinConstraintHandler.GetAttachCollider(RopeSide.End));
            pins.Add(begin); pins.Add(end);
            physical[i] = new(Id(part), begin, end, part.obiRope.restLength, true);
        }
        var station = Id(state.Quickdraw.GetAttachEnd());
        var backup = Id(state.Backup.Quickdraw.GetAttachEnd());
        if (!BelayStationConnectionTrial.PinsAgree(station, backup, pins, stationAttached)) return false;
        foreach (var protection in state.Protections)
        {
            var count = 0;
            foreach (var pin in pins) if (pin == protection.Collider) count++;
            if (count < 2) return false;
        }
        if (!stationAttached) return true;
        // Robot holders can move in the logical list without adding physical
        // segments. Confirm the actual station-to-harness pin walk separately.
        var holders = state.Lifeline.securingRope.ropeHolders;
        var harness = state.Lifeline.harness;
        if (holders == null || holders.Count > 66 || harness == null) return false;
        var walk = new List<long>();
        var started = false; var ended = false;
        for (var i = 0; i < holders.Count; i++)
        {
            var holder = holders[i];
            if (holder == null) return false;
            if (!started)
            {
                if (holder.Pointer != state.Quickdraw.Pointer) continue;
                started = true;
            }
            if (holder.Pointer == harness.Pointer)
            { walk.Add(Id(harness.GetAttachEnd())); ended = true; break; }
            if (holder.TryCast<Il2CppTheGameBakers.Cairn.RobotPawnController>() != null) continue;
            var quickdraw = holder.TryCast<LogicalQuickdraw>();
            if (quickdraw?.Piton == null || !(quickdraw.Piton.pitonHp > 0)) return false;
            walk.Add(Id(quickdraw.GetAttachEnd()));
        }
        return started && ended && BelayFallRopeRoute.TryMeasure(walk.ToArray(), physical,
            new int[physical.Length], out _, out _, out _);
    }

    private void CancelStationAttachment(string reason)
    {
        if (_attachment == null) return;
        if (_rolePreparing) _roleFailure = reason;
        ModLog.Info("[BelayAttachment] Stopped; no additional native requests or automatic rollback: " + reason);
        _attachment = null;
        _lease?.CancelMotion(); _lease = null;
        _localRopeNotice = reason;
    }
}
