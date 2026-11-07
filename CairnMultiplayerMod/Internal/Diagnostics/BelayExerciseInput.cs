using System;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>Bounded local stand-in for a second player holding the take-in command.</summary>
internal sealed class BelayExerciseInput
{
    internal const double DurationSeconds = 20;
    private double _until, _lastTime = -1;
    private bool _ignoreTakeInUntilRelease;
    internal bool TakingIn { get; private set; }

    internal int Resolve(bool eligible, bool toggleTakeIn, bool feedHeld, bool takeInHeld, double now)
    {
        if (!eligible || !double.IsFinite(now) || now < 0 || now < _lastTime)
        {
            Reset();
            return 0;
        }
        _lastTime = now;
        if (TakingIn && now >= _until) TakingIn = false;
        if (toggleTakeIn)
        {
            TakingIn = !TakingIn;
            _until = TakingIn ? now + DurationSeconds : 0;
            _ignoreTakeInUntilRelease = takeInHeld;
        }
        // Releasing Ctrl before the arrow must not turn the chord's last held
        // frame into a manual command that cancels the newly armed stand-in.
        if (!takeInHeld) _ignoreTakeInUntilRelease = false;
        var manualTakeIn = takeInHeld && !_ignoreTakeInUntilRelease;
        if (feedHeld || manualTakeIn)
        {
            TakingIn = false;
            return (feedHeld ? 1 : 0) - (manualTakeIn ? 1 : 0);
        }
        return TakingIn ? -1 : 0;
    }

    internal double RemainingSeconds(double now)
        => TakingIn && double.IsFinite(now) ? Math.Max(0, _until - now) : 0;

    internal void Reset()
    {
        TakingIn = false;
        _until = 0;
        _lastTime = -1;
        _ignoreTakeInUntilRelease = false;
    }
}
