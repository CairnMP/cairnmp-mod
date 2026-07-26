using System;
using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnMultiplayerMod.Features.Players.Avatar;

/// <summary>
/// Exact sync of finger bones between players.
///
/// The game's netplay only captures ~16 coarse bones, without the fingers -> the ghosts' hands
/// stay frozen. Here we capture the finger bones of the Aava skeleton and apply them as
/// localRotation onto the same ghost bones (after the native pipeline has posed the body).
///
/// Resolution BY NAME (not via the humanoid Animator: Cairn's rig isn't humanoid, isHuman=False).
/// The names are identical on the local and ghost sides (same Aava skeleton):
/// bn_{l|r}_{Thumb 00-02 | Index/Middle/Ring/Pinky 00-03}. We compress with smallest-three.
/// </summary>
internal static unsafe class FingerApi
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

    /// <summary>Captures the local finger pose (compressed localRotations). False if unavailable.</summary>
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
            Mod.Log.Warning($"[FingerSync] Capture failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Applies a received finger pose onto the ghost's bones.</summary>
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
            Mod.Log.Warning($"[FingerSync] Apply failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>First Animator in the hierarchy (used by other cosmetic modules).</summary>
    internal static Animator TryGetHumanoidAnimator(GameObject root)
    {
        try { return root.GetComponentInChildren<Animator>(true); }
        catch { return null; }
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

        var mc = LocalPlayerApi.TryGetMCGameObject();
        if (mc == null) return null;

        _localFingerBones = ResolveFingerBonesByName(mc, out int resolved);
        if (_localFingerBones != null)
        {
            _localFingerResolved = true;
            Mod.LogDebug($"[FingerSync] Local finger bones resolved by name ({resolved}/{FingerBoneNames.Length}).");
        }
        return _localFingerBones;
    }

    private static Transform[] EnsureGhostFingerBones(NetplayRemotePlayer ghost)
    {
        GameObject go;
        try { go = ghost.gameObject; }
        catch { return null; }
        if (go == null) return null;

        int id = go.GetInstanceID();
        if (_ghostFingerBones.TryGetValue(id, out var cached))
            return cached;

        var bones = ResolveFingerBonesByName(go, out int resolved);
        _ghostFingerBones[id] = bones;
        if (bones != null)
            Mod.LogDebug($"[FingerSync] Ghost finger bones resolved by name ({resolved}/{FingerBoneNames.Length}).");
        return bones;
    }

    /// <summary>
    /// Resolves the finger bones by NAME in the hierarchy. Returns an array of the canonical
    /// size (null entries where a bone is missing), or null if no bone was found.
    /// </summary>
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
            Mod.Log.Warning($"[FingerSync] Bone resolution failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
