using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Global state of active rope links (playerId pairs), shared between the network
/// (updated from ServerRopeClip) and rendering (RopeLinkRenderer). Each link is
/// stored once, in normalized form (min/max) so that (a,b) == (b,a).
/// </summary>
public static class RopeLinkState
{
    private static readonly HashSet<long> _links = new();

    private static long Key(int a, int b)
    {
        uint lo = (uint)Math.Min(a, b);
        uint hi = (uint)Math.Max(a, b);
        return ((long)lo << 32) | hi;
    }

    private static int High(long k) => (int)(uint)(k >> 32);
    private static int Low(long k) => (int)(uint)(k & 0xFFFFFFFFL);

    public static int Count => _links.Count;

    /// <summary>Adds (clip=true) or removes (clip=false) the link between a and b.</summary>
    public static void Apply(int a, int b, bool clip)
    {
        if (a == b) return;
        var k = Key(a, b);
        if (clip) _links.Add(k);
        else _links.Remove(k);
    }

    /// <summary>Enumerates the active links as (a, b).</summary>
    public static IEnumerable<(int a, int b)> Links()
    {
        foreach (var k in _links)
            yield return (High(k), Low(k));
    }

    /// <summary>True if a and b are roped together.</summary>
    public static bool IsLinked(int a, int b) => a != b && _links.Contains(Key(a, b));

    /// <summary>First rope partner of <paramref name="playerId"/>, or -1.</summary>
    public static int PartnerOf(int playerId)
    {
        foreach (var k in _links)
        {
            int hi = High(k), lo = Low(k);
            if (hi == playerId) return lo;
            if (lo == playerId) return hi;
        }
        return -1;
    }

    /// <summary>Removes all links involving <paramref name="playerId"/> (a player leaving).</summary>
    public static void RemovePlayer(int playerId)
    {
        _links.RemoveWhere(k => High(k) == playerId || Low(k) == playerId);
    }

    public static void Clear() => _links.Clear();
}
