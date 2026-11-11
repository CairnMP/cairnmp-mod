namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// A short release of D-pad up takes a piton. A longer press is Cairn's hold to place one,
/// so that press is left alone.
/// </summary>
internal static class PitonTakeTap
{
    /// <summary>At or under this, the release is a tap. Past it, the press belongs to placement.</summary>
    internal const float MaxSeconds = 0.25f;

    /// <summary>
    /// Follows one button. <paramref name="pressedAt"/> stays negative while the button is up.
    /// Returns true on the release of a press no longer than <paramref name="maxSeconds"/>.
    /// <paramref name="cancel"/> drops the press without taking, which is how a modifier chord
    /// keeps its own meaning.
    /// </summary>
    internal static bool Advance(ref float pressedAt, bool down, bool cancel, float now, float maxSeconds)
    {
        if (cancel || !float.IsFinite(now) || !float.IsFinite(maxSeconds) || maxSeconds <= 0f)
        {
            pressedAt = -1f;
            return false;
        }

        if (down)
        {
            if (pressedAt < 0f) pressedAt = now;
            return false;
        }

        if (pressedAt < 0f) return false;
        var held = now - pressedAt;
        pressedAt = -1f;
        return float.IsFinite(held) && held >= 0f && held <= maxSeconds;
    }
}
