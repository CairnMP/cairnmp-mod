using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>Identity-based discovery, including removals and equal-count replacements.</summary>
internal sealed class PitonIdentityTracker
{
    private readonly Dictionary<IntPtr, uint> _local = new();
    private readonly HashSet<IntPtr> _remote = new();
    private uint _nextId = 1;

    internal bool TryFindNew(IEnumerable<IntPtr> current, out IntPtr pointer)
    {
        foreach (var candidate in current)
            if (candidate != IntPtr.Zero && !_local.ContainsKey(candidate) && !_remote.Contains(candidate))
            { pointer = candidate; return true; }
        pointer = IntPtr.Zero;
        return false;
    }
    internal uint Announce(IntPtr pointer)
    {
        if (!_local.TryGetValue(pointer, out var id)) _local.Add(pointer, id = _nextId++);
        return id;
    }
    internal void MarkRemote(IntPtr pointer) { if (pointer != IntPtr.Zero) _remote.Add(pointer); }
    internal void ForgetRemote(IntPtr pointer) => _remote.Remove(pointer);
    internal bool TryRemoveMissing(ISet<IntPtr> current, out uint id)
    {
        foreach (var entry in _local)
            if (!current.Contains(entry.Key))
            {
                id = entry.Value;
                _local.Remove(entry.Key);
                return true;
            }
        id = 0;
        return false;
    }
    internal void Clear() { _local.Clear(); _remote.Clear(); }
}
