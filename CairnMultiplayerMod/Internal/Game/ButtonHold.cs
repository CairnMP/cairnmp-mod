namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// A button that has to stay down. The first frame past <paramref name="seconds"/> returns
/// true once. Letting go early, or holding on after that, does not.
/// </summary>
internal static class ButtonHold
{
    internal static bool Advance(ref float pressedAt, ref bool fired, bool down, float now, float seconds)
    {
        if (!down || !float.IsFinite(now) || !float.IsFinite(seconds) || seconds <= 0f)
        {
            pressedAt = -1f;
            fired = false;
            return false;
        }

        if (pressedAt < 0f) pressedAt = now;
        if (fired || now - pressedAt < seconds) return false;
        fired = true;
        return true;
    }
}
