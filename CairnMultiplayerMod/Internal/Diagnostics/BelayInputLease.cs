using System;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>Explicit, ordered intent; an expired session cannot resume payout.</summary>
internal sealed class BelayInputLease
{
    internal const double DurationSeconds = 3;
    internal const double MotionDurationSeconds = .4;
    internal Guid Session { get; }
    internal uint Sequence { get; private set; }
    internal bool Braked { get; private set; }
    private double _lastReceived, _expiresAt;
    private double _motionExpiresAt;
    private int _motion;

    internal BelayInputLease(Guid session, double now, bool braked = false)
    {
        if (session == Guid.Empty || !double.IsFinite(now)) throw new ArgumentOutOfRangeException();
        Session = session;
        Braked = braked;
        _lastReceived = now;
        _expiresAt = now + DurationSeconds;
    }

    internal bool IsExpired(double now) => !double.IsFinite(now) || now >= _expiresAt;

    internal bool TryAccept(Guid session, uint sequence, bool braked, double now)
        => TryAccept(session, sequence, braked, 0, now);

    internal bool TryAccept(Guid session, uint sequence, bool braked, int motion, double now)
    {
        if (motion < -1 || motion > 1 || session != Session || sequence <= Sequence
            || now < _lastReceived || IsExpired(now)) return false;
        Sequence = sequence;
        Braked = braked;
        _lastReceived = now;
        _expiresAt = now + DurationSeconds;
        _motion = motion;
        _motionExpiresAt = now + MotionDurationSeconds;
        return true;
    }

    internal int MotionAt(double now)
        => IsExpired(now) || now < _lastReceived || now >= _motionExpiresAt ? 0 : _motion;

    internal void CancelMotion() => _motion = 0;
}
