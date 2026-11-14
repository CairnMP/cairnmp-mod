using System;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>Retains the last drawn rope so a new packet can blend without reading the renderer.</summary>
internal sealed class BelayRopeVisualPlayback
{
    private WorldPosition[] _from = Array.Empty<WorldPosition>();
    private WorldPosition[] _target = Array.Empty<WorldPosition>();
    private WorldPosition[] _current = Array.Empty<WorldPosition>();

    internal Guid Session { get; private set; }
    internal uint Revision { get; private set; }
    internal double UpdatedAt { get; private set; }
    internal int Count => _current.Length;
    internal ReadOnlySpan<WorldPosition> Current => _current;

    internal bool SetTarget(Guid session, uint revision, ReadOnlySpan<WorldPosition> points, double now)
    {
        if (points.Length < 2 || points.Length > 192 || !double.IsFinite(now)) return false;
        foreach (var point in points)
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return false;

        var blend = Session == session && Revision == revision && Count == points.Length;
        if (Count != points.Length)
        {
            _from = new WorldPosition[points.Length];
            _target = new WorldPosition[points.Length];
            _current = new WorldPosition[points.Length];
        }
        for (var i = 0; i < points.Length; i++)
        {
            _from[i] = blend ? _current[i] : points[i];
            _target[i] = points[i];
            if (!blend) _current[i] = points[i];
        }
        Session = session;
        Revision = revision;
        UpdatedAt = now;
        return true;
    }

    internal ReadOnlySpan<WorldPosition> Sample(double now)
    {
        if (!double.IsFinite(now)) return _current;
        var t = Math.Clamp((float)((now - UpdatedAt) / .1), 0, 1);
        for (var i = 0; i < Count; i++)
        {
            if (t <= 0) _current[i] = _from[i];
            else if (t >= 1) _current[i] = _target[i];
            else
            {
                var from = _from[i];
                var target = _target[i];
                _current[i] = new WorldPosition(
                    from.X + (target.X - from.X) * t,
                    from.Y + (target.Y - from.Y) * t,
                    from.Z + (target.Z - from.Z) * t);
            }
        }
        return _current;
    }

    internal void Clear()
    {
        _from = _target = _current = Array.Empty<WorldPosition>();
        Session = Guid.Empty;
        Revision = 0;
        UpdatedAt = 0;
    }
}
