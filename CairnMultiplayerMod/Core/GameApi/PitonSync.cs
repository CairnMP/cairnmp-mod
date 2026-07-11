using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

public static unsafe partial class CairnGameApi
{
    private static MonoBehaviour _lifelineCached;
    private static int _lastLifelineSearchFrame;
    private static int _lastKnownPitonCount;
    private static uint _nextPitonNetId = 1;
    private static int _remotePitonsAdded; // combien de pitons on a fait apparaitre via AddPiton (a ignorer lors de la detection)
    private static readonly Dictionary<IntPtr, uint> _localPitonIdsByPointer = new();
    private static readonly Dictionary<uint, GameObject> _remotePitonsByNetId = new();
    // Pointeur IL2CPP du composant Piton — sert a appeler Lifeline.DetachPiton
    // qui retire proprement le piton de la corde et destroy le visuel (vs juste
    // Object.Destroy sur la GameObject qui peut laisser une ref pendante).
    private static readonly Dictionary<uint, IntPtr> _remotePitonPointersByNetId = new();
    private static IntPtr _lifelineDetachPitonMethod;
    private static bool _lifelineDetachPitonResolved;
    private static float _lastPitonCheckErrorLogAt;
    private const float PitonCheckErrorLogIntervalSeconds = 5f;
    // ClimbingV2PawnController LOCAL — sert a renseigner le ClimbingSetting des pitons
    // distants (spawnes avec ClimbingSetting=null) via UpdatePlacedPitonClimbingSetting.
    private static Il2Cpp.ClimbingV2PawnController _localClimbControllerCached;
    private static int _lastClimbControllerSearchFrame;
    private static IntPtr _lifelineUpdateSettingMethod;
    private static bool _lifelineUpdateSettingResolved;

    public static MonoBehaviour TryGetLifeline()
        => FindMonoBehaviourByName("Lifeline", ref _lifelineCached, ref _lastLifelineSearchFrame);

    /// <summary>
    /// Verifie si de nouveaux pitons ont ete places depuis le dernier appel. Retourne
    /// la position monde, la rotation et la qualite du nouveau piton via les parametres out.
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

            // List<T> Il2Cpp a _size a un offset connu. On le lit.
            // Layout de List<T> : [klass, monitor, _items (ptr tableau), _size (int), _version (int)]
            // _items est a l'offset 2*IntPtr.Size, _size a 2*IntPtr.Size + IntPtr.Size
            int sizeOffset = 3 * IntPtr.Size;
            int currentCount = *(int*)((byte*)listPtr + sizeOffset);

            if (currentCount <= _lastKnownPitonCount)
            {
                _lastKnownPitonCount = currentCount;
                return false;
            }

            // Verifie si cette augmentation est causee par un spawn distant qu'on a
            // fait nous-memes. Si oui, on met juste a jour le compteur et on passe.
            int newPitons = currentCount - _lastKnownPitonCount;
            if (_remotePitonsAdded >= newPitons)
            {
                _remotePitonsAdded -= newPitons;
                _lastKnownPitonCount = currentCount;
                return false;
            }
            _remotePitonsAdded = 0;

            // Nouveau(x) piton(s) LOCAL(aux) ajoute(s). Lit le dernier depuis le tableau _items.
            _lastKnownPitonCount = currentCount;
            IntPtr itemsArrayPtr = *(IntPtr*)((byte*)listPtr + 2 * IntPtr.Size);
            if (itemsArrayPtr == IntPtr.Zero) return false;

            // Le tableau items est un Il2CppArray de references PlacedPitonData.
            int headerSize = 4 * IntPtr.Size;
            IntPtr lastItemPtr = *(IntPtr*)((byte*)itemsArrayPtr + headerSize + (currentCount - 1) * IntPtr.Size);
            if (lastItemPtr == IntPtr.Zero) return false;

            // PlacedPitonData a un champ Piton (premier backing field).
            var pdKlass = IL2CPP.il2cpp_object_get_class(lastItemPtr);
            var pitonField = IL2CPP.GetIl2CppField(pdKlass, "<Piton>k__BackingField");
            if (pitonField == IntPtr.Zero)
                pitonField = IL2CPP.GetIl2CppField(pdKlass, "piton");
            if (pitonField == IntPtr.Zero) return false;

            int pitonOffset = (int)IL2CPP.il2cpp_field_get_offset(pitonField);
            IntPtr pitonPtr = *(IntPtr*)((byte*)lastItemPtr + pitonOffset);
            if (pitonPtr == IntPtr.Zero) return false;

            // Lit le transform du MonoBehaviour Piton pour la position/rotation.
            // Le pointeur peut etre stale (entrée detruite encore dans la liste apres
            // une suppression / reload de save). On valide avant de toucher transform
            // pour eviter de spammer des NRE IL2CPP qui plombent le frame.
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
                // Piton fantome : on absorbe sans logger pour pas spammer.
                return false;
            }

            // Lit le champ pitonHp.
            var pitonKlass = IL2CPP.il2cpp_object_get_class(pitonPtr);
            var hpField = IL2CPP.GetIl2CppField(pitonKlass, "pitonHp");
            if (hpField != IntPtr.Zero)
            {
                int hpOff = (int)IL2CPP.il2cpp_field_get_offset(hpField);
                hp = *(int*)((byte*)pitonPtr + hpOff);
            }

            // Lit executionQuality (enum, taille int).
            var qualField = IL2CPP.GetIl2CppField(pitonKlass, "executionQuality");
            if (qualField != IntPtr.Zero)
            {
                int qualOff = (int)IL2CPP.il2cpp_field_get_offset(qualField);
                quality = (byte)(*(int*)((byte*)pitonPtr + qualOff));
            }

            // Lit ItemId (InventoryItemStringId -- struct enveloppant un seul int).
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
            // Rate-limite : la liste peut etre transitoirement incoherente (reload, pickup),
            // pas la peine de noyer le log + alourdir le frame avec des stacktraces IL2CPP.
            var now = Time.unscaledTime;
            if (now - _lastPitonCheckErrorLogAt >= PitonCheckErrorLogIntervalSeconds)
            {
                _lastPitonCheckErrorLogAt = now;
                Mod.Log.Warning($"[Piton] CheckForNewPiton failed (rate-limited): {ex.GetType().Name}: {FirstLine(ex.Message)}");
            }
            return false;
        }
    }

    /// <summary>
    /// Detecte la suppression d'un piton local deja annonce au reseau.
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
    /// Fait apparaitre un piton sur le client distant en appelant Lifeline.AddPiton()
    /// via invocation IL2CPP runtime. Enregistre correctement le piton dans le systeme
    /// de corde pour que les degaines et l'accroche de corde fonctionnent correctement.
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

            // Trouve la methode AddPiton avec 6 parametres. On veut la surcharge :
            // AddPiton(Vector3, Quaternion, PitonExecutionQuality, int, InventoryItemStringId, ClimbingSetting)
            // ou ClimbingSetting est un type reference qu'on peut passer a null.
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
                    // Verifie que le dernier parametre est un type reference (classe ClimbingSetting,
                    // pas ClimbingV2PawnController). Les deux sont des types reference, mais on prend
                    // le SECOND match (la surcharge ClimbingSetting est declaree apres celle du
                    // Controller dans la decompilation).
                    method = m;
                    // Continue d'iterer pour obtenir la DERNIERE surcharge a 6 parametres.
                }
            }

            if (method == IntPtr.Zero)
            {
                Mod.Log.Error("[Piton] Lifeline.AddPiton method not found");
                return false;
            }

            // Prepare les arguments pour il2cpp_runtime_invoke.
            // Les types valeur sont passes comme pointeurs vers leurs donnees.
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

            // Marque qu'on est sur le point d'ajouter un piton nous-memes pour que
            // CheckForNewPiton ignore l'augmentation de compteur resultante.
            _remotePitonsAdded++;

            IntPtr exception = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, lifeline.Pointer, (void**)args, ref exception);

            if (exception != IntPtr.Zero)
            {
                Mod.Log.Error($"[Piton] AddPiton threw an exception");
                return false;
            }

            // Le piton natif vient d'etre ajoute avec ClimbingSetting=null (6e arg d'AddPiton).
            // Un piton au ClimbingSetting null fait planter Lifeline.Update() et
            // Piton.WriteToSavegame (NRE natif) des qu'une corde s'y attache -> sauvegarde
            // avortee. On backfill le setting avec le controller de grimpe LOCAL.
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
        // 1) Voie privilegiee : Lifeline.DetachPiton(piton) — retire de la corde,
        //    detache des lifelines et destroy la GameObject vanille.
        if (_remotePitonPointersByNetId.TryGetValue(netId, out var pitonPtr) && pitonPtr != IntPtr.Zero)
        {
            _remotePitonPointersByNetId.Remove(netId);
            _remotePitonsByNetId.Remove(netId);

            if (TryDetachPitonViaLifeline(pitonPtr))
            {
                Mod.LogDebug($"[Piton] Detached remote piton #{netId} via Lifeline.DetachPiton");
                return true;
            }

            // Fallback : destroy direct sur la GameObject capturee.
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

        // 2) Fallback historique : on n'avait stocke que la GameObject.
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
            // On cherche la surcharge instance (non statique) avec 1 parametre :
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
    /// Renseigne le ClimbingSetting du dernier piton place (spawn distant) avec le
    /// ClimbingV2PawnController LOCAL, via Lifeline.UpdatePlacedPitonClimbingSetting.
    /// Sans ca le ClimbingSetting reste null -> NRE natif dans Lifeline.Update() et
    /// Piton.WriteToSavegame quand on mousquetonne le piton -> sauvegarde cassee.
    /// Best-effort : si le controller local est introuvable on laisse le piton tel quel.
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

    /// <summary>Trouve (et cache) le ClimbingV2PawnController LOCAL. Les joueurs distants
    /// sont des NetplayRemotePlayer (type different) -> FindObjectsOfType ne renvoie que le local.</summary>
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
            // Instance, 2 parametres : public void UpdatePlacedPitonClimbingSetting(Piton, ClimbingV2PawnController).
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
