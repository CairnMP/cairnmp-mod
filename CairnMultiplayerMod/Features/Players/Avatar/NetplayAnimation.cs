using System;
using CairnMultiplayer.Shared;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace CairnMultiplayerMod.Features.Players.Avatar;

internal static unsafe class NetplayAnimationApi
{
    private static MonoBehaviour _netplayManagerCached;
    private static int _lastNetplayManagerSearchFrame;
    private static GameObject _climberPrefabCached;
    private static int _lastPrefabSearchFrame;

    /// <summary>Forgets the scene-bound NetplayManager reference and climber prefab
    /// (called on scene reload).</summary>
    internal static void ResetCaches()
    {
        _netplayManagerCached = null;
        _lastNetplayManagerSearchFrame = 0;
        _climberPrefabCached = null;
        _lastPrefabSearchFrame = 0;
    }

    public static GameObject TryGetNetplayClimberPrefab()
    {
        if (_climberPrefabCached != null) return _climberPrefabCached;

        int frame = Time.frameCount;
        if (frame - _lastPrefabSearchFrame < 120) return null;
        _lastPrefabSearchFrame = frame;

        // Strategy 0: read the game-generated singleton directly.
        try
        {
            var manager = MoSingleton<NetplayManager>.Instance;
            var prefab = manager?.NetplayClimberPrefab;
            if (prefab != null)
            {
                _climberPrefabCached = prefab;
                Mod.LogDebug("[CairnGameApi] NetplayClimberPrefab found via NetplayManager singleton");
                return prefab;
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnGameApi] NetplayManager singleton read failed: {ex.Message}");
        }

        // Strategy 1: read NetplayManager.NetplayClimberPrefab.
        var nm = GameInterop.FindMonoBehaviourByName("NetplayManager", ref _netplayManagerCached, ref _lastNetplayManagerSearchFrame);
        if (nm != null)
        {
            try
            {
                var klass = IL2CPP.il2cpp_object_get_class(nm.Pointer);
                var field = IL2CPP.GetIl2CppField(klass, "<NetplayClimberPrefab>k__BackingField");
                if (field != IntPtr.Zero)
                {
                    int off = (int)IL2CPP.il2cpp_field_get_offset(field);
                    IntPtr ptr = *(IntPtr*)((byte*)nm.Pointer + off);
                    if (ptr != IntPtr.Zero)
                    {
                        _climberPrefabCached = new GameObject(ptr);
                        Mod.LogDebug("[CairnGameApi] NetplayClimberPrefab found via NetplayManager");
                        return _climberPrefabCached;
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warning($"[CairnGameApi] NetplayClimberPrefab read failed: {ex.Message}");
            }
        }

        // Strategy 2: scan all GameObjects to find the native prefab.
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    var go = all[i].TryCast<GameObject>();
                    if (go == null) continue;
                    var name = go.name;
                    if (name == "MC_Netplay_Player" || name.StartsWith("MC_Netplay_Player"))
                    {
                        _climberPrefabCached = go;
                        Mod.LogDebug($"[CairnGameApi] NetplayClimberPrefab found by name scan: '{name}'");
                        return go;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnGameApi] GameObject name scan failed: {ex.Message}");
        }

        // Strategy 3: load the prefab via the game's native Addressables.
        try
        {
            var handle = Addressables.LoadAssetAsync<GameObject>(NetplayManager.ADDRESSABLE_NAME);
            handle.WaitForCompletion();
            var prefab = handle.Result;
            if (prefab != null)
            {
                _climberPrefabCached = prefab;
                Mod.LogDebug("[CairnGameApi] NetplayClimberPrefab loaded via Addressables");
                return prefab;
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnGameApi] Addressables prefab load failed: {ex.Message}");
        }

        return null;
    }

    // ------------------------------------------------------------------
    // NetplayRemotePlayer.SetFrame -- calls the game's native animation and render
    // pipeline. The TGB shaders only update through this method.
    // ------------------------------------------------------------------

    private static bool _directPlayerBoneFallbackLogged;
    private static bool _directClimbotBoneFallbackLogged;
    private static bool _directBoneFallbackFailureLogged;
    private static bool _directBoneFallbackMismatchLogged;
    private static bool _nativePlayerSetFrameFailureLogged;
    private static bool _nativeClimbotSetFrameFailureLogged;

    /// <summary>
    /// Applies a native frame received from the network onto the remote player.
    /// </summary>
    public static bool CallNetplaySetFrame(NetplayRemotePlayer player, int id, string playerName, NetFrameData frameData)
    {
        if (player == null || !frameData.IsValid) return false;

        try
        {
            var frame = PawnCaptureApi.ToNativePlayerNetFrame(frameData);
            player.SetFrame(id, playerName ?? "", frame);
            return true;
        }
        catch (Exception ex)
        {
            LogNativeSetFrameFailure("player", frameData, ex, ref _nativePlayerSetFrameFailureLogged);
            return ApplyNetFrameToGhostBones(player, frameData, "player", ref _directPlayerBoneFallbackLogged);
        }
    }

    /// <summary>
    /// Shows or hides the name plate (native `nameMesh` field, a TextMeshPro)
    /// above a remote ghost. Used by the N toggle (photo mode). Toggles the mesh
    /// GameObject — idempotent (only acts on an actual state change).
    /// </summary>
    public static void SetGhostNameVisible(NetplayRemotePlayer player, bool visible)
    {
        if (player == null) return;
        try
        {
            var mesh = player.nameMesh;
            if (mesh == null) return;
            var go = mesh.gameObject;
            if (go != null && go.activeSelf != visible)
                go.SetActive(visible);
        }
        catch
        {
            // Name plate unavailable (ghost not yet initialized) -> ignore.
        }
    }

    /// <summary>
    /// Applies a native frame received from the network onto the remote climbot.
    /// </summary>
    public static bool CallNetplayClimbotSetFrame(NetplayRemotePlayer player, int id, NetFrameData frameData)
    {
        if (player == null || !frameData.IsValid) return false;

        try
        {
            var climbot = player.Climbot;
            if (climbot == null) return false;
            if (!climbot.gameObject.activeSelf)
                climbot.gameObject.SetActive(true);

            var frame = PawnCaptureApi.ToNativeClimbotNetFrame(frameData);
            climbot.SetFrame(id, frame);
            return true;
        }
        catch (Exception ex)
        {
            LogNativeSetFrameFailure("climbot", frameData, ex, ref _nativeClimbotSetFrameFailureLogged);
            try
            {
                var climbot = player.Climbot;
                return ApplyNetFrameToGhostBones(climbot, frameData, "climbot", ref _directClimbotBoneFallbackLogged);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Reads LiveGhostAnchors.relatives from a NetplayRemotePlayer component.
    /// </summary>
    public static bool TryReadGhostBoneArray(MonoBehaviour nrpComponent, out IntPtr relativesArrayPtr, out int boneCount)
    {
        relativesArrayPtr = IntPtr.Zero;
        boneCount = 0;
        if (nrpComponent == null) return false;

        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(nrpComponent.Pointer);
            var field = IL2CPP.GetIl2CppField(klass, "anchors");
            if (field == IntPtr.Zero) return false;

            int offset = (int)IL2CPP.il2cpp_field_get_offset(field);
            byte* basePtr = (byte*)nrpComponent.Pointer + offset;
            IntPtr relativesPtr = *(IntPtr*)(basePtr + IntPtr.Size);

            if (relativesPtr == IntPtr.Zero) return false;

            boneCount = *(int*)((byte*)relativesPtr + 3 * IntPtr.Size);
            relativesArrayPtr = relativesPtr;
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[CairnGameApi] TryReadGhostBoneArray failed: {ex.Message}");
            return false;
        }
    }

    private static bool ApplyNetFrameToGhostBones(MonoBehaviour component, NetFrameData frameData, string label, ref bool logged)
    {
        if (component == null || !frameData.IsValid) return false;
        if (frameData.Positions == null || frameData.Positions.Length < 3) return false;

        try
        {
            if (!TryReadGhostBoneArray(component, out var relativesArrayPtr, out int boneCount))
                return false;

            int positionCount = frameData.Positions.Length / 3;
            int eulerCount = frameData.Eulers == null ? 0 : frameData.Eulers.Length / 3;

            // Expected invariant: the native frame = [world root] + [local relative bones],
            // so positionCount == boneCount + 1. We no longer GUESS the offset: the old
            // heuristic (offset 0 on mismatch) wrote the WORLD root into a LOCAL bone slot
            // and shifted every bone by one -> clipping limbs. On a mismatch (different rig,
            // cap at 128 bones...), we skip the frame rather than corrupt it.
            // Capture caps the relative bones at 128; we align the apply side so a
            // hypothetical rig >128 bones degrades to the first 128 instead of freezing.
            int applyCount = Math.Min(boneCount, 128);
            if (positionCount != applyCount + 1)
            {
                if (!_directBoneFallbackMismatchLogged)
                {
                    _directBoneFallbackMismatchLogged = true;
                    Mod.Log.Warning($"[CairnGameApi] Ghost bone fallback skipped for {label}: positionCount={positionCount} != applyCount+1={applyCount + 1} (boneCount={boneCount})");
                }
                return false;
            }

            const int frameOffset = 1;
            // World root (slot 0) applied as world position/eulerAngles.
            ApplyNetFrameRoot(component.transform, frameData, eulerCount);

            int headerSize = 4 * IntPtr.Size;

            for (int i = 0; i < applyCount; i++)
            {
                IntPtr transformPtr = *(IntPtr*)((byte*)relativesArrayPtr + headerSize + i * IntPtr.Size);
                if (transformPtr == IntPtr.Zero) continue;

                int frameIndex = i + frameOffset;
                int pi = frameIndex * 3;
                var localPos = new Vector3(
                    frameData.Positions[pi],
                    frameData.Positions[pi + 1],
                    frameData.Positions[pi + 2]);
                if (!IsFiniteVector(localPos)) continue; // reject NaN/Inf -> no limb flung to infinity

                var t = new Transform(transformPtr);
                t.localPosition = localPos;

                if (frameIndex < eulerCount)
                {
                    int ri = frameIndex * 3;
                    var localEuler = new Vector3(
                        frameData.Eulers[ri],
                        frameData.Eulers[ri + 1],
                        frameData.Eulers[ri + 2]);
                    if (IsFiniteVector(localEuler))
                        t.localEulerAngles = localEuler;
                }
            }

            if (!logged)
            {
                logged = true;
                Mod.LogDebug($"[CairnGameApi] Direct ghost bone fallback active for {label} bones={applyCount} frameVectors={positionCount}");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (!_directBoneFallbackFailureLogged)
            {
                _directBoneFallbackFailureLogged = true;
                Mod.Log.Warning($"[CairnGameApi] Direct ghost bone fallback failed: {ex.Message}");
            }
            return false;
        }
    }

    private static void ApplyNetFrameRoot(Transform root, NetFrameData frameData, int eulerCount)
    {
        if (root == null || frameData.Positions == null || frameData.Positions.Length < 3)
            return;

        var pos = new Vector3(
            frameData.Positions[0],
            frameData.Positions[1],
            frameData.Positions[2]);
        if (!IsFiniteVector(pos)) return;
        root.position = pos;

        if (eulerCount <= 0 || frameData.Eulers == null || frameData.Eulers.Length < 3)
            return;

        var euler = new Vector3(
            frameData.Eulers[0],
            frameData.Eulers[1],
            frameData.Eulers[2]);
        if (IsFiniteVector(euler))
            root.eulerAngles = euler;
    }

    private static bool IsFiniteVector(Vector3 v)
        => !(float.IsNaN(v.x) || float.IsInfinity(v.x)
          || float.IsNaN(v.y) || float.IsInfinity(v.y)
          || float.IsNaN(v.z) || float.IsInfinity(v.z));

    private static void LogNativeSetFrameFailure(string label, NetFrameData frameData, Exception ex, ref bool logged)
    {
        if (logged) return;
        logged = true;
        int positionCount = frameData.Positions == null ? 0 : frameData.Positions.Length / 3;
        int eulerCount = frameData.Eulers == null ? 0 : frameData.Eulers.Length / 3;
        Mod.Log.Warning($"[CairnGameApi] Native {label} SetFrame failed: {ex.Message} flags=0x{frameData.Flags:X2} positions={positionCount} eulers={eulerCount}");
    }
}
