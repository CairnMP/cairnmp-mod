using System;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>Retains inactive detours, never native rope ownership or recovery consent.</summary>
internal sealed class BelayPatchRetention
{
    private const double RenewalSeconds = 2;
    private const double MaximumSeconds = 120;
    private const double TeardownGraceSeconds = .25;
    private Guid _session;
    private uint _scene;
    private double _lastTime, _until, _deadline, _releaseAfter;
    private bool _allowed, _retaining, _releaseRequested;

    internal void BeginSession(Guid session, uint scene, double now)
    {
        _session = session;
        _scene = scene;
        _lastTime = now;
        _allowed = session != Guid.Empty && double.IsFinite(now);
        _retaining = _releaseRequested = false;
        _until = _deadline = _releaseAfter = 0;
    }

    internal bool TryRetain(Guid session, uint scene, double now)
    {
        if (!ReadClock(now) || !_allowed || session == Guid.Empty || session != _session || scene != _scene) return false;
        if ((_retaining && now >= _until) || (!_retaining && _releaseRequested && now >= _releaseAfter))
        { Release(); return false; }
        if (!_retaining)
        {
            _deadline = now + MaximumSeconds;
            _retaining = true;
        }
        _until = Math.Min(now + RenewalSeconds, _deadline);
        if (now < _until) return true;
        Release();
        return false;
    }

    internal void RequestRelease(double now)
    {
        if (!ReadClock(now) || _releaseRequested) return;
        _releaseRequested = true;
        _releaseAfter = _allowed ? now + TeardownGraceSeconds : now;
    }

    internal bool KeepInstalled(uint scene, double now)
    {
        if (!ReadClock(now) || !_allowed) return false;
        if (scene != _scene) { Release(); return false; }
        if (_retaining)
        {
            if (now < _until) return true;
            Release();
            return false;
        }
        return _releaseRequested && now < _releaseAfter;
    }

    // Explicit End cancels grace as well as renewal. A new native acquisition is
    // the only operation that can make the next session eligible again.
    internal void Release()
    {
        _allowed = _retaining = false;
        _until = _deadline = _releaseAfter = 0;
    }

    private bool ReadClock(double now)
    {
        if (!double.IsFinite(now) || now < _lastTime) { Release(); return false; }
        _lastTime = now;
        return true;
    }
}
