using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

public static unsafe partial class CairnGameApi
{
    private static MonoBehaviour _pawnManagerCached;
    private static int _mcGameObjectOffset = -1;
    private static int _lastPawnManagerSearchFrame;
    private static bool _mcResolvedOnce;
    private static int _nullReadCount;

    /// <summary>
    /// Returns the world position + yaw of the local player's MC GameObject,
    /// or `false` if the MC hasn't been instantiated yet (still in a menu/cutscene).
    /// </summary>
    public static bool TryGetLocalPlayerPose(out Vector3 position, out float yaw)
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
                Mod.Log.Warning("[CairnGameApi] <MCGameObject>k__BackingField not found on PawnManager");
                return false;
            }
            _mcGameObjectOffset = (int)IL2CPP.il2cpp_field_get_offset(field);
            Mod.LogDebug($"[CairnGameApi] MCGameObject offset = 0x{_mcGameObjectOffset:X}");
        }

        IntPtr goPtr = *(IntPtr*)((byte*)pm.Pointer + _mcGameObjectOffset);
        if (goPtr == IntPtr.Zero)
        {
            // MC not yet present — still in a cutscene/loading. Log a heartbeat
            // every ~3 seconds so we know we're still polling.
            _nullReadCount++;
            if (_nullReadCount == 1 || _nullReadCount % 30 == 0)
                Mod.LogDebug($"[CairnGameApi] MC still null (try #{_nullReadCount})");
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
            Mod.LogDebug($"[CairnGameApi] MC transform RESOLVED @ ({position.x:F1}, {position.y:F1}, {position.z:F1})  after {_nullReadCount} null reads");
        }
        return true;
    }

    private static MonoBehaviour FindPawnManager() => FindMonoBehaviourByName("PawnManager", ref _pawnManagerCached, ref _lastPawnManagerSearchFrame);

    /// <summary>
    /// Returns the local player's MC GameObject itself (not a clone).
    /// Callers can use `Object.Instantiate` to get a rendered clone that uses exactly
    /// the same render-pipeline setup as the real MC — sidesteps all the
    /// SkinnedMeshRenderer / custom shader / render-pass issues hit when
    /// instantiating NetplayClimberPrefab.
    /// </summary>
    public static GameObject TryGetLocalMCGameObject()
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

    /// <summary>
    /// Moves the local character (MC) to <paramref name="position"/> and orients its
    /// yaw. Used by the admin commands: /tp (the host moves locally toward a player)
    /// and /bring (a client receives a ServerTeleport and moves there).
    /// Returns false if the MC isn't instantiated yet (menu/cutscene).
    /// </summary>
    public static bool TeleportLocalPlayer(Vector3 position, float yawDeg)
    {
        var go = TryGetLocalMCGameObject();
        if (go == null) return false;

        // Target zone different from the current zone? -> we do as the game does: a managed TRAVEL
        // (load the target zone + clean unload of the origin), then set the exact position once
        // the world is idle. Otherwise (same zone), direct instant teleport.
        // See TeleportStreaming.cs.
        if (TryTeleportAcrossZones(position, yawDeg))
            return true;

        var t = go.transform;
        t.position = position;
        var euler = t.eulerAngles;
        t.eulerAngles = new Vector3(euler.x, yawDeg, euler.z);
        Mod.LogDebug($"[CairnGameApi] Teleported local MC to ({position.x:F1}, {position.y:F1}, {position.z:F1}) yaw={yawDeg:F0}");
        return true;
    }
}
