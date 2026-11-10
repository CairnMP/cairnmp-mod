using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>Use the same fresh replicated body point on both sides of an interaction.</summary>
internal static class PlayerInteractionPose
{
    internal const double MaximumAgeSeconds = 2;

    internal static bool IsFresh(double capturedAt, double now)
        => double.IsFinite(capturedAt) && double.IsFinite(now) && capturedAt > 0
            && now >= capturedAt && now - capturedAt <= MaximumAgeSeconds;

    internal static bool TryRead(NetFrameData frame, double capturedAt, double now, out WorldPosition position)
    {
        position = default;
        if (!IsFresh(capturedAt, now) || !frame.IsValid || frame.Positions == null || frame.Positions.Length < 3) return false;
        var x = frame.Positions[0]; var y = frame.Positions[1]; var z = frame.Positions[2];
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
        position = new WorldPosition(x, y, z);
        return true;
    }
}
