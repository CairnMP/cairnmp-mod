using System;
using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.Features.Roping;

/// <summary>
/// Rope-team belaying between players — system borrowed from Episure (NATIVE rope, no separate
/// cosmetic rope). Principle:
///
/// 1. We capture a piton TEMPLATE (first Piton in the scene). We CLONE it
///    (Object.Instantiate) for each roped partner — Instantiate does NOT play the placement
///    sound (unlike Lifeline.AddPiton, which spammed "clack" sounds).
/// 2. The anchor piton is made kinematic + non-pickable, then TELEPORTED onto the partner's
///    harness every frame (the piton follows the partner).
/// 3. We attach the LOCAL lifeline's rope to that piton via Lifeline.AttachToPiton (only
///    once). The native rope thus becomes both the visual AND the belay: if the player falls,
///    the lifeline catches them natively (suspension, no death/drain).
///
/// The system is SYMMETRIC: each client attaches ITS OWN rope to a piton placed on the
/// partner. Both players therefore see a rope, without sharing anything over the network (the
/// positions are already synchronized). No more need for a cosmetic rope (RopeLinkRenderer).
/// </summary>
internal static unsafe partial class RopeApi
{
    /// <summary>Template cloned for each anchor. Captured by PatchPitonTemplate (Awake) or scan.</summary>
    private static GameObject _pitonTemplate;
    private static int _lastPitonScanFrame;

    /// <summary>One anchor (cloned piton) per roped partner.</summary>
    private static readonly Dictionary<int, GameObject> _ropeAnchors = new();

    /// <summary>Partners whose local lifeline rope has already been clipped in (AttachToPiton done).</summary>
    private static readonly HashSet<int> _ropeAnchorsAttached = new();

    /// <summary>True while at least one rope-team anchor is active (RopeTeamFallPatch safety net).</summary>
    private static bool _belayEngaged;
    public static bool IsNativeBelayEngaged => _belayEngaged;

    /// <summary>True if at least one rope-team anchor is placed (used by TickRopeTeam).</summary>
    public static bool HasRopeTeamAnchors => _ropeAnchors.Count > 0;

    /// <summary>Captures a piton template (called from the Piton.Awake patch).</summary>
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

    /// <summary>Piton template; throttled scene scan as a fallback if Awake captured nothing.</summary>
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
    /// Maintains the rope anchor toward <paramref name="partnerId"/>: creates it if needed (clone of
    /// the template), clips the local lifeline rope onto it once, then MOVES it onto the
    /// partner's harness every frame. Call every frame while the link is active.
    /// </summary>
    public static void UpdateRopeTeamAnchor(int partnerId, Vector3 partnerAnchorPos)
    {
        try
        {
            // Create the anchor on the first call (clone of the template -> no placement sound).
            if (!_ropeAnchors.TryGetValue(partnerId, out var anchor) || anchor == null)
            {
                var template = ResolvePitonTemplate();
                if (template == null) return;   // no template yet -> we retry later
                anchor = SpawnAnchorPiton(template, partnerAnchorPos);
                if (anchor == null) return;
                _ropeAnchors[partnerId] = anchor;
                _belayEngaged = true;
                Mod.LogDebug($"[RopeTeam] Rope anchor spawned for partner {partnerId}.");
            }

            // Follow the partner: piton + quickdraw endpoint rigidbodies (the Obi rope
            // pinned on the piton's collider follows -> mobile anchor).
            MoveAnchorPiton(anchor, partnerAnchorPos);

            // Clip the local lifeline rope into the anchor (only once). If the attach
            // hasn't taken yet (robot rope not available, etc.), we retry the next frame.
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
            // Non-pickable anchor (cf. Episure: *(sbyte*)(Piton+32)=0 == canBePickedUp=false).
            try { piton.canBePickedUp = false; } catch { }
            // Kinematic: the anchor follows the imposed position without falling or being pulled by tension.
            SetKinematic(piton.RigidBody);
            SetKinematic(piton.quickdrawBeginRigidBody);
            SetKinematic(piton.quickdrawEndRigidBody);
        }
        // The anchor is only a mechanical attach point for the rope: we hide its visual
        // (piton mesh + quickdraw) so we don't leave a piton floating in the void at the
        // partner's side. The Piton component, its rigidbodies and colliders stay active -> the
        // rope attaches to it and keeps following. The coop rope (lifeline.securingRope) is a
        // separate object, so it stays visible.
        HideAnchorRenderers(go);
        return go;
    }

    /// <summary>Disables all Renderers on the anchor clone (piton visual invisible).</summary>
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

        // The native rope (securingRope) must exist for AttachToPiton to have something to
        // clip. At rest it is null -> we INJECT the robot companion's rope
        // (Il2CppTheGameBakers.Cairn.RobotPawnController.GetRope()) into the lifeline, exactly like Episure. Internal
        // throttle (ResolveLocalClimbot) -> no spam if the rope is not available yet.
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
    /// Injects the robot companion's rope (Il2CppTheGameBakers.Cairn.RobotPawnController.GetRope()) as the lifeline's
    /// securingRope if it is empty (borrowed from Episure: write to the securingRope field). Returns
    /// true if the lifeline now has a rope.
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

    /// <summary>Finds (and caches) the local Il2CppTheGameBakers.Cairn.RobotPawnController. Remote robots are
    /// NetplayRemoteClimbot (a different type) -> FindObjectsOfType only returns the local one.</summary>
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

    /// <summary>Removes the rope anchor toward a partner (unroping / partner gone).</summary>
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

    /// <summary>Removes ALL rope-team anchors (disconnect / scene change).</summary>
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
            // Cleanly detach the rope from the piton before destruction (avoids leaving the
            // lifeline's securingRope pinned on a destroyed collider).
            var piton = anchor.GetComponent<Piton>();
            if (piton != null) { try { piton.Detach(); } catch { } }
        }
        catch { }
        try { Object.Destroy(anchor); } catch { }
    }
}
