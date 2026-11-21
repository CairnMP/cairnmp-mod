using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>Refresh an expired local pose before judging it, at most once per frame.</summary>
internal sealed class PlayerInteractionPoseCache
{
    private WorldPosition _position;
    private double _capturedAt;
    private int _lastRefreshFrame = -1;

    internal void Remember(NetFrameData frame, double now)
    {
        if (!PlayerInteractionPose.TryRead(frame, now, now, out var position)) return;
        _position = position; _capturedAt = now;
    }

    internal bool TryGet(Func<double> clock, int frame, Action refresh, out WorldPosition position)
    {
        position = _position;
        if (PlayerInteractionPose.IsFresh(_capturedAt, clock())) return true;
        if (_lastRefreshFrame == frame) return false;
        _lastRefreshFrame = frame;
        refresh();
        position = _position;
        // Capturing may itself take time. Judge freshness after the capture,
        // using its actual timestamp rather than the time before that work.
        return PlayerInteractionPose.IsFresh(_capturedAt, clock());
    }

    internal void Reset()
    { _position = default; _capturedAt = 0; _lastRefreshFrame = -1; }
}
