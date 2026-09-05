#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CairnMultiplayerMod.Internal.Networking.Authoritative;

/// <summary>A bounded per-peer sliding burst budget using a monotonic clock.</summary>
internal sealed class PeerBudget
{
    private readonly Dictionary<int, (double at, double tokens)> _peers = new();
    private readonly int _burst;
    private readonly double _perSecond;
    private readonly Func<double> _now;

    internal PeerBudget(int burst, double perSecond, Func<double>? now = null)
    {
        _burst = burst;
        _perSecond = perSecond;
        _now = now ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }

    internal bool Take(int playerId)
    {
        var now = _now();
        var tokens = _peers.TryGetValue(playerId, out var entry)
            ? Math.Min(_burst, entry.tokens + Math.Max(0, now - entry.at) * _perSecond)
            : _burst;
        var accepted = tokens >= 1;
        _peers[playerId] = (now, accepted ? tokens - 1 : tokens);
        return accepted;
    }

    internal void Remove(int playerId) => _peers.Remove(playerId);
    internal void Clear() => _peers.Clear();
}
