using System;

namespace CairnMultiplayerMod.Internal.Game.Voice;

internal static class VoiceSpatialPolicy
{
    internal const float NormalMaxDistance = 40f;
    internal const float ReverberantMaxDistance = 70f;

    internal static bool HasRecentPose(double lastUpdate, double now)
        => double.IsFinite(lastUpdate) && double.IsFinite(now) && lastUpdate > 0
           && now >= lastUpdate && now - lastUpdate <= 2;

    internal static float Attenuation(float distance, float maxDistance = NormalMaxDistance)
    {
        if (!float.IsFinite(distance) || !float.IsFinite(maxDistance) || distance >= maxDistance) return 0;
        if (distance <= 5) return 1;
        if (distance <= 20) return 1 - (distance - 5) * .4f / 15;
        if (distance <= 30) return .6f - (distance - 20) * .25f / 10;
        var hasReverberantTail = maxDistance > NormalMaxDistance;
        var tailStart = hasReverberantTail ? .18f : 0;
        if (distance <= NormalMaxDistance)
            return .35f - (distance - 30) * (.35f - tailStart) / 10;
        if (!hasReverberantTail) return 0;
        return tailStart * (1 - (distance - NormalMaxDistance) / (maxDistance - NormalMaxDistance));
    }

    internal static void PanGains(float volume, float pan, out float left, out float right)
    {
        pan = Math.Clamp(pan, -1, 1);
        left = MathF.Sqrt((1 - pan) * .5f);
        right = MathF.Sqrt((1 + pan) * .5f);
        var compensation = 1 / Math.Max(left, right);
        left *= volume * compensation;
        right *= volume * compensation;
    }

    internal static float Cutoff(float distance, float maxDistance = NormalMaxDistance)
    {
        var span = Math.Max(1, maxDistance - 5);
        var progress = float.IsFinite(distance) ? Math.Clamp((distance - 5) / span, 0, 1) : 1;
        return 18000 * MathF.Pow(1f / 3, progress);
    }

    internal static float Reverb(float distance, float zoneAmount, float maxDistance)
    {
        if (!float.IsFinite(zoneAmount) || zoneAmount <= 0) return 0;
        var progress = float.IsFinite(distance) ? Math.Clamp(distance / Math.Max(1, maxDistance), 0, 1) : 1;
        return Math.Clamp(zoneAmount * (.65f + .35f * progress), 0, .5f);
    }
}
