using System;

namespace CairnMultiplayerMod.Internal.Game.Voice;

internal static class VoiceSpatialPolicy
{
    internal static bool HasRecentPose(double lastUpdate, double now)
        => double.IsFinite(lastUpdate) && double.IsFinite(now) && lastUpdate > 0
           && now >= lastUpdate && now - lastUpdate <= 2;

    internal static float Attenuation(float distance)
    {
        if (!float.IsFinite(distance) || distance >= 30) return 0;
        if (distance <= 5) return 1;
        if (distance <= 20) return 1 - (distance - 5) * .45f / 15;
        if (distance <= 25) return .55f - (distance - 20) * .15f / 5;
        return .4f - (distance - 25) * .4f / 5;
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

    internal static float Cutoff(float distance)
    {
        var progress = float.IsFinite(distance) ? Math.Clamp((distance - 5) / 25, 0, 1) : 1;
        return 18000 * MathF.Pow(1f / 3, progress);
    }
}
