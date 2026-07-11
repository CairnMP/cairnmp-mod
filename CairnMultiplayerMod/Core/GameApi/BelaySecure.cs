using System;
using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Encordement cordee entre joueurs — systeme repris d'Episure (corde NATIVE, pas de corde
/// cosmetique separee). Principe :
///
/// 1. On capture un TEMPLATE de piton (premier Piton de la scene). On le CLONE
///    (Object.Instantiate) pour chaque partenaire encorde — Instantiate ne joue PAS le son de
///    pose (contrairement a Lifeline.AddPiton, qui spammait des « clac »).
/// 2. Le piton-ancre est rendu kinematic + non ramassable, puis TELEPORTE sur le baudrier du
///    partenaire chaque frame (le piton suit le partenaire).
/// 3. On attache la corde de la lifeline LOCALE a ce piton via Lifeline.AttachToPiton (une
///    seule fois). La corde native devient donc le visuel ET l'assurage : si le joueur chute,
///    la lifeline le retient nativement (suspension, pas de mort/drain).
///
/// Le systeme est SYMETRIQUE : chaque client attache SA propre corde a un piton pose chez le
/// partenaire. Les deux joueurs voient donc une corde, sans rien partager au reseau (les
/// positions sont deja synchronisees). Plus besoin de corde cosmetique (RopeLinkRenderer).
/// </summary>
public static unsafe partial class CairnGameApi
{
    /// <summary>Template clone pour chaque ancre. Capture par PatchPitonTemplate (Awake) ou scan.</summary>
    private static GameObject _pitonTemplate;
    private static int _lastPitonScanFrame;

    /// <summary>Une ancre (piton clone) par partenaire encorde.</summary>
    private static readonly Dictionary<int, GameObject> _ropeAnchors = new();

    /// <summary>Partenaires dont la corde de lifeline locale a deja ete clippee (AttachToPiton fait).</summary>
    private static readonly HashSet<int> _ropeAnchorsAttached = new();

    /// <summary>Vrai tant qu'au moins une ancre de cordee est active (filet RopeTeamFallPatch).</summary>
    private static bool _belayEngaged;
    public static bool IsNativeBelayEngaged => _belayEngaged;

    /// <summary>Vrai si au moins une ancre de cordee est posee (utilise par TickRopeTeam).</summary>
    public static bool HasRopeTeamAnchors => _ropeAnchors.Count > 0;

    /// <summary>Capture un template de piton (appele depuis le patch Piton.Awake).</summary>
    public static void CapturePitonTemplate(Piton candidate)
    {
        if (_pitonTemplate != null || candidate == null) return;
        try
        {
            _pitonTemplate = candidate.gameObject;
            Mod.LogDebug("[RopeTeam] Piton template captured (Awake).");
        }
        catch { }
    }

    /// <summary>Template de piton ; scan throttle de la scene en repli si l'Awake n'a rien capture.</summary>
    private static GameObject ResolvePitonTemplate()
    {
        if (_pitonTemplate != null) return _pitonTemplate;
        if (_lastPitonScanFrame != 0 && Time.frameCount - _lastPitonScanFrame < 30) return null;
        _lastPitonScanFrame = Time.frameCount;
        try
        {
            var all = Resources.FindObjectsOfTypeAll<Piton>();
            if (all != null && all.Length > 0 && all[0] != null)
            {
                _pitonTemplate = all[0].gameObject;
                Mod.LogDebug($"[RopeTeam] Piton template captured via scan ('{_pitonTemplate.name}').");
            }
        }
        catch (Exception ex) { Mod.Log.Warning($"[RopeTeam] piton template scan failed: {ex.Message}"); }
        return _pitonTemplate;
    }

    /// <summary>
    /// Entretient l'ancre de corde vers <paramref name="partnerId"/> : la cree au besoin (clone du
    /// template), clippe la corde de la lifeline locale dessus une fois, puis la DEPLACE sur le
    /// baudrier du partenaire chaque frame. A appeler chaque frame tant que le lien est actif.
    /// </summary>
    public static void UpdateRopeTeamAnchor(int partnerId, Vector3 partnerAnchorPos)
    {
        try
        {
            // Cree l'ancre au premier appel (clone du template -> aucun son de pose).
            if (!_ropeAnchors.TryGetValue(partnerId, out var anchor) || anchor == null)
            {
                var template = ResolvePitonTemplate();
                if (template == null) return;   // pas encore de template -> on reessaie plus tard
                anchor = SpawnAnchorPiton(template, partnerAnchorPos);
                if (anchor == null) return;
                _ropeAnchors[partnerId] = anchor;
                _belayEngaged = true;
                Mod.LogDebug($"[RopeTeam] Rope anchor spawned for partner {partnerId}.");
            }

            // Suit le partenaire : piton + rigidbodies des extremites de quickdraw (la corde Obi
            // pinnee sur le collider du piton suit -> ancre mobile).
            MoveAnchorPiton(anchor, partnerAnchorPos);

            // Clippe la corde de la lifeline locale dans l'ancre (une seule fois). Si l'attache
            // n'a pas encore pris (corde du robot pas dispo, etc.), on retentera la frame suivante.
            if (!_ropeAnchorsAttached.Contains(partnerId) && TryAttachLifelineToAnchor(anchor))
            {
                _ropeAnchorsAttached.Add(partnerId);
                Mod.LogDebug($"[RopeTeam] Clipped local lifeline into anchor for partner {partnerId}.");
            }
        }
        catch (Exception ex) { Mod.Log.Warning($"[RopeTeam] anchor update failed: {ex.Message}"); }
    }

    private static GameObject SpawnAnchorPiton(GameObject template, Vector3 pos)
    {
        var go = Object.Instantiate(template, pos, Quaternion.identity);
        Object.DontDestroyOnLoad(go);
        var piton = go.GetComponent<Piton>();
        if (piton != null)
        {
            // Ancre non ramassable (cf. Episure : *(sbyte*)(Piton+32)=0 == canBePickedUp=false).
            try { piton.canBePickedUp = false; } catch { }
            // Kinematic : l'ancre suit la position imposee sans tomber ni etre tiree par la tension.
            SetKinematic(piton.RigidBody);
            SetKinematic(piton.quickdrawBeginRigidBody);
            SetKinematic(piton.quickdrawEndRigidBody);
        }
        // L'ancre n'est qu'un point d'accroche mecanique pour la corde : on cache son visuel
        // (mesh du piton + degaine) pour ne pas laisser un piton flottant dans le vide chez le
        // partenaire. Le composant Piton, ses rigidbodies et colliders restent actifs -> la
        // corde s'y attache et suit toujours. La corde coop (lifeline.securingRope) est un
        // objet separe, donc reste visible.
        HideAnchorRenderers(go);
        return go;
    }

    /// <summary>Desactive tous les Renderer du clone d'ancre (visuel piton invisible).</summary>
    private static void HideAnchorRenderers(GameObject go)
    {
        try
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers == null) return;
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null) renderers[i].enabled = false;
        }
        catch (Exception ex) { Mod.Log.Warning($"[RopeTeam] hide anchor renderers failed: {ex.Message}"); }
    }

    private static void SetKinematic(Rigidbody rb)
    {
        try { if (rb != null) rb.isKinematic = true; } catch { }
    }

    private static void MoveAnchorPiton(GameObject anchor, Vector3 pos)
    {
        anchor.transform.position = pos;
        var piton = anchor.GetComponent<Piton>();
        if (piton == null) return;
        try { if (piton.quickdrawBeginRigidBody != null) piton.quickdrawBeginRigidBody.transform.position = pos; } catch { }
        try { if (piton.quickdrawEndRigidBody != null) piton.quickdrawEndRigidBody.transform.position = pos; } catch { }
    }

    private static bool TryAttachLifelineToAnchor(GameObject anchor)
    {
        var harness = ResolveLocalHarness();
        var lifeline = harness != null ? harness.lifeline : null;
        if (lifeline == null) return false;
        var pawn = lifeline.pawn;
        if (pawn == null) return false;
        var piton = anchor.GetComponent<Piton>();
        if (piton == null) return false;

        // La corde native (securingRope) doit exister pour qu'AttachToPiton ait quelque chose a
        // clipper. Au repos elle est null -> on INJECTE la corde du compagnon robot
        // (Il2CppTheGameBakers.Cairn.RobotPawnController.GetRope()) dans la lifeline, exactement comme Episure. Throttle
        // interne (ResolveLocalClimbot) -> pas de spam si la corde n'est pas encore dispo.
        if (lifeline.securingRope == null && !TryInjectSecuringRope(lifeline))
            return false;

        try
        {
            lifeline.AttachToPiton(piton, pawn, true);
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[RopeTeam] AttachToPiton failed: {ex.Message}");
            return false;
        }
    }

    private static Il2CppTheGameBakers.Cairn.RobotPawnController _localClimbotCached;
    private static int _lastClimbotSearchFrame;

    /// <summary>
    /// Injecte la corde du compagnon robot (Il2CppTheGameBakers.Cairn.RobotPawnController.GetRope()) comme securingRope de
    /// la lifeline si celle-ci est vide (repris d'Episure : write du champ securingRope). Renvoie
    /// true si la lifeline a desormais une corde.
    /// </summary>
    private static bool TryInjectSecuringRope(Lifeline lifeline)
    {
        var climbot = ResolveLocalClimbot();
        if (climbot == null) return false;

        LogicalRope rope = null;
        try { rope = climbot.GetRope(); } catch { }
        if (rope == null) return false;

        try
        {
            var go = rope.gameObject;
            if (go != null) go.SetActive(true);
            lifeline.securingRope = rope;
            Mod.LogDebug("[RopeTeam] Injected climbot rope into lifeline.securingRope.");
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[RopeTeam] securingRope inject failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Trouve (et cache) le Il2CppTheGameBakers.Cairn.RobotPawnController local. Les robots distants sont des
    /// NetplayRemoteClimbot (type different) -> FindObjectsOfType ne renvoie que le local.</summary>
    private static Il2CppTheGameBakers.Cairn.RobotPawnController ResolveLocalClimbot()
    {
        if (_localClimbotCached != null) return _localClimbotCached;
        if (_lastClimbotSearchFrame != 0 && Time.frameCount - _lastClimbotSearchFrame < 30) return null;
        _lastClimbotSearchFrame = Time.frameCount;
        try
        {
            var all = Object.FindObjectsOfType<Il2CppTheGameBakers.Cairn.RobotPawnController>();
            if (all != null)
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null) { _localClimbotCached = all[i]; Mod.LogDebug("[RopeTeam] Local climbot resolved."); break; }
        }
        catch (Exception ex) { Mod.Log.Warning($"[RopeTeam] climbot search failed: {ex.Message}"); }
        return _localClimbotCached;
    }

    /// <summary>Retire l'ancre de corde vers un partenaire (decordage / partenaire parti).</summary>
    public static void ReleaseRopeTeamAnchor(int partnerId)
    {
        if (_ropeAnchors.TryGetValue(partnerId, out var anchor))
        {
            DestroyAnchor(anchor);
            _ropeAnchors.Remove(partnerId);
            Mod.LogDebug($"[RopeTeam] Rope anchor released for partner {partnerId}.");
        }
        _ropeAnchorsAttached.Remove(partnerId);
        if (_ropeAnchors.Count == 0) _belayEngaged = false;
    }

    /// <summary>Retire TOUTES les ancres de cordee (deconnexion / changement de scene).</summary>
    public static void ReleaseAllRopeTeamAnchors()
    {
        if (_ropeAnchors.Count == 0) { _belayEngaged = false; _ropeAnchorsAttached.Clear(); return; }
        foreach (var kv in _ropeAnchors)
            DestroyAnchor(kv.Value);
        _ropeAnchors.Clear();
        _ropeAnchorsAttached.Clear();
        _belayEngaged = false;
        Mod.LogDebug("[RopeTeam] All rope anchors released.");
    }

    private static void DestroyAnchor(GameObject anchor)
    {
        if (anchor == null) return;
        try
        {
            // Detache proprement la corde du piton avant destruction (evite de laisser la
            // securingRope de la lifeline pinnee sur un collider detruit).
            var piton = anchor.GetComponent<Piton>();
            if (piton != null) { try { piton.Detach(); } catch { } }
        }
        catch { }
        try { Object.Destroy(anchor); } catch { }
    }
}
