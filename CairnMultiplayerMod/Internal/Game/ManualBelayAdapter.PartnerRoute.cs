using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private readonly long[] _partnerRouteColliders = new long[65];
    private readonly Vector3[] _partnerRoutePositions = new Vector3[65];
    private readonly BelayFallRopePart[] _partnerRouteParts = new BelayFallRopePart[64];
    private readonly int[] _partnerRouteSelected = new int[64];
    private readonly long[] _partnerRouteFingerprint = new long[512];
    private readonly long[] _partnerRouteScratch = new long[512];
    private int _partnerRouteSize, _partnerRouteLegs;
    private uint _partnerRouteRevision;

    // Both measurement and drawing use this exact native station-to-harness
    // pin walk. Climbot holder-list padding is not a protection or physical leg.
    private bool TryReadPartnerRoute(out float length, out string reason)
    {
        length = float.NaN;
        _partnerRouteLegs = 0;
        reason = "The agreed station is unavailable or unhealthy.";
        var station = _localStationPiton;
        if (station == null || !(station.pitonHp > 0) || !LocalStationMatches(_lifeline, out _)) return false;
        return TryReadPartnerRoute(_lifeline, station, out length, out reason);
    }

    private bool TryReadPartnerRoute(Lifeline lifeline, Piton station, out float length, out string reason,
        bool trackRevision = true)
    {
        length = float.NaN;
        if (trackRevision) _partnerRouteLegs = 0;
        var rope = lifeline?.securingRope;
        var harness = lifeline?.harness;
        reason = "The native protection rope is not initialized.";
        if (rope?.IsInitialized != true || harness == null) return false;
        var holders = rope.ropeHolders;
        var parts = rope.ropeParts;
        reason = "The native route exceeds its bounded holder/segment limits.";
        if (holders == null || holders.Count < 1 || holders.Count > 66
            || parts == null || parts.Count < 1 || parts.Count > 64) return false;
        var first = -1; var end = -1;
        for (var i = 0; i < holders.Count; i++)
        {
            var holder = holders[i];
            reason = "A native rope holder is unavailable.";
            if (holder == null) return false;
            if (holder.Pointer.ToInt64() == Id(harness))
            {
                reason = "The native harness occurs more than once.";
                if (end >= 0) return false;
                end = i;
            }
            if (Id(holder.TryCast<LogicalQuickdraw>()?.Piton) == Id(station))
            {
                reason = "The agreed station occurs more than once.";
                if (first >= 0) return false;
                first = i;
            }
        }
        reason = "The agreed station-to-harness holder order is not ready.";
        if (first < 0 || end <= first || end - first > 64) return false;
        var last = lifeline.GetLastSecuringPiton();
        reason = "The last securing piton is unavailable or unhealthy.";
        if (last == null || !(last.pitonHp > 0)) return false;
        var scratch = _partnerRouteScratch;
        var size = 0;
        scratch[size++] = Id(rope); scratch[size++] = Id(station); scratch[size++] = Id(last);
        scratch[size++] = end - first;
        long walkedLast = 0;
        var connectorCount = 0;
        for (var i = first; i <= end; i++)
        {
            var holder = holders[i];
            if (i != end && holder.TryCast<Il2CppTheGameBakers.Cairn.RobotPawnController>() != null)
            {
                // Native insertion can move repeated Climbot markers after the
                // station. Retain their identity/order in the revision, but only
                // exact quickdraw-to-harness pin matches can form measured legs.
                scratch[size++] = holder.Pointer.ToInt64(); scratch[size++] = 0; scratch[size++] = 0;
                continue;
            }
            var quickdraw = holder.TryCast<LogicalQuickdraw>();
            var piton = quickdraw?.Piton;
            if (i != end && (quickdraw == null || piton == null || !(piton.pitonHp > 0)))
            { reason = "A protection inside the measured native path is unavailable or unsupported."; return false; }
            var collider = i == end ? Id(harness.GetAttachEnd()) : Id(quickdraw.GetAttachEnd());
            reason = "A native route attachment collider is unavailable.";
            if (collider == 0) return false;
            var position = i == end ? harness.GetAttachPosition() : holder.Position;
            if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z))
            { reason = "A native route attachment position is invalid."; return false; }
            _partnerRouteColliders[connectorCount] = collider;
            _partnerRoutePositions[connectorCount++] = position;
            scratch[size++] = holder.Pointer.ToInt64(); scratch[size++] = Id(piton); scratch[size++] = collider;
            if (piton != null) walkedLast = Id(piton);
        }
        reason = "The measured path does not end at the current securing piton.";
        if (walkedLast != Id(last)) return false;
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i]; var native = part?.obiRope; var handler = part?.pinConstraintHandler;
            var loaded = native != null && native.isLoaded && handler != null;
            _partnerRouteParts[i] = new(Id(part),
                loaded ? Id(handler.GetAttachCollider(RopeSide.Begin)) : 0,
                loaded ? Id(handler.GetAttachCollider(RopeSide.End)) : 0,
                loaded ? native.restLength : float.NaN, loaded);
        }
        if (!BelayFallRopeRoute.TryMeasure(_partnerRouteColliders.AsSpan(0, connectorCount),
            _partnerRouteParts.AsSpan(0, parts.Count), _partnerRouteSelected,
            out var measured, out var failure, out var failedLeg))
        {
            reason = "Native pin path: " + failure + (failedLeg >= 0 ? "; segment=" + (failedLeg + 1) : "");
            return false;
        }
        if (!trackRevision) { length = measured; reason = ""; return true; }
        for (var i = 0; i < connectorCount - 1; i++)
        {
            var selected = _partnerRouteParts[_partnerRouteSelected[i]];
            scratch[size++] = selected.Id; scratch[size++] = selected.Begin; scratch[size++] = selected.End;
        }
        var changed = size != _partnerRouteSize;
        for (var i = 0; !changed && i < size; i++) changed = scratch[i] != _partnerRouteFingerprint[i];
        if (changed)
        {
            reason = "The native path revision limit was reached.";
            if (_partnerRouteRevision == uint.MaxValue) return false;
            Array.Copy(scratch, _partnerRouteFingerprint, size);
            _partnerRouteSize = size;
            _partnerRouteRevision++;
        }
        _partnerRouteLegs = connectorCount - 1;
        length = measured;
        reason = "";
        return true;
    }

    private void ResetPartnerRoute()
    {
        _partnerRouteSize = _partnerRouteLegs = 0;
        _partnerRouteRevision = 0;
    }
}
