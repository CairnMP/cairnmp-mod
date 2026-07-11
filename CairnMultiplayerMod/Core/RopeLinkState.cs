using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Etat global des liens d'encordement actifs (paires de playerId), partage entre le
/// reseau (mis a jour depuis ServerRopeClip) et le rendu (RopeLinkRenderer). Chaque
/// lien est stocke une fois, sous forme normalisee (min/max) pour que (a,b) == (b,a).
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

    /// <summary>Ajoute (clip=true) ou retire (clip=false) le lien entre a et b.</summary>
    public static void Apply(int a, int b, bool clip)
    {
        if (a == b) return;
        var k = Key(a, b);
        if (clip) _links.Add(k);
        else _links.Remove(k);
    }

    /// <summary>Enumere les liens actifs en (a, b).</summary>
    public static IEnumerable<(int a, int b)> Links()
    {
        foreach (var k in _links)
            yield return (High(k), Low(k));
    }

    /// <summary>Vrai si a et b sont encordes ensemble.</summary>
    public static bool IsLinked(int a, int b) => a != b && _links.Contains(Key(a, b));

    /// <summary>Premier partenaire encorde de <paramref name="playerId"/>, ou -1.</summary>
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

    /// <summary>Retire tous les liens impliquant <paramref name="playerId"/> (depart d'un joueur).</summary>
    public static void RemovePlayer(int playerId)
    {
        _links.RemoveWhere(k => High(k) == playerId || Low(k) == playerId);
    }

    public static void Clear() => _links.Clear();
}
