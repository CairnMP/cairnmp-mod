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

    internal bool IsValid
    {
        get
        {
            if (Rope == 0 || LastPiton == 0 || Parts.Length < 2 || Parts.Length > 64
                || Holders.Length < 1 || Holders.Length > 66 || ProtectionCount < 1 || ProtectionCount > 63
                || !float.IsFinite(Available) || Available <= 0 || Available > 200) return false;
            var seen = new HashSet<long>();
            foreach (var part in Parts) if (part == 0 || !seen.Add(part)) return false;
            seen.Clear();
            foreach (var holder in Holders) if (holder == 0 || !seen.Add(holder)) return false;
            return true;
        }
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
