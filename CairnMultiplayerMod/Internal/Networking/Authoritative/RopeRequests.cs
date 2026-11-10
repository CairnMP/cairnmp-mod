#nullable enable
using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.Internal.Networking.Authoritative;

internal static class RopeRequests
{
    /// <summary>L ties the rope only when the two climbers are at most this far apart.</summary>
    internal const float LinkRangeMeters = 1f;

    internal static bool WithinLinkRange(float ax, float ay, float az, float bx, float by, float bz)
    {
        var dx = ax - bx;
        var dy = ay - by;
        var dz = az - bz;
        var distanceSquared = dx * dx + dy * dy + dz * dz;
        return float.IsFinite(distanceSquared)
            && distanceSquared <= LinkRangeMeters * LinkRangeMeters;
    }

    internal static bool IsAllowed(int source, int target, bool clip,
        IEnumerable<(int a, int b)> links, Func<int, bool> isAdmitted)
    {
        if (source <= 0 || target <= 0 || source == target || !isAdmitted(source)) return false;
        if (clip && !isAdmitted(target)) return false;
        foreach (var (a, b) in links)
        {
            var same = (a == source && b == target) || (b == source && a == target);
            if (same) return !clip; // idempotent add; removal remains available
            if (clip && (a == source || b == source || a == target || b == target)) return false;
        }
        return clip;
    }
}
