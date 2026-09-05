using CairnMultiplayerMod.Internal.Diagnostics;
using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Players;

/// <summary>Reads the local player's MC (main character) GameObject, world pose and yaw
/// from the native PawnManager. Shared foundation used by teleport, avatar sync, roping
/// and ping.</summary>
internal static unsafe class LocalPlayerInterop
{
    private static MonoBehaviour _pawnManagerCached;
    private static int _mcGameObjectOffset = -1;
    private static int _lastPawnManagerSearchFrame;
    private static bool _mcResolvedOnce;
    private static int _nullReadCount;

    /// <summary>Forgets the scene-bound PawnManager reference (called on scene reload). The
    /// MCGameObject field offset is stable across scenes, so it is not cleared.</summary>
    internal static void ResetCaches()
    {
        _pawnManagerCached = null;
        _lastPawnManagerSearchFrame = 0;
        _mcResolvedOnce = false;
        _nullReadCount = 0;
    }

    /// <summary>
    /// Returns the world position + yaw of the local player's MC GameObject,
    /// or `false` if the MC hasn't been instantiated yet (still in a menu/cutscene).
    /// </summary>
    public static bool TryGetPose(out Vector3 position, out float yaw)
    {
        position = default;
        yaw = 0f;

        var pm = FindPawnManager();
        if (pm == null) return false;

        // Resolve the MCGameObject backing-field offset once. Il2CppInterop only
        // exposes the real fields (not the C# properties), so we access the
        // compiler-generated backing field directly.
        if (_mcGameObjectOffset < 0)
        {
            var klass = IL2CPP.il2cpp_object_get_class(pm.Pointer);
            var field = IL2CPP.GetIl2CppField(klass, "<MCGameObject>k__BackingField");
            if (field == IntPtr.Zero)
            {
                ModLog.Warning("[LocalPlayer] <MCGameObject>k__BackingField not found on PawnManager");
                return false;
            }
            _mcGameObjectOffset = (int)IL2CPP.il2cpp_field_get_offset(field);
            ModLog.Debug($"[LocalPlayer] MCGameObject offset = 0x{_mcGameObjectOffset:X}");
        }

        IntPtr goPtr = *(IntPtr*)((byte*)pm.Pointer + _mcGameObjectOffset);
        if (goPtr == IntPtr.Zero)
        {
            // MC not yet present — still in a cutscene/loading. Log a heartbeat
            // every ~3 seconds so we know we're still polling.
            _nullReadCount++;
            if (_nullReadCount == 1 || _nullReadCount % 30 == 0)
                ModLog.Debug($"[LocalPlayer] MC still null (try #{_nullReadCount})");
            return false;
        }

        var go = new GameObject(goPtr);
        if (go == null) return false;

        var t = go.transform;
        position = t.position;
        yaw = t.eulerAngles.y;

        // Log the first successful resolution — an important moment, we want to see it.
        if (!_mcResolvedOnce)
        {
            _mcResolvedOnce = true;
            ModLog.Debug($"[LocalPlayer] MC transform RESOLVED @ ({position.x:F1}, {position.y:F1}, {position.z:F1})  after {_nullReadCount} null reads");
        }
        return true;
    }

    private static MonoBehaviour FindPawnManager() => GameInterop.FindMonoBehaviourByName("PawnManager", ref _pawnManagerCached, ref _lastPawnManagerSearchFrame);

    /// <summary>
    /// Returns the local player's MC GameObject itself (not a clone).
    /// Callers can use `Object.Instantiate` to get a rendered clone that uses exactly
    /// the same render-pipeline setup as the real MC — sidesteps all the
    /// SkinnedMeshRenderer / custom shader / render-pass issues hit when
    /// instantiating NetplayClimberPrefab.
    /// </summary>
    public static GameObject TryGetMCGameObject()
    {
        var pm = FindPawnManager();
        if (pm == null) return null;

        if (_mcGameObjectOffset < 0)
        {
            var klass = IL2CPP.il2cpp_object_get_class(pm.Pointer);
            var field = IL2CPP.GetIl2CppField(klass, "<MCGameObject>k__BackingField");
            if (field == IntPtr.Zero) return null;
            _mcGameObjectOffset = (int)IL2CPP.il2cpp_field_get_offset(field);
        }

        IntPtr goPtr = *(IntPtr*)((byte*)pm.Pointer + _mcGameObjectOffset);
        if (goPtr == IntPtr.Zero) return null;

        return new GameObject(goPtr);
    }
}
