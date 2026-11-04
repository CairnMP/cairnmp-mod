using System;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>Bounds drawing cost without dropping a protection junction from a long route.</summary>
internal static class BelayRopeVisualSampling
{
    internal static WorldPosition[] Sample(WorldPosition[][] segments, int maximum)
    {
        if (segments == null || segments.Length == 0 || segments.Length > 64 || maximum < segments.Length * 2) return null;
        var counts = new int[segments.Length];
        var total = segments.Length * 2;
        for (var i = 0; i < counts.Length; i++)
        {
            if (segments[i] == null || segments[i].Length < 2 || segments[i].Length > 4096) return null;
            counts[i] = 2;
        }
        while (total < maximum)
        {
            var added = false;
            for (var i = 0; i < counts.Length && total < maximum; i++)
                if (counts[i] < segments[i].Length) { counts[i]++; total++; added = true; }
            if (!added) break;
        }
        var points = new WorldPosition[total];
        var offset = 0;
        for (var i = 0; i < segments.Length; i++)
            for (var j = 0; j < counts[i]; j++)
                points[offset++] = segments[i][(int)((long)j * (segments[i].Length - 1) / (counts[i] - 1))];
        return points;
    }
}
