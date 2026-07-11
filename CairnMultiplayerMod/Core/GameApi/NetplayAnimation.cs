using System;
using CairnMultiplayer.Shared;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace CairnMultiplayerMod.Core;

public static unsafe partial class CairnGameApi
{
    private static MonoBehaviour _netplayManagerCached;
    private static int _lastNetplayManagerSearchFrame;
    private static GameObject _climberPrefabCached;
    private static int _lastPrefabSearchFrame;

    public static GameObject TryGetNetplayClimberPrefab()
    {
        if (_climberPrefabCached != null) return _climberPrefabCached;

        int frame = Time.frameCount;
        if (frame - _lastPrefabSearchFrame < 120) return null;
        _lastPrefabSearchFrame = frame;

        // Strategie 0 : lit directement le singleton genere par le jeu.
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

        // Strategie 1 : lit NetplayManager.NetplayClimberPrefab.
        var nm = FindMonoBehaviourByName("NetplayManager", ref _netplayManagerCached, ref _lastNetplayManagerSearchFrame);
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

        // Strategie 2 : parcourt tous les GameObjects pour trouver le prefab natif.
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

        // Strategie 3 : charge le prefab via Addressables natif du jeu.
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
    // NetplayRemotePlayer.SetFrame -- appelle le pipeline d'animation et de rendu natif
    // du jeu. Les shaders TGB ne se mettent a jour qu'avec cette methode.
    // ------------------------------------------------------------------

    private static bool _directPlayerBoneFallbackLogged;
    private static bool _directClimbotBoneFallbackLogged;
    private static bool _directBoneFallbackFailureLogged;
    private static bool _directBoneFallbackMismatchLogged;
    private static bool _nativePlayerSetFrameFailureLogged;
    private static bool _nativeClimbotSetFrameFailureLogged;

    /// <summary>
    /// Applique une frame native recue depuis le reseau sur le joueur distant.
    /// </summary>
    public static bool CallNetplaySetFrame(NetplayRemotePlayer player, int id, string playerName, NetFrameData frameData)
    {
        if (player == null || !frameData.IsValid) return false;

        try
        {
            var frame = ToNativePlayerNetFrame(frameData);
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
    /// Affiche ou masque la plaque de nom (champ natif `nameMesh`, un TextMeshPro)
    /// au-dessus d'un fantome distant. Sert au toggle N (mode photo). Bascule le
    /// GameObject du mesh — idempotent (ne touche que sur changement reel d'etat).
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
            // Plaque de nom indisponible (fantome pas encore initialise) -> ignore.
        }
    }

    /// <summary>
    /// Applique une frame native recue depuis le reseau sur le climbot distant.
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

            var frame = ToNativeClimbotNetFrame(frameData);
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
    /// Lit LiveGhostAnchors.relatives depuis un composant NetplayRemotePlayer.
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

            // Invariant attendu : la frame native = [racine monde] + [os relatifs locaux],
            // donc positionCount == boneCount + 1. On ne DEVINE plus l'offset : l'ancienne
            // heuristique (offset 0 sur mismatch) ecrivait la racine MONDE dans un slot d'os
            // LOCAL et decalait tous les os d'un cran -> membres qui clippent. En cas de
            // mismatch (rig different, cap a 128 os...), on saute la frame plutot que corrompre.
            // La capture plafonne les os relatifs a 128 ; on aligne le cote apply pour
            // qu'un rig hypothetique >128 os degrade aux 128 premiers au lieu de figer.
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
            // Racine monde (slot 0) appliquee comme position/eulerAngles monde.
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
                if (!IsFiniteVector(localPos)) continue; // rejette NaN/Inf -> pas de membre projete a l'infini

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
