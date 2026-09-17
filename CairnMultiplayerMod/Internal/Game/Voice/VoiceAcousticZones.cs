using System;
using System.Collections.Generic;
using Il2CppTheGameBakers.Cairn;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.Internal.Game.Voice;

internal readonly struct VoiceAcousticProfile
{
    internal VoiceAcousticProfile(float maxDistance, float reverb)
    {
        MaxDistance = maxDistance;
        Reverb = reverb;
    }

    internal float MaxDistance { get; }
    internal float Reverb { get; }
    internal static VoiceAcousticProfile Outside => new(VoiceSpatialPolicy.NormalMaxDistance, 0);
}

/// <summary>Maps Cairn's existing room volumes to voice-chat acoustics on the Unity thread.</summary>
internal sealed class VoiceAcousticZones
{
    private sealed class Room
    {
        internal AudioRoomHandler Handler;
        internal Collider[] Volumes;
    }

    private readonly List<Room> _rooms = new();
    private bool _scanned;
    private float _nextScanAt;

    internal VoiceAcousticProfile Resolve(Vector3 speakerPosition)
    {
        EnsureRooms();
        for (var i = 0; i < _rooms.Count; i++)
        {
            var room = _rooms[i];
            var handler = room.Handler;
            if (handler == null || handler.Pointer == IntPtr.Zero || handler.roomType == AudioRoomHandler.AreaType.Outside
                || (!handler.isPlayerInside && !handler.listenerIsInside))
                continue;
            if (!Contains(room, speakerPosition)) continue;

            return new VoiceAcousticProfile(VoiceSpatialPolicy.ReverberantMaxDistance, ReverbFor(handler.roomType));
        }
        return VoiceAcousticProfile.Outside;
    }

    internal void Reset()
    {
        _rooms.Clear();
        _scanned = false;
        _nextScanAt = 0;
    }

    private void EnsureRooms()
    {
        if (_scanned && (_rooms.Count > 0 || Time.realtimeSinceStartup < _nextScanAt)) return;
        _scanned = true;
        _nextScanAt = Time.realtimeSinceStartup + 5;
        _rooms.Clear();
        try
        {
            var found = Object.FindObjectsOfType<AudioRoomHandler>(true);
            if (found == null) return;
            foreach (var room in found)
            {
                if (room == null || room.Pointer == IntPtr.Zero) continue;
                var colliders = new List<Collider>();
                var volumes = room.audioVolumes;
                if (volumes != null)
                {
                    for (var i = 0; i < volumes.Length; i++)
                    {
                        var collider = volumes[i] != null ? volumes[i].GetComponent<Collider>() : null;
                        if (collider != null) colliders.Add(collider);
                    }
                }
                _rooms.Add(new Room { Handler = room, Volumes = colliders.ToArray() });
            }
        }
        catch
        {
            // Missing room data simply keeps the normal outdoor voice profile.
            _rooms.Clear();
        }
    }

    private static bool Contains(Room room, Vector3 position)
    {
        try
        {
            var handler = room.Handler;
            if (handler.hasBounds && !handler.collectionBounds.Contains(position)) return false;
            if (room.Volumes.Length == 0)
                return handler.hasBounds && handler.collectionBounds.Contains(position);
            for (var i = 0; i < room.Volumes.Length; i++)
            {
                var collider = room.Volumes[i];
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                if (!collider.bounds.Contains(position)) continue;
                var closest = collider.ClosestPoint(position);
                if ((closest - position).sqrMagnitude < .0001f) return true;
            }
        }
        catch { }
        return false;
    }

    private static float ReverbFor(AudioRoomHandler.AreaType type)
        => type switch
        {
            AudioRoomHandler.AreaType.OpenCave => .20f,
            AudioRoomHandler.AreaType.Gym => .22f,
            AudioRoomHandler.AreaType.SmallRoom => .24f,
            AudioRoomHandler.AreaType.ShepherdIndoor => .24f,
            AudioRoomHandler.AreaType.SpikeWallShelter => .26f,
            AudioRoomHandler.AreaType.WaterfallCave => .34f,
            _ => .30f,
        };
}
