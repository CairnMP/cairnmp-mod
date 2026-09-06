#nullable enable
using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.Internal.Networking.Authoritative;

internal static class RopeRequests
{
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
