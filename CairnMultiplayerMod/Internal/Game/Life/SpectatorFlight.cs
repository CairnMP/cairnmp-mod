namespace CairnMultiplayerMod.Internal.Game.Life;

/// <summary>
/// How the free camera moves. The stick, the triggers, and a left-stick hold do the same
/// work as the keys: forward, sideways, up, and faster.
/// </summary>
internal static class SpectatorFlight
{
    internal const float LookDegreesPerSecond = 120f;
    private const float Deadzone = 0.2f;

    internal readonly struct Axes
    {
        internal Axes(float forward, float strafe, float up, bool faster)
        {
            Forward = forward;
            Strafe = strafe;
            Up = up;
            Faster = faster;
        }

        internal float Forward { get; }
        internal float Strafe { get; }
        internal float Up { get; }
        internal bool Faster { get; }
    }

    internal static Axes Combine(
        float keyForward, float keyStrafe, float keyUp, bool keyFaster,
        float stickX, float stickY, float rise, float sink, bool stickFaster)
        => new(
            Clamp(keyForward + Axis(stickY)),
            Clamp(keyStrafe + Axis(stickX)),
            Clamp(keyUp + Axis(rise) - Axis(sink)),
            keyFaster || stickFaster);

    private static float Axis(float value)
    {
        if (!float.IsFinite(value) || value > -Deadzone && value < Deadzone) return 0f;
        return value;
    }

    private static float Clamp(float value)
    {
        if (!float.IsFinite(value)) return 0f;
        if (value > 1f) return 1f;
        if (value < -1f) return -1f;
        return value;
    }
}
