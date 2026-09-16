using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal static partial class RopeInterop
{
    private static Il2Cpp.Lifeline _lifelineCached;
    private static int _lastLifelineSearchFrame;
    private static readonly PitonIdentityTracker _pitonIdentities = new();
    private static bool _spawningRemotePiton;
    private static HashSet<IntPtr> _beforeRemoteSpawn;
    private static readonly Dictionary<uint, GameObject> _remotePitonsByNetId = new();
    // Native detach needs the Piton pointer; destroying only its GameObject leaves rope state dangling.
    private static readonly Dictionary<uint, IntPtr> _remotePitonPointersByNetId = new();
    private static readonly HashSet<uint> _remotePitonSpawnAttempts = new();
    private static float _lastPitonCheckErrorLogAt;
    private const float PitonCheckErrorLogIntervalSeconds = 5f;
    private static Il2Cpp.ClimbingV2PawnController _localClimbControllerCached;

    internal static void ResetCaches()
    {
        // Scene callbacks may arrive after native objects were destroyed; never invoke
        // native removal through references belonging to the previous scene here.
        _lifelineCached = null;
        _lastLifelineSearchFrame = 0;
        _pitonIdentities.Clear();
        _beforeRemoteSpawn = null;
        _spawningRemotePiton = false;
        _remotePitonsByNetId.Clear();
        _remotePitonPointersByNetId.Clear();
        _remotePitonSpawnAttempts.Clear();
        _localClimbControllerCached = null;
    }

    internal static void ClearRemotePitons()
    {
        foreach (var id in new List<uint>(_remotePitonPointersByNetId.Keys)) RemoveRemotePiton(id);
        foreach (var id in new List<uint>(_remotePitonsByNetId.Keys)) RemoveRemotePiton(id);
    }

    public static Il2Cpp.Lifeline TryGetLifeline()
    {
        if (_lifelineCached != null) return _lifelineCached;
        if (_lastLifelineSearchFrame != 0 && Time.frameCount - _lastLifelineSearchFrame < 30) return null;
        _lastLifelineSearchFrame = Time.frameCount;

        try
        {
            var mcGameObject = Players.LocalPlayerInterop.TryGetMCGameObject();
            _lifelineCached = mcGameObject?.GetComponent<Il2Cpp.Lifeline>()
                              ?? mcGameObject?.GetComponentInChildren<Il2Cpp.Lifeline>(true);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.resolve-lifeline", exception);
        }

        return _lifelineCached;
    }

    public static bool CheckForNewPiton(out uint netId, out Vector3 position,
        out Quaternion rotation, out byte quality, out int hp, out int itemId)
    {
        netId = 0;
        position = default;
        rotation = default;
        quality = 0;
        hp = 0;
        itemId = 0;

        var lifeline = TryGetLifeline();
        if (lifeline == null) return false;

        try
        {
            if (_spawningRemotePiton || !TryCollectCurrentPitonPointers(out var current)) return false;
            if (!CompleteRemoteSpawnDiscovery(current)) return false;
            if (!_pitonIdentities.TryFindNew(current, out var pitonPtr)) return false;

            // The list can briefly contain a destroyed entry during reload/removal.
            var piton = new Il2Cpp.Piton(pitonPtr);
            Transform t;
            try
            {
                t = piton.transform;
                if (t == null || t.Pointer == IntPtr.Zero)
                    return false;
                position = t.position;
                rotation = t.rotation;
            }
            catch (Exception exception)
            {
                ModLog.SuppressedException("rope.resolve-local-piton", exception);
                return false;
            }

            hp = piton.pitonHp;
            quality = (byte)piton.executionQuality;
            itemId = piton.ItemId.value;

            netId = _pitonIdentities.Announce(pitonPtr);
            ModLog.Debug($"[Piton] Local piton #{netId} detected @ ({position.x:F1},{position.y:F1},{position.z:F1}) quality={quality} hp={hp} itemId={itemId}");
            return true;
        }
        catch (Exception ex)
        {
            // Rate-limited: the list can be transiently inconsistent (reload, pickup),
            // no point drowning the log + weighing down the frame with IL2CPP stack traces.
            var now = Time.unscaledTime;
            if (now - _lastPitonCheckErrorLogAt >= PitonCheckErrorLogIntervalSeconds)
            {
                _lastPitonCheckErrorLogAt = now;
                ModLog.Warning($"[Piton] CheckForNewPiton failed (rate-limited): {ex.GetType().Name}: {GameInterop.FirstLine(ex.Message)}");
            }
            return false;
        }
    }

    public static bool CheckForRemovedPiton(out uint netId)
    {
        netId = 0;

        try
        {
            if (!TryCollectCurrentPitonPointers(out var currentPointers))
                return false;

            return _pitonIdentities.TryRemoveMissing(currentPointers, out netId);
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[Piton] CheckForRemovedPiton failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Spawns a piton through Cairn's typed Lifeline API. Passing the local climbing
    /// controller lets the game initialize its ClimbingSetting before the piton is
    /// visible to Lifeline.Update or save serialization.
    /// </summary>
    public static bool SpawnRemotePiton(Vector3 position, Quaternion rotation,
        int quality, int hp, int itemId)
    {
        try
        {
            var lifeline = TryGetLifeline();
            if (lifeline == null)
            {
                ModLog.Warning("[Piton] Lifeline not found — cannot spawn remote piton");
                return false;
            }

            var controller = ResolveLocalClimbController();
            if (controller == null || controller.Pointer == IntPtr.Zero)
            {
                ModLog.Warning("[Piton] Local ClimbingV2PawnController not found — cannot create a save-safe remote piton");
                return false;
            }

            // Capture identities even if native code allocates and then throws.
            if (_spawningRemotePiton || !TryCollectCurrentPitonPointers(out var before)) return false;
            if (!CompleteRemoteSpawnDiscovery(before)) return false;
            _beforeRemoteSpawn = before;
            _spawningRemotePiton = true;
            try
            {
                lifeline.AddPiton(position, rotation, (Il2Cpp.PitonExecutionQuality)quality,
                    hp, (Il2Cpp.InventoryItemStringId)itemId, controller);
            }
            finally
            {
                _spawningRemotePiton = false;
                if (TryCollectCurrentPitonPointers(out var after)) CompleteRemoteSpawnDiscovery(after);
            }

            ModLog.Debug($"[Piton] Spawned remote piton via Lifeline.AddPiton @ ({position.x:F1},{position.y:F1},{position.z:F1}) quality={quality} hp={hp}");
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error($"[Piton] SpawnRemotePiton failed: {ex.Message}");
            return false;
        }
    }

    public static bool SpawnRemotePiton(uint netId, Vector3 position, Quaternion rotation,
        int quality, int hp, int itemId)
    {
        if (_remotePitonSpawnAttempts.Contains(netId)) return true;
        if (_remotePitonSpawnAttempts.Count >= 1024 || TryGetLifeline() == null) return false;
        // Reserve before native invocation: an exception can occur after native allocation.
        _remotePitonSpawnAttempts.Add(netId);
        var ok = SpawnRemotePiton(position, rotation, quality, hp, itemId);
        if (!ok) return false;

        if (TryGetLastPitonPointer(out var pitonPtr) && pitonPtr != IntPtr.Zero)
        {
            _remotePitonPointersByNetId[netId] = pitonPtr;
            try
            {
                var pitonGo = new MonoBehaviour(pitonPtr).gameObject;
                if (pitonGo != null)
                    _remotePitonsByNetId[netId] = pitonGo;
            }
            catch (Exception exception) { ModLog.SuppressedException("rope.remove-remote-piton", exception); }
        }
        else
        {
            ModLog.Warning($"[Piton] Spawned remote #{netId} but could not capture piton pointer for later detach");
        }

        return true;
    }

    public static bool RemoveRemotePiton(uint netId)
    {
        // A failed native removal keeps both identity and budget reserved. Destroying only
        // the visual could leave a dangling entry in the native save/rope collection.
        if (!_remotePitonPointersByNetId.TryGetValue(netId, out var pointer)
            || pointer == IntPtr.Zero || !TryDetachPitonViaLifeline(pointer))
            return false;
        _remotePitonPointersByNetId.Remove(netId);
        _pitonIdentities.ForgetRemote(pointer);
        _remotePitonsByNetId.Remove(netId);
        _remotePitonSpawnAttempts.Remove(netId);
        ModLog.Debug($"[Piton] Detached remote piton #{netId} via Lifeline.DetachPiton");
        return true;
    }
    private static bool TryDetachPitonViaLifeline(IntPtr pitonPtr)
    {
        if (pitonPtr == IntPtr.Zero) return false;

        var lifeline = TryGetLifeline();
        if (lifeline == null) return false;

        try
        {
            lifeline.DetachPiton(new Il2Cpp.Piton(pitonPtr));
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[Piton] DetachPiton failed: {ex.Message}");
            return false;
        }
    }

    private static Il2Cpp.ClimbingV2PawnController ResolveLocalClimbController()
    {
        if (_localClimbControllerCached != null) return _localClimbControllerCached;
        try
        {
            _localClimbControllerCached = Il2Cpp.PawnManager.Instance?.ClimbingPawnController;
            if (_localClimbControllerCached != null)
                ModLog.Debug("[Piton] Local ClimbingV2PawnController resolved");
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.resolve-climbing-controller", exception);
        }
        return _localClimbControllerCached;
    }

    private static bool CompleteRemoteSpawnDiscovery(HashSet<IntPtr> current)
    {
        if (_spawningRemotePiton) return false;
        if (_beforeRemoteSpawn == null) return true;
        foreach (var pointer in current)
            if (!_beforeRemoteSpawn.Contains(pointer)) _pitonIdentities.MarkRemote(pointer);
        _beforeRemoteSpawn = null;
        return true;
    }

    private static bool TryGetLastPitonPointer(out IntPtr pitonPtr)
    {
        pitonPtr = IntPtr.Zero;
        var piton = TryGetLifeline()?.GetLastPiton();
        if (piton == null || piton.Pointer == IntPtr.Zero) return false;
        pitonPtr = piton.Pointer;
        return true;
    }

    private static bool TryCollectCurrentPitonPointers(out HashSet<IntPtr> pointers)
    {
        pointers = new HashSet<IntPtr>();
        var placedPitons = TryGetLifeline()?.PlacedPitons;
        if (placedPitons == null) return false;

        for (var i = 0; i < placedPitons.Count; i++)
        {
            var piton = placedPitons[i]?.Piton;
            if (piton != null && piton.Pointer != IntPtr.Zero)
                pointers.Add(piton.Pointer);
        }

        return true;
    }
}
