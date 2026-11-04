using System;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal static class RopeAttachmentPolicy
{
    // A newly linked pair starts with enough room to move, rather than with the
    // complete coil paid out. Keep the slack bounded so a near-limit link stays
    // below the rope's physical maximum as well.
    private const float InitialSlackMeters = .75f;

    internal static bool CanReach(float maxLength, float distance)
        => float.IsFinite(maxLength) && float.IsFinite(distance)
           && maxLength > 0 && distance >= 0 && distance < maxLength;

    internal static bool TryGetInitialLength(float maxLength, float distance, out float length)
    {
        length = 0;
        if (!CanReach(maxLength, distance)) return false;

        // Reserve half of the remaining distance when the players were clipped
        // near the limit; otherwise a fixed small amount feels natural on flat
        // ground and still leaves room for climbing movement.
        var slack = Math.Min(InitialSlackMeters, (maxLength - distance) * .5f);
        length = distance + slack;
        return float.IsFinite(length) && length > distance && length < maxLength;
    }

    /// <summary>
    /// The paid-out length has reached the request. A cloned part still reports the
    /// prefab's full length until the physics step accepts the snap, and that full
    /// length is what draws the line to the horizon.
    /// </summary>
    internal static bool SnapLanded(float requested, float reported)
    {
        if (!float.IsFinite(requested) || !float.IsFinite(reported) || requested <= 0f) return false;
        var tolerance = Math.Max(0.5f, requested * 0.15f);
        return Math.Abs(reported - requested) <= tolerance;
    }
}
