using System;
using System.Collections.Generic;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Voice;

/// <summary>Samples world geometry at a low rate; audio smoothing hides probe steps.</summary>
internal sealed class VoiceOcclusionProbe
{
    private sealed class Entry
    {
        internal double NextProbe;
        internal float Occlusion;
    }

    private readonly Dictionary<int, Entry> _entries = new();

    internal float Resolve(int playerId, Vector3 listenerRoot, Vector3 speakerRoot, double now)
    {
        if (!_entries.TryGetValue(playerId, out var entry))
        {
            entry = new Entry();
            _entries.Add(playerId, entry);
        }
        if (now < entry.NextProbe) return entry.Occlusion;
        entry.NextProbe = now + .16;
        entry.Occlusion = Measure(listenerRoot, speakerRoot);
        return entry.Occlusion;
    }

    internal void Remove(int playerId) => _entries.Remove(playerId);
    internal void Reset() => _entries.Clear();

    private static float Measure(Vector3 listenerRoot, Vector3 speakerRoot)
    {
        // Probe around head/chest height. Insetting both ends keeps the local and
        // remote character colliders from being mistaken for a rock wall.
        var listener = listenerRoot + Vector3.up * 1.45f;
        var speaker = speakerRoot + Vector3.up * .65f;
        var delta = speaker - listener;
        var distance = delta.magnitude;
        if (!float.IsFinite(distance) || distance <= 1.1f) return 0;
        var direction = delta / distance;
        listener += direction * .45f;
        speaker -= direction * .45f;

        var blocked = 0;
        if (Blocked(listener, speaker)) blocked++;
        if (Blocked(listener + Vector3.up * .3f, speaker + Vector3.up * .3f)) blocked++;
        if (Blocked(listener - Vector3.up * .3f, speaker - Vector3.up * .3f)) blocked++;
        return blocked / 3f;
    }

    private static bool Blocked(Vector3 from, Vector3 to)
    {
        var delta = to - from;
        var distance = delta.magnitude;
        return distance > .01f && Physics.Raycast(from, delta / distance, distance,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
    }
}
