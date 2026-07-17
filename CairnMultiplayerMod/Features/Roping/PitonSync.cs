using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Features.Roping;

internal static unsafe partial class RopeApi
{
    private static MonoBehaviour _lifelineCached;
    private static int _lastLifelineSearchFrame;
    private static int _lastKnownPitonCount;
    private static uint _nextPitonNetId = 1;
    private static int _remotePitonsAdded; // how many pitons we spawned via AddPiton (to ignore during detection)
    private static readonly Dictionary<IntPtr, uint> _localPitonIdsByPointer = new();
    private static readonly Dictionary<uint, GameObject> _remotePitonsByNetId = new();
    // IL2CPP pointer of the Piton component — used to call Lifeline.DetachPiton
    // which cleanly removes the piton from the rope and destroys the visual (vs. just
    // Object.Destroy on the GameObject, which can leave a dangling ref).
    private static readonly Dictionary<uint, IntPtr> _remotePitonPointersByNetId = new();
    private static IntPtr _lifelineDetachPitonMethod;
    private static bool _lifelineDetachPitonResolved;
    private static float _lastPitonCheckErrorLogAt;
    private const float PitonCheckErrorLogIntervalSeconds = 5f;
    // LOCAL ClimbingV2PawnController — used to fill in the ClimbingSetting of remote
    // pitons (spawned with ClimbingSetting=null) via UpdatePlacedPitonClimbingSetting.
    private static Il2Cpp.ClimbingV2PawnController _localClimbControllerCached;
    private static int _lastClimbControllerSearchFrame;
    private static IntPtr _lifelineUpdateSettingMethod;
    private static bool _lifelineUpdateSettingResolved;

    /// <summary>Forgets the scene-bound Lifeline reference and the piton bookkeeping
    /// (called on scene reload).</summary>
    internal static void ResetCaches()
    {
        _lifelineCached = null;
        _lastLifelineSearchFrame = 0;
        _lastKnownPitonCount = 0;
        _remotePitonsAdded = 0;
        _localPitonIdsByPointer.Clear();
        _remotePitonsByNetId.Clear();
        _remotePitonPointersByNetId.Clear();
    }

    public static MonoBehaviour TryGetLifeline()
        => GameInterop.FindMonoBehaviourByName("Lifeline", ref _lifelineCached, ref _lastLifelineSearchFrame);

    /// <summary>
    /// Checks whether new pitons have been placed since the last call. Returns
    /// the new piton's world position, rotation and quality via the out parameters.
    /// </summary>
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
            var klass = IL2CPP.il2cpp_object_get_class(lifeline.Pointer);
            var field = IL2CPP.GetIl2CppField(klass, "<PlacedPitons>k__BackingField");
            if (field == IntPtr.Zero)
                field = IL2CPP.GetIl2CppField(klass, "PlacedPitons");
            if (field == IntPtr.Zero) return false;

            int offset = (int)IL2CPP.il2cpp_field_get_offset(field);
            IntPtr listPtr = *(IntPtr*)((byte*)lifeline.Pointer + offset);
            if (listPtr == IntPtr.Zero) return false;

            // Il2Cpp List<T> has _size at a known offset. We read it.
            // List<T> layout: [klass, monitor, _items (array ptr), _size (int), _version (int)]
            // _items is at offset 2*IntPtr.Size, _size at 2*IntPtr.Size + IntPtr.Size
            int sizeOffset = 3 * IntPtr.Size;
            int currentCount = *(int*)((byte*)listPtr + sizeOffset);

            if (currentCount <= _lastKnownPitonCount)
            {
                _lastKnownPitonCount = currentCount;
                return false;
            }

            // Check whether this increase is caused by a remote spawn we did
            // ourselves. If so, we just update the counter and move on.
            int newPitons = currentCount - _lastKnownPitonCount;
            if (_remotePitonsAdded >= newPitons)
            {
                _remotePitonsAdded -= newPitons;
                _lastKnownPitonCount = currentCount;
                return false;
            }
            _remotePitonsAdded = 0;

            // New LOCAL piton(s) added. Read the last one from the _items array.
            _lastKnownPitonCount = currentCount;
            IntPtr itemsArrayPtr = *(IntPtr*)((byte*)listPtr + 2 * IntPtr.Size);
            if (itemsArrayPtr == IntPtr.Zero) return false;

            // The items array is an Il2CppArray of PlacedPitonData references.
            int headerSize = 4 * IntPtr.Size;
            IntPtr lastItemPtr = *(IntPtr*)((byte*)itemsArrayPtr + headerSize + (currentCount - 1) * IntPtr.Size);
            if (lastItemPtr == IntPtr.Zero) return false;

            // PlacedPitonData has a Piton field (first backing field).
            var pdKlass = IL2CPP.il2cpp_object_get_class(lastItemPtr);
            var pitonField = IL2CPP.GetIl2CppField(pdKlass, "<Piton>k__BackingField");
            if (pitonField == IntPtr.Zero)
                pitonField = IL2CPP.GetIl2CppField(pdKlass, "piton");
            if (pitonField == IntPtr.Zero) return false;

            int pitonOffset = (int)IL2CPP.il2cpp_field_get_offset(pitonField);
            IntPtr pitonPtr = *(IntPtr*)((byte*)lastItemPtr + pitonOffset);
            if (pitonPtr == IntPtr.Zero) return false;

            // Read the Piton MonoBehaviour's transform for the position/rotation.
            // The pointer can be stale (destroyed entry still in the list after
            // a removal / save reload). We validate before touching transform
            // to avoid spamming IL2CPP NREs that tank the frame.
            var pitonMono = new MonoBehaviour(pitonPtr);
            Transform t;
            try
            {
                t = pitonMono.transform;
                if (t == null || t.Pointer == IntPtr.Zero)
                    return false;
                position = t.position;
                rotation = t.rotation;
            }
            catch
            {
                // Ghost piton: we swallow it without logging to avoid spam.
                return false;
            }

            // Read the pitonHp field.
            var pitonKlass = IL2CPP.il2cpp_object_get_class(pitonPtr);
            var hpField = IL2CPP.GetIl2CppField(pitonKlass, "pitonHp");
            if (hpField != IntPtr.Zero)
            {
                int hpOff = (int)IL2CPP.il2cpp_field_get_offset(hpField);
                hp = *(int*)((byte*)pitonPtr + hpOff);
            }

            // Read executionQuality (enum, int-sized).
            var qualField = IL2CPP.GetIl2CppField(pitonKlass, "executionQuality");
            if (qualField != IntPtr.Zero)
            {
                int qualOff = (int)IL2CPP.il2cpp_field_get_offset(qualField);
                quality = (byte)(*(int*)((byte*)pitonPtr + qualOff));
            }

            // Read ItemId (InventoryItemStringId -- a struct wrapping a single int).
            var itemField = IL2CPP.GetIl2CppField(pitonKlass, "<ItemId>k__BackingField");
            int readItemId = 0;
            if (itemField != IntPtr.Zero)
            {
                int itemOff = (int)IL2CPP.il2cpp_field_get_offset(itemField);
                readItemId = *(int*)((byte*)pitonPtr + itemOff);
            }
            itemId = readItemId;

            netId = _nextPitonNetId++;
            _localPitonIdsByPointer[pitonPtr] = netId;
            Mod.LogDebug($"[Piton] Local piton #{netId} detected @ ({position.x:F1},{position.y:F1},{position.z:F1}) quality={quality} hp={hp} itemId={itemId}");
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
                Mod.Log.Warning($"[Piton] CheckForNewPiton failed (rate-limited): {ex.GetType().Name}: {GameInterop.FirstLine(ex.Message)}");
            }
            return false;
        }
    }

    /// <summary>
    /// Detects the removal of a local piton already announced to the network.
    /// </summary>
    public static bool CheckForRemovedPiton(out uint netId)
    {
        netId = 0;
        if (_localPitonIdsByPointer.Count == 0)
            return false;

        try
        {
            if (!TryCollectCurrentPitonPointers(out var currentPointers))
                return false;

            foreach (var kv in new List<KeyValuePair<IntPtr, uint>>(_localPitonIdsByPointer))
            {
                if (currentPointers.Contains(kv.Key))
                    continue;

                _localPitonIdsByPointer.Remove(kv.Key);
                netId = kv.Value;
                Mod.LogDebug($"[Piton] Local piton #{netId} removed -> sending ClientPitonRemoved");
                return true;
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[Piton] CheckForRemovedPiton failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Spawns a piton on the remote client by calling Lifeline.AddPiton()
    /// via IL2CPP runtime invocation. Registers the piton properly in the rope
    /// system so quickdraws and rope clipping work correctly.
    /// </summary>
    public static bool SpawnRemotePiton(Vector3 position, Quaternion rotation,
        int quality, int hp, int itemId)
    {
        try
        {
            var lifeline = TryGetLifeline();
            if (lifeline == null)
            {
                Mod.Log.Warning("[Piton] Lifeline not found — cannot spawn remote piton");
                return false;
            }

            var klass = IL2CPP.il2cpp_object_get_class(lifeline.Pointer);

            // Find the AddPiton method with 6 parameters. We want the overload:
            // AddPiton(Vector3, Quaternion, PitonExecutionQuality, int, InventoryItemStringId, ClimbingSetting)
            // where ClimbingSetting is a reference type we can pass as null.
            IntPtr method = IntPtr.Zero;
            IntPtr iter = IntPtr.Zero;
            while (true)
            {
                var m = IL2CPP.il2cpp_class_get_methods(klass, ref iter);
                if (m == IntPtr.Zero) break;
                var namePtr = IL2CPP.il2cpp_method_get_name(m);
                var name = Marshal.PtrToStringAnsi(namePtr);
                if (name == "AddPiton" && IL2CPP.il2cpp_method_get_param_count(m) == 6)
                {
                    // Verify the last parameter is a reference type (the ClimbingSetting class,
                    // not ClimbingV2PawnController). Both are reference types, but we take
                    // the SECOND match (the ClimbingSetting overload is declared after the
                    // Controller one in the decompilation).
                    method = m;
                    // Keep iterating to get the LAST 6-parameter overload.
                }
            }

            if (method == IntPtr.Zero)
            {
                Mod.Log.Error("[Piton] Lifeline.AddPiton method not found");
                return false;
            }

            // Prepare the arguments for il2cpp_runtime_invoke.
            // Value types are passed as pointers to their data.
            var pos = position;
            var rot = rotation;
            int qual = quality;
            int pitonHp = hp;
            int pitonItemId = itemId;

            var args = stackalloc IntPtr[6];
            args[0] = (IntPtr)(&pos);                // Vector3 pitonPosition
            args[1] = (IntPtr)(&rot);                // Quaternion pitonRotation
            args[2] = (IntPtr)(&qual);               // PitonExecutionQuality (enum = int)
            args[3] = (IntPtr)(&pitonHp);            // int pitonHp
            args[4] = (IntPtr)(&pitonItemId);        // InventoryItemStringId (struct = int)
            args[5] = IntPtr.Zero;                   // ClimbingSetting = null

            // Mark that we're about to add a piton ourselves so that
            // CheckForNewPiton ignores the resulting counter increase.
            _remotePitonsAdded++;

            IntPtr exception = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, lifeline.Pointer, (void**)args, ref exception);

            if (exception != IntPtr.Zero)
            {
                Mod.Log.Error($"[Piton] AddPiton threw an exception");
                return false;
            }

            // The native piton was just added with ClimbingSetting=null (6th arg of AddPiton).
            // A piton with a null ClimbingSetting crashes Lifeline.Update() and
            // Piton.WriteToSavegame (native NRE) as soon as a rope attaches to it -> aborted
            // save. We backfill the setting with the LOCAL climbing controller.
            TryAssignLocalClimbingSetting(lifeline);

            Mod.LogDebug($"[Piton] Spawned remote piton via Lifeline.AddPiton @ ({position.x:F1},{position.y:F1},{position.z:F1}) quality={quality} hp={hp}");
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[Piton] SpawnRemotePiton failed: {ex.Message}");
            return false;
        }
    }

    public static bool SpawnRemotePiton(uint netId, Vector3 position, Quaternion rotation,
        int quality, int hp, int itemId)
    {
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
            catch { }
        }
        else
        {
            Mod.Log.Warning($"[Piton] Spawned remote #{netId} but could not capture piton pointer for later detach");
        }

        return true;
    }

    public static bool RemoveRemotePiton(uint netId)
    {
        // 1) Preferred path: Lifeline.DetachPiton(piton) — removes it from the rope,
        //    detaches it from the lifelines and destroys the GameObject the vanilla way.
        if (_remotePitonPointersByNetId.TryGetValue(netId, out var pitonPtr) && pitonPtr != IntPtr.Zero)
        {
            _remotePitonPointersByNetId.Remove(netId);
            _remotePitonsByNetId.Remove(netId);

            if (TryDetachPitonViaLifeline(pitonPtr))
            {
                Mod.LogDebug($"[Piton] Detached remote piton #{netId} via Lifeline.DetachPiton");
                return true;
            }

            // Fallback: direct destroy on the captured GameObject.
            try
            {
                var go = new MonoBehaviour(pitonPtr).gameObject;
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                    Mod.LogDebug($"[Piton] Destroyed remote piton #{netId} GameObject (DetachPiton unavailable)");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warning($"[Piton] Fallback destroy failed for #{netId}: {ex.Message}");
            }
        }

        // 2) Legacy fallback: we had only stored the GameObject.
        if (_remotePitonsByNetId.TryGetValue(netId, out var pitonGo))
        {
            _remotePitonsByNetId.Remove(netId);
            if (pitonGo != null)
            {
                UnityEngine.Object.Destroy(pitonGo);
                Mod.LogDebug($"[Piton] Destroyed remote piton #{netId} via cached GameObject");
                return true;
            }
        }

        Mod.Log.Warning($"[Piton] RemoveRemotePiton #{netId}: no mapping found");
        return false;
    }

    private static bool TryDetachPitonViaLifeline(IntPtr pitonPtr)
    {
        if (pitonPtr == IntPtr.Zero) return false;

        var lifeline = TryGetLifeline();
        if (lifeline == null) return false;

        var method = ResolveLifelineDetachPiton(lifeline);
        if (method == IntPtr.Zero) return false;

        try
        {
            var args = stackalloc IntPtr[1];
            args[0] = pitonPtr;
            IntPtr exception = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, lifeline.Pointer, (void**)args, ref exception);
            if (exception != IntPtr.Zero)
            {
                Mod.Log.Warning("[Piton] Lifeline.DetachPiton threw, falling back");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[Piton] DetachPiton invoke failed: {ex.Message}");
            return false;
        }
    }

    private static IntPtr ResolveLifelineDetachPiton(MonoBehaviour lifeline)
    {
        if (_lifelineDetachPitonResolved) return _lifelineDetachPitonMethod;
        _lifelineDetachPitonResolved = true;

        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(lifeline.Pointer);
            // We look for the instance (non-static) overload with 1 parameter:
            // public void DetachPiton(Piton piton).
            IntPtr iter = IntPtr.Zero;
            while (true)
            {
                var m = IL2CPP.il2cpp_class_get_methods(klass, ref iter);
                if (m == IntPtr.Zero) break;
                var namePtr = IL2CPP.il2cpp_method_get_name(m);
                if (namePtr == IntPtr.Zero) continue;
                var name = Marshal.PtrToStringAnsi(namePtr);
                if (name != "DetachPiton") continue;
                if (IL2CPP.il2cpp_method_get_param_count(m) != 1) continue;
                _lifelineDetachPitonMethod = m;
                Mod.LogDebug("[Piton] Resolved Lifeline.DetachPiton(Piton)");
                break;
            }

            if (_lifelineDetachPitonMethod == IntPtr.Zero)
                Mod.Log.Warning("[Piton] Lifeline.DetachPiton(Piton) not found, will use GameObject fallback");
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[Piton] DetachPiton lookup failed: {ex.Message}");
        }

        return _lifelineDetachPitonMethod;
    }

    /// <summary>
    /// Fills in the ClimbingSetting of the last placed piton (remote spawn) with the
    /// LOCAL ClimbingV2PawnController, via Lifeline.UpdatePlacedPitonClimbingSetting.
    /// Without this the ClimbingSetting stays null -> native NRE in Lifeline.Update() and
    /// Piton.WriteToSavegame when the piton is clipped -> broken save.
    /// Best-effort: if the local controller can't be found, we leave the piton as-is.
    /// </summary>
    private static void TryAssignLocalClimbingSetting(MonoBehaviour lifeline)
    {
        try
        {
            if (!TryGetLastPitonPointer(out var pitonPtr) || pitonPtr == IntPtr.Zero)
                return;

            var controller = ResolveLocalClimbController();
            if (controller == null || controller.Pointer == IntPtr.Zero)
            {
                Mod.LogDebug("[Piton] No local ClimbingV2PawnController — ClimbingSetting left null (save may break on clip-in)");
                return;
            }

            var method = ResolveLifelineUpdateSettingMethod(lifeline);
            if (method == IntPtr.Zero) return;

            var args = stackalloc IntPtr[2];
            args[0] = pitonPtr;              // Piton
            args[1] = controller.Pointer;   // ClimbingV2PawnController
            IntPtr exception = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, lifeline.Pointer, (void**)args, ref exception);
            if (exception != IntPtr.Zero)
            {
                Mod.Log.Warning("[Piton] UpdatePlacedPitonClimbingSetting threw — piton save may still break");
                return;
            }

            Mod.LogDebug("[Piton] Assigned local ClimbingSetting to remote piton (save-safe)");
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[Piton] TryAssignLocalClimbingSetting failed: {ex.Message}");
        }
    }

    /// <summary>Finds (and caches) the LOCAL ClimbingV2PawnController. Remote players
    /// are NetplayRemotePlayer (a different type) -> FindObjectsOfType only returns the local one.</summary>
    private static Il2Cpp.ClimbingV2PawnController ResolveLocalClimbController()
    {
        if (_localClimbControllerCached != null) return _localClimbControllerCached;
        if (_lastClimbControllerSearchFrame != 0 && Time.frameCount - _lastClimbControllerSearchFrame < 30) return null;
        _lastClimbControllerSearchFrame = Time.frameCount;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<Il2Cpp.ClimbingV2PawnController>();
            if (all != null)
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null) { _localClimbControllerCached = all[i]; Mod.LogDebug("[Piton] Local ClimbingV2PawnController resolved"); break; }
        }
        catch (Exception ex) { Mod.Log.Warning($"[Piton] ClimbingV2PawnController search failed: {ex.Message}"); }
        return _localClimbControllerCached;
    }

    private static IntPtr ResolveLifelineUpdateSettingMethod(MonoBehaviour lifeline)
    {
        if (_lifelineUpdateSettingResolved) return _lifelineUpdateSettingMethod;
        _lifelineUpdateSettingResolved = true;

        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(lifeline.Pointer);
            // Instance, 2 parameters: public void UpdatePlacedPitonClimbingSetting(Piton, ClimbingV2PawnController).
            IntPtr iter = IntPtr.Zero;
            while (true)
            {
                var m = IL2CPP.il2cpp_class_get_methods(klass, ref iter);
                if (m == IntPtr.Zero) break;
                var namePtr = IL2CPP.il2cpp_method_get_name(m);
                if (namePtr == IntPtr.Zero) continue;
                var name = Marshal.PtrToStringAnsi(namePtr);
                if (name != "UpdatePlacedPitonClimbingSetting") continue;
                if (IL2CPP.il2cpp_method_get_param_count(m) != 2) continue;
                _lifelineUpdateSettingMethod = m;
                Mod.LogDebug("[Piton] Resolved Lifeline.UpdatePlacedPitonClimbingSetting(Piton, ClimbingV2PawnController)");
                break;
            }

            if (_lifelineUpdateSettingMethod == IntPtr.Zero)
                Mod.Log.Warning("[Piton] Lifeline.UpdatePlacedPitonClimbingSetting not found — remote pitons stay save-unsafe");
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[Piton] UpdatePlacedPitonClimbingSetting lookup failed: {ex.Message}");
        }

        return _lifelineUpdateSettingMethod;
    }

    private static bool TryGetLastPitonPointer(out IntPtr pitonPtr)
    {
        pitonPtr = IntPtr.Zero;
        if (!TryGetPlacedPitonItems(out var itemsArrayPtr, out var count) || count <= 0)
            return false;

        int headerSize = 4 * IntPtr.Size;
        IntPtr lastItemPtr = *(IntPtr*)((byte*)itemsArrayPtr + headerSize + (count - 1) * IntPtr.Size);
        return TryReadPitonPointer(lastItemPtr, out pitonPtr);
    }

    private static bool TryCollectCurrentPitonPointers(out HashSet<IntPtr> pointers)
    {
        pointers = new HashSet<IntPtr>();
        if (!TryGetPlacedPitonItems(out var itemsArrayPtr, out var count))
            return false;

        int headerSize = 4 * IntPtr.Size;
        for (int i = 0; i < count; i++)
        {
            IntPtr placedDataPtr = *(IntPtr*)((byte*)itemsArrayPtr + headerSize + i * IntPtr.Size);
            if (TryReadPitonPointer(placedDataPtr, out var pitonPtr))
                pointers.Add(pitonPtr);
        }

        return true;
    }

    private static bool TryGetPlacedPitonItems(out IntPtr itemsArrayPtr, out int count)
    {
        itemsArrayPtr = IntPtr.Zero;
        count = 0;

        var lifeline = TryGetLifeline();
        if (lifeline == null) return false;

        var klass = IL2CPP.il2cpp_object_get_class(lifeline.Pointer);
        var field = IL2CPP.GetIl2CppField(klass, "<PlacedPitons>k__BackingField");
        if (field == IntPtr.Zero)
            field = IL2CPP.GetIl2CppField(klass, "PlacedPitons");
        if (field == IntPtr.Zero) return false;

        int offset = (int)IL2CPP.il2cpp_field_get_offset(field);
        IntPtr listPtr = *(IntPtr*)((byte*)lifeline.Pointer + offset);
        if (listPtr == IntPtr.Zero) return false;

        int sizeOffset = 3 * IntPtr.Size;
        count = *(int*)((byte*)listPtr + sizeOffset);
        if (count < 0) return false;

        itemsArrayPtr = *(IntPtr*)((byte*)listPtr + 2 * IntPtr.Size);
        return itemsArrayPtr != IntPtr.Zero;
    }

    private static bool TryReadPitonPointer(IntPtr placedDataPtr, out IntPtr pitonPtr)
    {
        pitonPtr = IntPtr.Zero;
        if (placedDataPtr == IntPtr.Zero) return false;

        var pdKlass = IL2CPP.il2cpp_object_get_class(placedDataPtr);
        var pitonField = IL2CPP.GetIl2CppField(pdKlass, "<Piton>k__BackingField");
        if (pitonField == IntPtr.Zero)
            pitonField = IL2CPP.GetIl2CppField(pdKlass, "piton");
        if (pitonField == IntPtr.Zero) return false;

        int pitonOffset = (int)IL2CPP.il2cpp_field_get_offset(pitonField);
        pitonPtr = *(IntPtr*)((byte*)placedDataPtr + pitonOffset);
        return pitonPtr != IntPtr.Zero;
    }
}
