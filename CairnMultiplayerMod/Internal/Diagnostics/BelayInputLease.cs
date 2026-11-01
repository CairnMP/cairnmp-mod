using System;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>Explicit, ordered intent; an expired session cannot resume payout.</summary>
internal sealed class BelayInputLease
{
    internal const double DurationSeconds = 3;
    internal Guid Session { get; }
    internal uint Sequence { get; private set; }
    internal bool Braked { get; private set; }
    private double _lastReceived, _expiresAt;

    internal BelayInputLease(Guid session, double now)
    {
        if (session == Guid.Empty || !double.IsFinite(now)) throw new ArgumentOutOfRangeException();
        Session = session;
        _lastReceived = now;
        _expiresAt = now + DurationSeconds;
    }

    internal bool IsExpired(double now) => !double.IsFinite(now) || now >= _expiresAt;

    internal bool TryAccept(Guid session, uint sequence, bool braked, double now)
    {
        if (session != Session || sequence <= Sequence || now < _lastReceived || IsExpired(now)) return false;
        Sequence = sequence;
        Braked = braked;
        _lastReceived = now;
        _expiresAt = now + DurationSeconds;
        return true;
    }
}
