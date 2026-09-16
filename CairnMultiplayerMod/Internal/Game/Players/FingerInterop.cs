using System;
using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Players;

/// <summary>
/// Native netplay omits fingers, and Cairn's non-humanoid rig requires name-based bone lookup.
/// </summary>
internal static unsafe class FingerInterop
{
    // Canonical order of finger bones. MUST be identical for capture and application
    // (the index = position in the payload) and hold Protocol.FingerBoneCount entries.
    private static readonly string[] FingerBoneNames = BuildFingerBoneNames();

    private static string[] BuildFingerBoneNames()
    {
        var list = new List<string>(Protocol.FingerBoneCount);
        foreach (var side in new[] { "l", "r" })
        {
            list.Add($"bn_{side}_Thumb_00");
            list.Add($"bn_{side}_Thumb_01");
            list.Add($"bn_{side}_Thumb_02");
            foreach (var finger in new[] { "Index", "Middle", "Ring", "Pinky" })
                for (int j = 0; j <= 3; j++)
                    list.Add($"bn_{side}_{finger}_{j:00}");
        }
        return list.ToArray();
    }

    private static Transform[] _localFingerBones;
    private static bool _localFingerResolved;
    private static int _lastLocalFingerResolveFrame;

    // Finger-bone cache per ghost (GameObject instance id).
    private static readonly Dictionary<int, Transform[]> _ghostFingerBones = new();

    public static bool TryCaptureLocalPose(out byte[] packed)
    {
        packed = null;
        var bones = EnsureLocalFingerBones();
        if (bones == null) return false;
        try
        {
            packed = EncodeFingerPose(bones);
            return packed != null;
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[FingerSync] Capture failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static bool TryApplyRemotePose(NetplayRemotePlayer ghost, byte[] packed)
    {
        if (ghost == null || ghost.Pointer == IntPtr.Zero) return false;
        if (packed == null || packed.Length != Protocol.HandPosePackedSize) return false;

        var bones = EnsureGhostFingerBones(ghost);
        if (bones == null) return false;

        try
        {
            using var ms = new MemoryStream(packed, writable: false);
            using var r = new BinaryReader(ms);
            for (int i = 0; i < FingerBoneNames.Length; i++)
            {
                QuaternionCodec.Unpack(r, out var x, out var y, out var z, out var w);
                var t = bones[i];
                if (t != null && t.Pointer != IntPtr.Zero)
                    t.localRotation = new Quaternion(x, y, z, w);
            }
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[FingerSync] Apply failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    internal static Animator TryGetHumanoidAnimator(GameObject root)
    {
        try { return root.GetComponentInChildren<Animator>(true); }
        catch (Exception exception)
        {
            ModLog.SuppressedException("fingers.resolve-humanoid-animator", exception);
            return null;
        }
    }

    public static void ResetCaches()
    {
        _localFingerBones = null;
        _localFingerResolved = false;
        _lastLocalFingerResolveFrame = 0;
        _ghostFingerBones.Clear();
    }

    private static byte[] EncodeFingerPose(Transform[] bones)
    {
        using var ms = new MemoryStream(Protocol.HandPosePackedSize);
        using (var w = new BinaryWriter(ms))
        {
            for (int i = 0; i < FingerBoneNames.Length; i++)
            {
                var t = bones[i];
                var q = (t != null && t.Pointer != IntPtr.Zero) ? t.localRotation : Quaternion.identity;
                QuaternionCodec.Pack(w, q.x, q.y, q.z, q.w);
            }
        }
        return ms.ToArray();
    }

    private static Transform[] EnsureLocalFingerBones()
    {
        if (_localFingerResolved) return _localFingerBones;

        var frame = Time.frameCount;
        if (_lastLocalFingerResolveFrame != 0 && frame - _lastLocalFingerResolveFrame < 60) return null;
        _lastLocalFingerResolveFrame = frame;

        var mc = LocalPlayerInterop.TryGetMCGameObject();
        if (mc == null) return null;

        _localFingerBones = ResolveFingerBonesByName(mc, out int resolved);
        if (_localFingerBones != null)
        {
            _localFingerResolved = true;
            ModLog.Debug($"[FingerSync] Local finger bones resolved by name ({resolved}/{FingerBoneNames.Length}).");
        }
        return _localFingerBones;
    }

    private static Transform[] EnsureGhostFingerBones(NetplayRemotePlayer ghost)
    {
        GameObject go;
        try { go = ghost.gameObject; }
        catch (Exception exception)
        {
            ModLog.SuppressedException("fingers.resolve-ghost-object", exception);
            return null;
        }
        if (go == null) return null;

        int id = go.GetInstanceID();
        if (_ghostFingerBones.TryGetValue(id, out var cached))
            return cached;

        var bones = ResolveFingerBonesByName(go, out int resolved);
        _ghostFingerBones[id] = bones;
        if (bones != null)
            ModLog.Debug($"[FingerSync] Ghost finger bones resolved by name ({resolved}/{FingerBoneNames.Length}).");
        return bones;
    }

    private static Transform[] ResolveFingerBonesByName(GameObject root, out int resolvedCount)
    {
        resolvedCount = 0;
        try
        {
            var map = new Dictionary<string, Transform>();
            var transforms = root.GetComponentsInChildren<Transform>(true);
            if (transforms == null) return null;
            for (int i = 0; i < transforms.Length; i++)
            {
                var t = transforms[i];
                if (t != null && !map.ContainsKey(t.name)) map[t.name] = t;
            }

            var bones = new Transform[FingerBoneNames.Length];
            for (int i = 0; i < FingerBoneNames.Length; i++)
            {
                if (map.TryGetValue(FingerBoneNames[i], out var b) && b != null)
                {
                    bones[i] = b;
                    resolvedCount++;
                }
            }
            return resolvedCount > 0 ? bones : null;
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[FingerSync] Bone resolution failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
