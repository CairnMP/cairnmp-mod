using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.Internal.Diagnostics;

internal sealed class BelayTopologySnapshot
{
    internal long Rope { get; }
    internal long LastPiton { get; }
    internal long[] Parts { get; }
    internal long[] Holders { get; }
    internal float Available { get; }
    internal int ProtectionCount { get; }

    internal BelayTopologySnapshot(long rope, long lastPiton, long[] parts, long[] holders,
        float available, int protectionCount)
    {
        Rope = rope; LastPiton = lastPiton;
        Parts = (long[])(parts ?? Array.Empty<long>()).Clone();
        Holders = (long[])(holders ?? Array.Empty<long>()).Clone();
        Available = available; ProtectionCount = protectionCount;
    }

    internal bool IsValid => TryValidate(out _);

    internal bool TryValidate(out string reason)
    {
        reason = "The rope or last piton identity is unavailable.";
        if (Rope == 0 || LastPiton == 0) return false;
        reason = "The protection route exceeds the supported segment or holder bounds.";
        if (Parts.Length < 2 || Parts.Length > 64 || Holders.Length < 1 || Holders.Length > 66
            || ProtectionCount < 1 || ProtectionCount > 63) return false;
        reason = "The remaining rope capacity is invalid.";
        if (!float.IsFinite(Available) || Available <= 0 || Available > 200) return false;
        var seen = new HashSet<long>();
        for (var i = 0; i < Parts.Length; i++)
        {
            if (Parts[i] == 0)
            {
                reason = $"Protection segment {i + 1} has no live identity.";
                return false;
            }
            if (!seen.Add(Parts[i]))
            {
                reason = $"Protection segment {i + 1} repeats another physical segment.";
                return false;
            }
        }
        // Holders describe an ordered walk, not a set of physical constraints.
        // A native endpoint can occur more than once; retain every occurrence so
        // Matches still detects changes in order, position and multiplicity.
        for (var i = 0; i < Holders.Length; i++)
            if (Holders[i] == 0)
            {
                reason = $"Protection holder {i + 1} has no live identity.";
                return false;
            }
        reason = "";
        return true;
    }

    internal bool Matches(BelayTopologySnapshot other)
        => other != null && Rope == other.Rope && LastPiton == other.LastPiton
            && Equal(Parts, other.Parts) && Equal(Holders, other.Holders);

    private static bool Equal(long[] a, long[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}

internal enum BelayTopologyChange { Invalid, Unchanged, Settling, Rebound }

/// <summary>Accept stable ordered routes while unloaded; never swap a loaded mapping.</summary>
internal sealed class BelayTopologyTracker
{
    internal const double SettleSeconds = .15;
    internal BelayTopologySnapshot Current { get; private set; }
    internal uint Revision { get; private set; }
    internal bool IsSettling => _pending != null;
    private BelayTopologySnapshot _pending;
    private double _pendingSince, _lastObservation;

    internal bool RearmAfterSuspension()
    {
        if (Current == null || Revision == uint.MaxValue) return false;
        // The same pitons can protect a new fall with a different native joint.
        // Old application acknowledgements must not describe that new suspension.
        Revision++; _pending = null;
        return true;
    }

    internal BelayTopologyChange ObserveProtectionAppend(BelayTopologySnapshot snapshot,
        IReadOnlyList<BelayProtectionPoint> previous, IReadOnlyList<BelayProtectionPoint> next, double now)
    {
        if (Current == null || snapshot?.IsValid != true || snapshot.Rope != Current.Rope
            || !double.IsFinite(now) || now < _lastObservation) return BelayTopologyChange.Invalid;
        if (!BelayRoutePlanner.IsSingleProtectionAppend(previous, next)
            || snapshot.ProtectionCount != Current.ProtectionCount + 1
            || snapshot.Parts.Length != Current.Parts.Length + 1
            || snapshot.Holders.Length != Current.Holders.Length + 1)
        {
            // Clipping splits one part and adds one holder. The native interaction
            // can still hold the rope after adding the piton, leaving another
            // temporary part/holder until release. Do not promote that mapping
            // or reuse its settling time when the completed split arrives.
            // The caller bounds this wait and ends control on timeout or load.
            _lastObservation = now;
            _pending = null;
            return BelayTopologyChange.Settling;
        }
        return Observe(snapshot, false, now);
    }

    internal BelayTopologyChange ObserveProtectionRestore(BelayTopologySnapshot snapshot,
        IReadOnlyList<BelayProtectionPoint> previous, IReadOnlyList<BelayProtectionPoint> next,
        bool samePhysicalPins, double now)
    {
        if (Current == null || snapshot?.IsValid != true || snapshot.Rope != Current.Rope
            || !double.IsFinite(now) || now < _lastObservation) return BelayTopologyChange.Invalid;
        _lastObservation = now;
        var same = samePhysicalPins && previous != null && next != null
            && previous.Count == Current.ProtectionCount && next.Count == previous.Count
            && snapshot.ProtectionCount == Current.ProtectionCount && snapshot.LastPiton == Current.LastPiton
            && snapshot.Parts.Length == Current.Parts.Length && snapshot.Holders.Length == Current.Holders.Length;
        if (same)
        {
            var ids = new HashSet<uint>();
            for (var i = 0; i < previous.Count; i++)
                if (previous[i].Id == 0 || previous[i].Id != next[i].Id || !ids.Add(previous[i].Id)) { same = false; break; }
            for (var i = 0; same && i < Current.Holders.Length; i++)
                if (snapshot.Holders[i] != Current.Holders[i]) same = false;
        }
        if (!same) { _pending = null; return BelayTopologyChange.Settling; }
        // Personal anchoring can temporarily split the rope and recreate its
        // segments. Restore only the exact holder/protection walk and pin walk,
        // after fresh observations; this never authorizes a removal or reorder.
        if (_pending?.Matches(snapshot) != true)
        { _pending = snapshot; _pendingSince = now; return BelayTopologyChange.Settling; }
        if (now - _pendingSince < SettleSeconds) return BelayTopologyChange.Settling;
        if (Revision == uint.MaxValue) return BelayTopologyChange.Invalid;
        Current = snapshot; _pending = null; Revision++;
        return BelayTopologyChange.Rebound;
    }

    internal BelayTopologyChange ObserveUpstreamRemoval(BelayTopologySnapshot snapshot,
        IReadOnlyList<BelayProtectionPoint> previous, IReadOnlyList<BelayProtectionPoint> next, uint station,
        IReadOnlyList<long> previousPins, IReadOnlyList<long> nextPins, bool suspended, double now)
    {
        if (Current == null || snapshot?.IsValid != true || snapshot.Rope != Current.Rope || suspended
            || !double.IsFinite(now) || now < _lastObservation) return BelayTopologyChange.Invalid;
        if (!SameProtectedSuffix(snapshot, previous, next, station, previousPins, nextPins))
        { _lastObservation = now; _pending = null; return BelayTopologyChange.Settling; }
        return Observe(snapshot, false, now);
    }

    private bool SameProtectedSuffix(BelayTopologySnapshot snapshot, IReadOnlyList<BelayProtectionPoint> previous,
        IReadOnlyList<BelayProtectionPoint> next, uint station, IReadOnlyList<long> previousPins, IReadOnlyList<long> nextPins)
    {
        if (station == 0 || previous == null || next == null || next.Count >= previous.Count
            || previous.Count != Current.ProtectionCount || next.Count != snapshot.ProtectionCount
            || snapshot.LastPiton != Current.LastPiton || Current.Parts.Length != previous.Count + 1
            || snapshot.Parts.Length != next.Count + 1 || Current.Holders.Length != previous.Count + 2
            || snapshot.Holders.Length != next.Count + 2 || previousPins?.Count != Current.Parts.Length * 2
            || nextPins?.Count != snapshot.Parts.Length * 2 || Current.Holders[0] != snapshot.Holders[0]) return false;
        var beforeStation = -1;
        var afterStation = -1;
        var seen = new HashSet<uint>();
        for (var i = 0; i < previous.Count; i++)
        {
            if (previous[i].Id == 0 || !seen.Add(previous[i].Id)) return false;
            if (previous[i].Id == station) beforeStation = i;
        }
        seen.Clear();
        for (var i = 0; i < next.Count; i++)
        {
            if (next[i].Id == 0 || !seen.Add(next[i].Id)) return false;
            if (next[i].Id == station) afterStation = i;
        }
        if (beforeStation <= afterStation || afterStation < 0
            || previous.Count - beforeStation != next.Count - afterStation) return false;
        var oldIndex = 0;
        for (var i = 0; i < afterStation; i++)
        {
            while (oldIndex < beforeStation && previous[oldIndex].Id != next[i].Id) oldIndex++;
            if (oldIndex >= beforeStation || Current.Holders[oldIndex + 1] != snapshot.Holders[i + 1]) return false;
            oldIndex++;
        }
        for (var i = 0; i < next.Count - afterStation; i++)
            if (previous[beforeStation + i].Id != next[afterStation + i].Id) return false;
        // Native removal may rebuild the upstream segment. From the station
        // through the harness, retain every holder, physical segment and pin.
        for (var i = 0; i < snapshot.Holders.Length - afterStation - 1; i++)
            if (Current.Holders[beforeStation + 1 + i] != snapshot.Holders[afterStation + 1 + i]) return false;
        for (var i = 0; i < snapshot.Parts.Length - afterStation - 1; i++)
        {
            var oldPart = beforeStation + 1 + i;
            var newPart = afterStation + 1 + i;
            if (Current.Parts[oldPart] != snapshot.Parts[newPart]) return false;
            for (var side = 0; side < 2; side++)
                if (previousPins[oldPart * 2 + side] == 0
                    || previousPins[oldPart * 2 + side] != nextPins[newPart * 2 + side]) return false;
        }
        return true;
    }

    internal BelayTopologyChange Observe(BelayTopologySnapshot snapshot, bool suspended, double now)
    {
        if (snapshot?.IsValid != true || !double.IsFinite(now) || now < _lastObservation)
            return BelayTopologyChange.Invalid;
        _lastObservation = now;
        if (Current == null)
        {
            Current = snapshot; Revision = 1;
            return BelayTopologyChange.Rebound;
        }
        if (snapshot.Rope != Current.Rope) return BelayTopologyChange.Invalid;
        if (Current.Matches(snapshot))
        {
            Current = snapshot; _pending = null;
            return BelayTopologyChange.Unchanged;
        }
        if (suspended || Revision == uint.MaxValue) return BelayTopologyChange.Invalid;
        if (_pending?.Matches(snapshot) != true)
        {
            _pending = snapshot; _pendingSince = now;
            return BelayTopologyChange.Settling;
        }
        if (now - _pendingSince < SettleSeconds) return BelayTopologyChange.Settling;
        Current = snapshot; _pending = null; Revision++;
        return BelayTopologyChange.Rebound;
    }
}
