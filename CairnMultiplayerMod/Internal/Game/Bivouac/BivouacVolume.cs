using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTheGameBakers.Cairn;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Bivouac;

/// <summary>
/// One camp's reach on the mountain, shared by the rest gate and the recall.
///
/// The colliders are the volume. A query that cannot see them falls back to the camp's
/// own interaction test, and only for that camp. The marker remembered here is the
/// provider's position on the mountain, which is what a recall names. The interior pose
/// is never stored.
/// </summary>
internal static class BivouacVolume
{
    /// <summary>A named point this close to the camp marker is that camp. The sender writes
    /// the marker itself, so this only absorbs the float trip.</summary>
    private const float MarkerSlackMeters = 2f;

    private static bool _hasMarker;
    private static Vector3 _marker;

    internal static void Remember(Vector3 marker)
    {
        if (!Finite(marker)) return;
        _marker = marker;
        _hasMarker = true;
    }

    internal static bool TryRemembered(out Vector3 marker)
    {
        marker = _marker;
        return _hasMarker && Finite(marker);
    }

    internal static void Clear()
    {
        _hasMarker = false;
        _marker = default;
    }

    /// <summary>The camp whose volume contains <paramref name="pose"/>, and its mountain marker.</summary>
    internal static bool TryApproach(Vector3 pose, out Vector3 marker)
    {
        marker = default;
        if (!Finite(pose)) return false;
        try
        {
            var camps = UnityEngine.Object.FindObjectsOfType<BivouacInteractionProvider>();
            if (camps == null) return false;
            foreach (var camp in camps)
            {
                if (camp == null || !Contains(camp, pose)) continue;
                marker = camp.transform.position;
                return Finite(marker);
            }

            return false;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("bivouac.approach", exception);
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="pose"/> is in the volume of the camp whose marker is
    /// <paramref name="claimed"/>. A point that only happens to be near some other camp
    /// does not count.
    /// </summary>
    internal static bool Same(Vector3 claimed, Vector3 pose)
    {
        if (!Finite(claimed) || !Finite(pose)) return false;
        try
        {
            var camp = BivouacInteractionProvider.GetClosestBivouac(claimed, out _);
            if (camp == null || !Contains(camp, pose)) return false;
            var marker = camp.transform.position;
            var dx = marker.x - claimed.x;
            var dy = marker.y - claimed.y;
            var dz = marker.z - claimed.z;
            return dx * dx + dy * dy + dz * dz <= MarkerSlackMeters * MarkerSlackMeters;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("bivouac.same-camp", exception);
            return false;
        }
    }

    internal static bool Contains(BivouacInteractionProvider camp, Vector3 position)
    {
        if (camp == null || !Finite(position)) return false;
        if (InsideOwnVolume(camp, position, out var inside)) return inside;
        var closest = BivouacInteractionProvider.GetClosestBivouac(position, out _);
        if (closest != null && closest.Pointer != camp.Pointer) return false;
        return camp.CanBeInteractedFromPosition(position);
    }

    private static bool Finite(Vector3 position)
        => float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z);

    /// <summary>
    /// The camp's own colliders. False when the volume is missing, so the caller can fall
    /// back. True, with <paramref name="inside"/>, when the colliders answered.
    /// </summary>
    private static bool InsideOwnVolume(BivouacInteractionProvider camp, Vector3 position, out bool inside)
    {
        inside = false;
        Il2CppReferenceArray<Collider> volume = camp.colliders;
        if (volume == null || volume.Length == 0) return false;

        var usable = false;
        for (var i = 0; i < volume.Length; i++)
        {
            var collider = volume[i];
            if (collider == null || !collider.enabled) continue;
            var size = collider.bounds.size;
            if (size.sqrMagnitude <= 0.0001f) continue;
            usable = true;
            if (collider.bounds.Contains(position))
            {
                inside = true;
                return true;
            }
        }

        return usable;
    }
}
