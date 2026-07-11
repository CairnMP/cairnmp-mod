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
    /// Retourne la position monde + yaw du GameObject MC du joueur local,
    /// ou `false` si le MC n'a pas encore été instancié (on est encore dans un menu/cinématique).
    /// </summary>
    public static bool TryGetLocalPlayerPose(out Vector3 position, out float yaw)
    {
        position = default;
        yaw = 0f;

        var pm = FindPawnManager();
        if (pm == null) return false;

        // Résout l'offset du backing field de MCGameObject une seule fois. Il2CppInterop
        // n'expose que les vrais champs (pas les propriétés C#), donc on accède directement
        // au backing field généré par le compilateur.
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
            // MC pas encore apparu — toujours en cinématique/chargement. Journalise un
            // battement de coeur toutes les ~3 secondes pour savoir qu'on interroge.
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

        // Journalise la première résolution réussie — moment important, on veut le voir.
        if (!_mcResolvedOnce)
        {
            _mcResolvedOnce = true;
            Mod.LogDebug($"[CairnGameApi] MC transform RESOLVED @ ({position.x:F1}, {position.y:F1}, {position.z:F1})  after {_nullReadCount} null reads");
        }
        return true;
    }

    private static MonoBehaviour FindPawnManager() => FindMonoBehaviourByName("PawnManager", ref _pawnManagerCached, ref _lastPawnManagerSearchFrame);

    /// <summary>
    /// Retourne le GameObject MC du joueur local lui-même (pas un clone).
    /// Les appelants peuvent utiliser `Object.Instantiate` pour obtenir un clone rendu
    /// qui utilise exactement la même configuration de pipeline de rendu que le vrai
    /// MC — contourne tous les problèmes de SkinnedMeshRenderer / shader custom /
    /// passe de rendu rencontrés lors de l'instanciation de NetplayClimberPrefab.
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
    /// Deplace le personnage local (MC) vers <paramref name="position"/> et oriente
    /// son yaw. Sert aux commandes admin : /tp (l'hote se deplace localement vers un
    /// joueur) et /bring (un client recoit un ServerTeleport et s'y deplace).
    /// Retourne false si le MC n'est pas encore instancie (menu/cinematique).
    /// </summary>
    public static bool TeleportLocalPlayer(Vector3 position, float yawDeg)
    {
        var go = TryGetLocalMCGameObject();
        if (go == null) return false;

        // Zone cible differente de la zone courante ? -> on fait comme le jeu : un TRAVEL gere
        // (chargement de la zone cible + dechargement propre de l'origine), puis on pose la
        // position exacte une fois le monde idle. Sinon (meme zone), teleport direct instantane.
        // Voir TeleportStreaming.cs.
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
