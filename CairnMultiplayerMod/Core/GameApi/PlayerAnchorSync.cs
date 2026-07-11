using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Encordement entre joueurs (assurage coop) — SPIKE physique local (etape 1).
///
/// But du spike : valider que poser un piton (Lifeline.AddPiton) a la position
/// d'un autre joueur et le deplacer chaque frame produit une corde / un blocage
/// de distance corrects. Aucun reseau ici : on clippe localement une ancre sur
/// la position (deja synchronisee) d'un fantome et on observe le comportement.
///
/// On reutilise la machinerie piton existante (PitonSync) : SpawnRemotePiton
/// pose un piton SANS le rediffuser (il incremente _remotePitonsAdded pour que
/// CheckForNewPiton l'ignore), TryGetLastPitonPointer capture son pointeur, et
/// TryDetachPitonViaLifeline le retire proprement.
///
/// Robustesse : tout en try/catch, no-op si Lifeline/piton absent, jamais de crash.
/// </summary>
public static unsafe partial class CairnGameApi
{
    private static IntPtr _anchorPitonPtr;

    // Offsets des rigidbodies du Piton (resolus une fois ; -1 = absent). Episure rend
    // CINEMATIQUES les trois (racine RigidBody + quickdrawBegin/End) et les colle sur
    // la racine du corps chaque frame. Ne deplacer que le transform du piton laissait
    // les extremites de corde diverger ET la racine non-cinematique se faisait bousculer
    // par la physique (jitter/derive).
    private static bool _pitonRbFieldsResolved;
    private static int _pitonRootRbOffset = -1;
    private static int _quickdrawBeginOffset = -1;
    private static int _quickdrawEndOffset = -1;

    public static bool IsAnchorActive => _anchorPitonPtr != IntPtr.Zero;

    /// <summary>Pose une ancre (piton local non rediffuse) a la position donnee.</summary>
    public static bool ClipAnchorTo(Vector3 position)
    {
        ReleaseAnchor();

        try
        {
            // Reutilise le spawn de piton local "silencieux" (non rediffuse).
            if (!SpawnRemotePiton(position, Quaternion.identity, quality: 5, hp: 100, itemId: 3))
                return false;

            if (TryGetLastPitonPointer(out var ptr) && ptr != IntPtr.Zero)
            {
                _anchorPitonPtr = ptr;
                // Rend les rigidbodies du piton cinematiques + les colle sur l'ancre
                // (comme Episure), pour que la physique ne les laisse pas derriere.
                PinPitonRigidbodies(position);
                DumpRopeRenderersOnce();
                Mod.LogDebug($"[RopeCouple] Anchor clipped @ ({position.x:F1},{position.y:F1},{position.z:F1})");
                return true;
            }

            Mod.Log.Warning("[RopeCouple] Anchor placed but piton pointer not captured");
            return false;
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[RopeCouple] ClipAnchorTo failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Deplace l'ancre vers la position du partenaire (chaque frame).</summary>
    public static void UpdateAnchor(Vector3 position)
    {
        if (_anchorPitonPtr == IntPtr.Zero) return;

        try
        {
            var t = new MonoBehaviour(_anchorPitonPtr).transform;
            if (t == null || t.Pointer == IntPtr.Zero)
            {
                _anchorPitonPtr = IntPtr.Zero; // pointeur stale (piton detruit)
                return;
            }
            t.position = position;
            // Garde la racine du piton + les extremites de corde coincidentes avec
            // l'ancre, sinon la corde native s'etire vers leur ancienne position.
            PinPitonRigidbodies(position);
        }
        catch
        {
            _anchorPitonPtr = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Rend cinematiques et colle sur <paramref name="position"/> les rigidbodies du
    /// piton (racine + quickdrawBegin/End), comme Episure. Resout les offsets une fois ;
    /// no-op par champ absent. isKinematic est reaffirme chaque frame pour couvrir les
    /// rigidbodies crees par la corde APRES le clip.
    /// </summary>
    private static void PinPitonRigidbodies(Vector3 position)
    {
        if (_anchorPitonPtr == IntPtr.Zero) return;
        try
        {
            if (!_pitonRbFieldsResolved)
            {
                _pitonRbFieldsResolved = true;
                var klass = IL2CPP.il2cpp_object_get_class(_anchorPitonPtr);
                _pitonRootRbOffset = ResolveFieldOffset(klass, "<RigidBody>k__BackingField");
                _quickdrawBeginOffset = ResolveFieldOffset(klass, "quickdrawBeginRigidBody");
                _quickdrawEndOffset = ResolveFieldOffset(klass, "quickdrawEndRigidBody");
            }

            PinBodyAt(_pitonRootRbOffset, position);
            PinBodyAt(_quickdrawBeginOffset, position);
            PinBodyAt(_quickdrawEndOffset, position);
        }
        catch { }
    }

    private static int ResolveFieldOffset(IntPtr klass, string name)
    {
        var f = IL2CPP.GetIl2CppField(klass, name);
        return f == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(f);
    }

    private static void PinBodyAt(int offset, Vector3 position)
    {
        if (offset < 0) return;
        IntPtr bodyPtr = *(IntPtr*)((byte*)_anchorPitonPtr + offset);
        if (bodyPtr == IntPtr.Zero) return;
        var body = new Rigidbody(bodyPtr);
        body.isKinematic = true; // idempotent : couvre les bodies crees apres le clip
        body.position = position;
    }

    // Diagnostic (#4 beam vert) : dump une fois le shader/couleur de tous les LineRenderer
    // de la scene pour identifier la corde rendue en vert vif. A lire dans le log apres
    // qu'une corde distante a buggue.
    private static bool _ropeRenderersDumped;

    private static void DumpRopeRenderersOnce()
    {
        if (_ropeRenderersDumped) return;
        _ropeRenderersDumped = true;
        try
        {
            var renderers = UnityEngine.Object.FindObjectsOfType<LineRenderer>();
            int n = renderers == null ? 0 : renderers.Length;
            Mod.LogDebug($"[RopeDiag] {n} LineRenderer(s) in scene:");
            for (int i = 0; i < n; i++)
            {
                var lr = renderers[i];
                if (lr == null) continue;
                var mat = lr.sharedMaterial;
                var shader = mat != null && mat.shader != null ? mat.shader.name : "<none>";
                Mod.LogDebug($"[RopeDiag] '{lr.gameObject.name}' shader='{shader}' " +
                    $"start={lr.startColor} end={lr.endColor} width={lr.startWidth:F2} points={lr.positionCount}");
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[RopeDiag] dump failed: {ex.Message}");
        }
    }

    /// <summary>Retire l'ancre (detache le piton de la corde).</summary>
    public static void ReleaseAnchor()
    {
        if (_anchorPitonPtr == IntPtr.Zero) return;

        var ptr = _anchorPitonPtr;
        _anchorPitonPtr = IntPtr.Zero;
        try
        {
            TryDetachPitonViaLifeline(ptr);
            Mod.LogDebug("[RopeCouple] Anchor released");
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[RopeCouple] ReleaseAnchor failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Reinitialise l'ancre (changement de scene / deconnexion).</summary>
    public static void ResetPlayerAnchorCache()
    {
        // Le pointeur devient stale au changement de scene : on l'oublie sans
        // tenter un detach (le Lifeline a ete recree).
        _anchorPitonPtr = IntPtr.Zero;
    }
}
