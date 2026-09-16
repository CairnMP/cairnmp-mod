using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace CairnMultiplayerMod.Internal.Game.Players;

internal static class NetplayAnimationInterop
{
    private static GameObject _climberPrefabCached;
    private static int _lastPrefabSearchFrame;

    internal static void ResetCaches()
    {
        _climberPrefabCached = null;
        _lastPrefabSearchFrame = 0;
    }

    public static GameObject TryGetNetplayClimberPrefab()
    {
        if (_climberPrefabCached != null) return _climberPrefabCached;

        int frame = Time.frameCount;
        if (frame - _lastPrefabSearchFrame < 120) return null;
        _lastPrefabSearchFrame = frame;

        // Cairn owns and loads this prefab through its NetplayManager singleton.
        try
        {
            var manager = MoSingleton<NetplayManager>.Instance;
            var prefab = manager?.NetplayClimberPrefab;
            if (prefab != null)
            {
                _climberPrefabCached = prefab;
                ModLog.Debug("[NetplayAnim] NetplayClimberPrefab found via NetplayManager singleton");
                return prefab;
            }
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[NetplayAnim] NetplayManager singleton read failed: {ex.Message}");
        }

        // During early loading the singleton may not exist yet. Use the same addressable
        // key as Cairn, then cache the result for the rest of the scene.
        try
        {
            var handle = Addressables.LoadAssetAsync<GameObject>(NetplayManager.ADDRESSABLE_NAME);
            handle.WaitForCompletion();
            var prefab = handle.Result;
            if (prefab != null)
            {
                _climberPrefabCached = prefab;
                ModLog.Debug("[NetplayAnim] NetplayClimberPrefab loaded via Addressables");
                return prefab;
            }
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[NetplayAnim] Addressables prefab load failed: {ex.Message}");
        }

        return null;
    }

    // NetplayRemotePlayer.SetFrame -- calls the game's native animation and render
    // pipeline. The TGB shaders only update through this method.

    private static bool _directPlayerBoneFallbackLogged;
    private static bool _directClimbotBoneFallbackLogged;
    private static bool _directBoneFallbackFailureLogged;
    private static bool _directBoneFallbackMismatchLogged;
    private static bool _nativePlayerSetFrameFailureLogged;
    private static bool _nativeClimbotSetFrameFailureLogged;

    public static bool CallNetplaySetFrame(NetplayRemotePlayer player, int id, string playerName, NetFrameData frameData)
    {
        if (player == null || !frameData.IsValid) return false;

        try
        {
            var frame = PawnCaptureInterop.ToNativePlayerNetFrame(frameData);
            player.SetFrame(id, playerName ?? "", frame);
            return true;
        }
        catch (Exception ex)
        {
            LogNativeSetFrameFailure("player", frameData, ex, ref _nativePlayerSetFrameFailureLogged);
            return ApplyNetFrameToGhostBones(player, frameData, "player", ref _directPlayerBoneFallbackLogged);
        }
    }

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
        catch (Exception exception)
        {
            ModLog.SuppressedException("netplay.set-name-label-visibility", exception);
        }
    }

    public static bool CallNetplayClimbotSetFrame(NetplayRemotePlayer player, int id, NetFrameData frameData)
    {
        if (player == null || !frameData.IsValid) return false;

        try
        {
            var climbot = player.Climbot;
            if (climbot == null) return false;
            if (!climbot.gameObject.activeSelf)
                climbot.gameObject.SetActive(true);

            var frame = PawnCaptureInterop.ToNativeClimbotNetFrame(frameData);
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
            catch (Exception exception)
            {
                ModLog.SuppressedException("netplay.apply-frame-to-climbot", exception);
                return false;
            }
        }
    }

    private static Il2CppReferenceArray<Transform> TryGetGhostBones(MonoBehaviour component)
    {
        if (component == null) return null;

        try
        {
            if (component is NetplayRemotePlayer player)
                return player.anchors?.relatives;
            if (component is NetplayRemoteClimbot climbot)
                return climbot.anchors?.relatives;
        }
        catch (Exception ex)
        {
            ModLog.Error($"[NetplayAnim] Ghost anchors read failed: {ex.Message}");
        }

        return null;
    }

    private static bool ApplyNetFrameToGhostBones(MonoBehaviour component, NetFrameData frameData, string label, ref bool logged)
    {
        if (component == null || !frameData.IsValid) return false;
        if (frameData.Positions == null || frameData.Positions.Length < 3) return false;

        try
        {
            var relatives = TryGetGhostBones(component);
            if (relatives == null) return false;
            var boneCount = relatives.Length;

            int positionCount = frameData.Positions.Length / 3;
            int eulerCount = frameData.Eulers == null ? 0 : frameData.Eulers.Length / 3;

            // The native frame is [world root] + [local relative bones], so the native
            // SetFrame requires positionCount == boneCount + 1. Guessing an offset on mismatch
            // shifts every bone and clips limbs; skip a genuinely different rig instead.
            // No 128-bone ceiling here: the capture side sends every bone or nothing, so
            // clamping would only manufacture mismatches on rigs the native path handles.
            int applyCount = boneCount;
            if (positionCount != applyCount + 1)
            {
                if (!_directBoneFallbackMismatchLogged)
                {
                    _directBoneFallbackMismatchLogged = true;
                    ModLog.Warning($"[NetplayAnim] Ghost bone fallback skipped for {label}: positionCount={positionCount} != applyCount+1={applyCount + 1} (boneCount={boneCount})");
                }
                return false;
            }

            const int frameOffset = 1;
            ApplyNetFrameRoot(component.transform, frameData, eulerCount);

            for (int i = 0; i < applyCount; i++)
            {
                var t = relatives[i];
                if (t == null) continue;

                int frameIndex = i + frameOffset;
                int pi = frameIndex * 3;
                var localPos = new Vector3(
                    frameData.Positions[pi],
                    frameData.Positions[pi + 1],
                    frameData.Positions[pi + 2]);
                if (!IsFiniteVector(localPos)) continue; // reject NaN/Inf -> no limb flung to infinity

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
                ModLog.Debug($"[NetplayAnim] Direct ghost bone fallback active for {label} bones={applyCount} frameVectors={positionCount}");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (!_directBoneFallbackFailureLogged)
            {
                _directBoneFallbackFailureLogged = true;
                ModLog.Warning($"[NetplayAnim] Direct ghost bone fallback failed: {ex.Message}");
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
        ModLog.Warning($"[NetplayAnim] Native {label} SetFrame failed: {ex.Message} flags=0x{frameData.Flags:X2} positions={positionCount} eulers={eulerCount}");
    }
}
