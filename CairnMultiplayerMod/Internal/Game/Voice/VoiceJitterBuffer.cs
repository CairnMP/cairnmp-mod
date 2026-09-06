using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.Internal.Game.Voice;

/// <summary>Reorders a small window of 20 ms packets. A new talk burst starts after a long gap.</summary>
internal sealed class VoiceJitterBuffer
{
    private readonly Dictionary<uint, byte[]> _packets = new();
    private uint _next;
    private double _due, _lastArrival;
    private bool _started;
    internal bool Push(uint sequence, byte[] data, double now)
    {
        if (!_started || now - _lastArrival > .3)
        {
            _packets.Clear();
            _next = sequence;
            _due = now + .06;
            _started = true;
        }
        var ahead = unchecked((int)(sequence - _next));
        if (ahead < 0 || ahead > 15 || _packets.ContainsKey(sequence)) return false;
        _lastArrival = now;
        _packets.Add(sequence, data);
        return true;
    }
    internal bool TryPop(double now, out byte[] data)
    {
        data = null;
        if (!_started || now < _due) return false;
        if (now - _due > .2 || now - _lastArrival > .1) { Clear(); return false; }
        _packets.Remove(_next++, out data); // null means a lost packet: decoder conceals it.
        _due += .02;
        return true;
    }
    internal void Clear() { _started = false; _packets.Clear(); }
}
