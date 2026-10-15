using System;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Networking;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal static partial class RopeInterop
{
    private static Il2Cpp.Lifeline _lifelineCached;
    private static int _lastLifelineSearchFrame;
    private static readonly PitonSync _pitons = new();
    private static readonly List<PlacedPiton> _placedPitons = new();
    private static readonly HashSet<IntPtr> _pitonPointers = new();
    private static readonly HashSet<IntPtr> _pitonPointersAfterSpawn = new();
    // A refused native detach is tried again, but not every frame.
    private static readonly Dictionary<IntPtr, float> _detachRetryAt = new();
    private const float DetachRetrySeconds = 2f;
    private static float _lastPitonCheckErrorLogAt;
    private const float PitonCheckErrorLogIntervalSeconds = 5f;
    private static Il2Cpp.ClimbingV2PawnController _localClimbControllerCached;

    internal static void ResetCaches()
    {
        // Scene callbacks may arrive after native objects were destroyed; never invoke
        // native removal through references belonging to the previous scene here. Piton
        // identities are not dropped either: whether the Lifeline survived is only known
        // once it resolves again, and PitonSync decides it then.
        _lifelineCached = null;
        _lastLifelineSearchFrame = 0;
        _detachRetryAt.Clear();
        _localClimbControllerCached = null;
    }

    /// <summary>Leaving the session: the other climbers' pitons come off this Lifeline.</summary>
    internal static void ClearRemotePitons()
    {
        try
        {
            var lifeline = TryGetLifeline();
            if (lifeline != null && TryCollectPitonPointers(lifeline, _pitonPointers))
            {
                foreach (var pointer in new List<IntPtr>(_pitons.RemotePointers))
                    if (_pitonPointers.Contains(pointer)) TryDetachPitonViaLifeline(pointer);
            }
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clear-remote-pitons", exception);
        }
        _pitons.ForgetSession();
        _detachRetryAt.Clear();
    }

    /// <summary>The host announced a piton. It is created on the next frame of play, so one
    /// announced during a load or a camp is not lost.</summary>
    internal static void KnowRemotePiton(ServerPitonPlaced placed) => _pitons.Know(placed);

    internal static void ForgetRemotePiton(uint pitonId) => _pitons.Forget(pitonId);

    /// <summary>Another climber took up one of our pitons.</summary>
    internal static void ForgetOwnPiton(uint localId) => _pitons.TakenByOther(localId);

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

    /// <summary>
    /// One pass per frame of play: read what the Lifeline holds, let PitonSync decide, carry
    /// out the native part and tell the host the rest.
    /// </summary>
    internal static void SyncPitons(NetworkManager network)
    {
        var lifeline = TryGetLifeline();
        if (lifeline == null || network == null) return;

        try
        {
            double now = Time.unscaledTime;
            _pitons.OnLifeline(lifeline.Pointer, now);
            if (!TryCollectPlacedPitons(lifeline)) return;

            System.Numerics.Vector3? localPosition = null;
            if (Players.LocalPlayerInterop.TryGetPose(out var pose, out _))
                localPosition = new System.Numerics.Vector3(pose.x, pose.y, pose.z);

            var actions = _pitons.Reconcile(_placedPitons, localPosition, now);
            if (actions.IsEmpty) return;

            foreach (var pointer in actions.Detach) DetachWhenAllowed(pointer);
            foreach (var placed in actions.Spawn) SpawnRemotePiton(lifeline, placed, now);
            foreach (var (localId, pointer) in actions.Announce) AnnounceLocalPiton(network, localId, pointer);
            foreach (var localId in actions.Removed) network.SendPitonRemoved(localId);
            foreach (var pitonId in actions.Recovered)
            {
                network.SendPitonRecovered(pitonId);
                ModLog.Debug($"[Piton] Took up remote piton #{pitonId}");
            }
        }
        catch (Exception ex)
        {
            // Rate-limited: the list can be transiently inconsistent (reload, pickup),
            // no point drowning the log + weighing down the frame with IL2CPP stack traces.
            var now = Time.unscaledTime;
            if (now - _lastPitonCheckErrorLogAt >= PitonCheckErrorLogIntervalSeconds)
            {
                _lastPitonCheckErrorLogAt = now;
                ModLog.Warning($"[Piton] Sync failed (rate-limited): {ex.GetType().Name}: {GameInterop.FirstLine(ex.Message)}");
            }
        }
    }

    /// <summary>
    /// Fills the frame's list. Positions are read only for pitons not yet accounted for:
    /// they are what tells two pitons apart, and reading them costs a native call each.
    /// </summary>
    private static bool TryCollectPlacedPitons(Il2Cpp.Lifeline lifeline)
    {
        _placedPitons.Clear();
        var placedPitons = lifeline.PlacedPitons;
        if (placedPitons == null) return false;

        for (var i = 0; i < placedPitons.Count; i++)
        {
            var piton = placedPitons[i]?.Piton;
            if (piton == null || piton.Pointer == IntPtr.Zero) continue;

            var position = default(System.Numerics.Vector3);
            if (!_pitons.Tracks(piton.Pointer))
            {
                // The list can briefly contain a destroyed entry during reload/removal.
                try
                {
                    var transform = piton.transform;
                    if (transform == null || transform.Pointer == IntPtr.Zero) continue;
                    var p = transform.position;
                    position = new System.Numerics.Vector3(p.x, p.y, p.z);
                }
                catch (Exception exception)
                {
                    ModLog.SuppressedException("rope.resolve-local-piton", exception);
                    continue;
                }
            }

            _placedPitons.Add(new PlacedPiton(piton.Pointer, position));
        }

        return true;
    }

    private static void AnnounceLocalPiton(NetworkManager network, uint localId, IntPtr pointer)
    {
        try
        {
            var piton = new Il2Cpp.Piton(pointer);
            var t = piton.transform;
            if (t == null || t.Pointer == IntPtr.Zero) return;
            var position = t.position;
            var rotation = t.rotation;
            var quality = (byte)piton.executionQuality;
            var hp = piton.pitonHp;
            var itemId = piton.ItemId.value;

            network.SendPitonPlaced(new ClientPitonPlaced
            {
                PitonId = localId,
                PosX = position.x,
                PosY = position.y,
                PosZ = position.z,
                RotX = rotation.x,
                RotY = rotation.y,
                RotZ = rotation.z,
                RotW = rotation.w,
                Quality = quality,
                PitonHp = hp,
                ItemId = itemId,
            });
            ModLog.Debug($"[Piton] Local piton #{localId} detected @ ({position.x:F1},{position.y:F1},{position.z:F1}) quality={quality} hp={hp} itemId={itemId}");
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.announce-local-piton", exception);
        }
    }

    /// <summary>
    /// Spawns a piton through Cairn's typed Lifeline API. Passing the local climbing
    /// controller lets the game initialize its ClimbingSetting before the piton is
    /// visible to Lifeline.Update or save serialization.
    /// </summary>
    private static void SpawnRemotePiton(Il2Cpp.Lifeline lifeline, ServerPitonPlaced placed, double now)
    {
        var controller = ResolveLocalClimbController();
        if (controller == null || controller.Pointer == IntPtr.Zero)
        {
            ModLog.Warning("[Piton] Local ClimbingV2PawnController not found — cannot create a save-safe remote piton");
            _pitons.SpawnFailed(placed.PitonId, now);
            return;
        }
        if (!TryCollectPitonPointers(lifeline, _pitonPointers))
        {
            _pitons.SpawnFailed(placed.PitonId, now);
            return;
        }

        try
        {
            lifeline.AddPiton(new Vector3(placed.PosX, placed.PosY, placed.PosZ),
                new Quaternion(placed.RotX, placed.RotY, placed.RotZ, placed.RotW),
                (Il2Cpp.PitonExecutionQuality)placed.Quality, placed.PitonHp,
                (Il2Cpp.InventoryItemStringId)placed.ItemId, controller);
        }
        catch (Exception ex)
        {
            ModLog.Error($"[Piton] SpawnRemotePiton failed: {ex.Message}");
        }

        // Native code may allocate and then throw: whatever appeared is accounted for.
        var spawned = IntPtr.Zero;
        if (TryCollectPitonPointers(lifeline, _pitonPointersAfterSpawn))
        {
            foreach (var pointer in _pitonPointersAfterSpawn)
            {
                if (_pitonPointers.Contains(pointer)) continue;
                if (spawned == IntPtr.Zero) spawned = pointer;
                else _pitons.Ignore(pointer);
            }
        }

        if (spawned == IntPtr.Zero)
        {
            _pitons.SpawnFailed(placed.PitonId, now);
            return;
        }

        _pitons.Spawned(placed.PitonId, spawned);
        ModLog.Debug($"[Piton] Spawned remote piton #{placed.PitonId} via Lifeline.AddPiton @ ({placed.PosX:F1},{placed.PosY:F1},{placed.PosZ:F1}) quality={placed.Quality} hp={placed.PitonHp}");
    }

    private static void DetachWhenAllowed(IntPtr pointer)
    {
        var now = Time.unscaledTime;
        if (_detachRetryAt.TryGetValue(pointer, out var retryAt) && now < retryAt) return;
        // A failed native removal keeps the piton accounted for. Destroying only the visual
        // could leave a dangling entry in the native save/rope collection.
        if (TryDetachPitonViaLifeline(pointer)) _detachRetryAt.Remove(pointer);
        else _detachRetryAt[pointer] = now + DetachRetrySeconds;
    }

    // Native detach needs the Piton pointer; destroying only its GameObject leaves rope state dangling.
    private static bool TryDetachPitonViaLifeline(IntPtr pitonPtr)
    {
        if (pitonPtr == IntPtr.Zero) return false;

        var lifeline = TryGetLifeline();
        if (lifeline == null) return false;

        try
        {
            lifeline.DetachPiton(new Il2Cpp.Piton(pitonPtr));
            ModLog.Debug("[Piton] Detached a piton via Lifeline.DetachPiton");
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

    private static bool TryCollectPitonPointers(Il2Cpp.Lifeline lifeline, HashSet<IntPtr> pointers)
    {
        pointers.Clear();
        var placedPitons = lifeline?.PlacedPitons;
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
