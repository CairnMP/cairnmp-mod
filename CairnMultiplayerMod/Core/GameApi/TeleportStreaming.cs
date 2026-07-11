using System;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Téléportation longue distance entre zones. Cairn streame le monde par ZONES. Un simple
/// `transform.position` lointain dépose le perso dans une zone non chargée -> chute dans le vide,
/// puis le streaming charge sauvagement TOUTES les zones traversées (origine + intermédiaires +
/// cible restent chargées) -> chute de FPS + repositionnement -> on n'arrive pas chez le joueur.
///
/// Solution « comme le jeu » :
/// - Même zone (cible dans la zone courante) -> téléport direct INSTANTANÉ (aucun chargement).
/// - Zone différente -> TRAVEL géré (CairnSceneManager.TravelToZone) : charge la zone cible ET
///   décharge proprement l'origine (écran de chargement), puis on RÉ-APPLIQUE la position exacte
///   une fois le monde idle (settle), pour atterrir pile chez le joueur.
///
/// L'« idle » est détecté via CairnSceneManager.IsLoadingOrUnloadingScenes + StreamingManager
/// .PreloadingZone (PAS IsZoneLoaded, qui passe true trop tôt).
/// </summary>
public static unsafe partial class CairnGameApi
{
    private const float TeleportSettleTimeoutSeconds = 40f;   // failsafe global
    private const float TeleportStableDistance = 3f;          // tolérance "sur la cible"
    private const float TeleportStableSeconds = 1.5f;         // durée idle+stable avant de lâcher

    private static bool _teleportPending;
    private static Vector3 _teleportTarget;
    private static float _teleportYaw;
    private static float _teleportDeadline;
    private static float _teleportStableSince;   // -1 = pas (encore) stable

    private static StreamingManager _streamingManagerCached;
    private static int _lastStreamingManagerSearchFrame;
    private static CairnSceneManager _sceneManagerCached;
    private static int _lastSceneManagerSearchFrame;

    private static StreamingManager TryGetStreamingManager()
    {
        if (_streamingManagerCached != null) return _streamingManagerCached;
        if (_lastStreamingManagerSearchFrame != 0 && Time.frameCount - _lastStreamingManagerSearchFrame < 60) return null;
        _lastStreamingManagerSearchFrame = Time.frameCount;
        try { _streamingManagerCached = MoSingleton<StreamingManager>.Instance; }
        catch { _streamingManagerCached = null; }
        return _streamingManagerCached;
    }

    private static CairnSceneManager TryGetSceneManager()
    {
        if (_sceneManagerCached != null) return _sceneManagerCached;
        if (_lastSceneManagerSearchFrame != 0 && Time.frameCount - _lastSceneManagerSearchFrame < 60) return null;
        _lastSceneManagerSearchFrame = Time.frameCount;
        try { _sceneManagerCached = MoSingleton<CairnSceneManager>.Instance; }
        catch { _sceneManagerCached = null; }
        return _sceneManagerCached;
    }

    /// <summary>
    /// Si la cible est dans une AUTRE zone que la zone courante, lance un travel géré + arme le
    /// settle pour poser la position exacte ensuite, et renvoie true. Renvoie false si la cible
    /// est dans la zone courante (ou info indispo) -> l'appelant fait un téléport direct instantané.
    /// </summary>
    private static bool TryTeleportAcrossZones(Vector3 pos, float yawDeg)
    {
        try
        {
            var sm = TryGetStreamingManager();
            var cm = TryGetSceneManager();
            if (sm == null || cm == null) return false;

            ZoneSceneData targetZone = null, currentZone = null;
            try { targetZone = sm.GetBestZoneAt(pos); } catch { }
            try { currentZone = sm.CurrentZone; } catch { }
            if (targetZone == null || currentZone == null) return false;

            // Même zone -> pas de chargement, l'appelant fera un téléport direct.
            if (targetZone.Pointer == currentZone.Pointer) return false;

            var world = sm.World;
            if (world == null) return false;

            Mod.LogDebug("[Teleport] Target in another zone -> travelling (loading)...");
            cm.TravelToZone(targetZone, world);

            // Pose la position exacte une fois le monde idle (le travel place d'abord au spawn de zone).
            ArmTeleportSettle(pos, yawDeg);
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[Teleport] cross-zone travel failed ({ex.Message}) — falling back to direct teleport.");
            return false;
        }
    }

    /// <summary>Arme le settle : ré-applique la position cible jusqu'à ce que le monde soit idle.</summary>
    private static void ArmTeleportSettle(Vector3 pos, float yawDeg)
    {
        _teleportTarget = pos;
        _teleportYaw = yawDeg;
        _teleportPending = true;
        _teleportStableSince = -1f;
        _teleportDeadline = Time.time + TeleportSettleTimeoutSeconds;
        Mod.LogDebug("[Teleport] Settling to exact target after zone load...");
    }

    /// <summary>Vrai si le monde est en train de (dé)charger des scènes / préparer une zone.</summary>
    private static bool IsWorldStreamingBusy()
    {
        try
        {
            var sm = TryGetStreamingManager();
            if (sm != null)
            {
                try { if (sm.PreloadingZone != null) return true; } catch { }
            }
            var cm = TryGetSceneManager();
            if (cm != null)
            {
                try { if (cm.IsLoadingOrUnloadingScenes) return true; } catch { }
                try { if (cm.IsTraveling) return true; } catch { }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// À appeler chaque frame depuis Mod.OnUpdate. Pour un travel inter-zones : une fois le monde
    /// idle, pose la position exacte (le travel place d'abord au spawn de zone) et maintient jusqu'à
    /// stabilité. No-op si rien en attente.
    /// </summary>
    public static void TickTeleportSettle(bool inGame)
    {
        if (!_teleportPending) return;
        try
        {
            if (Time.time >= _teleportDeadline)
            {
                _teleportPending = false;
                Mod.Log.Warning("[Teleport] Settle timed out. Released.");
                return;
            }

            // Tant que le monde charge (ou pas en jeu), on attend — pas de jeu avec la position.
            if (!inGame || IsWorldStreamingBusy()) { _teleportStableSince = -1f; return; }

            var go = TryGetLocalMCGameObject();
            if (go == null) { _teleportStableSince = -1f; return; }

            var t = go.transform;
            float dist = Vector3.Distance(t.position, _teleportTarget);

            // Monde idle + en jeu : on pose la position exacte si le travel nous a mis ailleurs.
            if (dist > TeleportStableDistance)
            {
                t.position = _teleportTarget;
                var e = t.eulerAngles;
                t.eulerAngles = new Vector3(e.x, _teleportYaw, e.z);
                _teleportStableSince = -1f;
                return;
            }

            // Sur la cible, monde idle, en jeu -> on confirme la stabilité avant de lâcher.
            if (_teleportStableSince < 0f) _teleportStableSince = Time.time;
            else if (Time.time - _teleportStableSince >= TeleportStableSeconds)
            {
                _teleportPending = false;
                Mod.LogDebug("[Teleport] Settled at target.");
            }
        }
        catch (Exception ex)
        {
            _teleportPending = false;
            _teleportStableSince = -1f;
            Mod.Log.Warning($"[Teleport] settle tick failed: {ex.Message}");
        }
    }
}
