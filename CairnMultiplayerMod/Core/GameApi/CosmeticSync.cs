using System;
using CairnMultiplayer.Shared;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Synchronisation des cosmetiques du personnage entre joueurs.
///
/// Le netplay vanille ne transmet ni les gants lumineux (GlowingGloves) ni l'outfit :
/// le fantome est un clone du NetplayClimberPrefab par defaut, donc apparence de base.
/// On lit ici l'etat cosmetique local (pour l'instant : gants lumineux actifs) sous
/// forme de champ de bits, et on le reapplique sur le fantome.
///
/// Gants lumineux : le composant Cairn `GlowingGloves` (sur le MC) porte deux
/// GameObjects `leftLight`/`rightLight` (la lueur reelle) + un `mainRenderer` (le mesh),
/// auto-geres depuis l'inventaire. Le fantome n'a PAS ce composant -> on attache deux
/// Light de secours aux os de mains du fantome (LeftHand/RightHand via l'Animator
/// humanoide, avec repli par nom d'os comme FingerSync). On ne reproduit pas le mesh
/// (rebind de skeleton trop fragile) : la lueur sur les mains suffit a regler le bug.
/// </summary>
public static unsafe partial class CairnGameApi
{
    private const string GhostGloveLightName = "MP_GlowGloveLight";

    private static GlowingGloves _localGlovesCached;
    private static int _lastLocalGlovesSearchFrame;
    private static bool _cosmeticWarningLogged;
    private static bool _ghostHandDumpDone;

    // Template de la lueur des gants, lu une fois depuis le GlowingGloves local
    // (sinon valeurs par defaut cyan). Sert a faire matcher la lueur du fantome.
    private static bool _glowTemplateRead;
    private static Color _glowColor = new(0.45f, 0.85f, 1f);
    private static float _glowIntensity = 2.5f;
    private static float _glowRange = 1.6f;

    /// <summary>
    /// Lit l'etat cosmetique local sous forme de champ de bits (cf. Protocol.CosmeticFlag*).
    /// Retourne false si le MC local est indisponible.
    /// </summary>
    public static bool TryGetLocalCosmetics(out byte flags)
    {
        flags = 0;
        var mc = TryGetLocalMCGameObject();
        if (mc == null) return false;

        try
        {
            var gloves = TryGetLocalGlowingGloves(mc);
            if (gloves != null && AreGlovesVisible(gloves))
            {
                flags |= Protocol.CosmeticFlagGlowingGloves;
                ReadGlowTemplate(gloves);
            }

            return true;
        }
        catch (Exception ex)
        {
            WarnOnce($"[CosmeticSync] Local read failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static void ResetLocalCosmeticsCache()
    {
        _localGlovesCached = null;
        _lastLocalGlovesSearchFrame = 0;
        _cosmeticWarningLogged = false;
    }

    /// <summary>
    /// Active/desactive la lueur de gants sur un fantome en attachant (ou retirant)
    /// deux Light de secours aux os de mains. Idempotent. Retourne false si echec.
    /// </summary>
    public static bool SetGhostGlowingGloves(NetplayRemotePlayer ghost, bool on)
    {
        if (ghost == null || ghost.Pointer == IntPtr.Zero) return false;

        GameObject go;
        try { go = ghost.gameObject; }
        catch { return false; }
        if (go == null) return false;

        try
        {
            if (!TryGetGhostHandBones(go, out var leftHand, out var rightHand))
            {
                DumpGhostHandCandidates(go);
                return false;
            }

            bool ok = false;
            ok |= SetHandGlow(leftHand, on);
            ok |= SetHandGlow(rightHand, on);
            return ok;
        }
        catch (Exception ex)
        {
            WarnOnce($"[CosmeticSync] Ghost apply failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool SetHandGlow(Transform hand, bool on)
    {
        if (hand == null || hand.Pointer == IntPtr.Zero) return false;

        var existing = hand.Find(GhostGloveLightName);
        if (on)
        {
            if (existing != null) return true; // deja en place

            var lightGo = new GameObject(GhostGloveLightName);
            lightGo.transform.SetParent(hand, worldPositionStays: false);
            lightGo.transform.localPosition = Vector3.zero;

            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = _glowColor;
            light.intensity = _glowIntensity;
            light.range = _glowRange;
            light.shadows = LightShadows.None;
            return true;
        }

        if (existing != null)
            UnityEngine.Object.Destroy(existing.gameObject);
        return true;
    }

    private static GlowingGloves TryGetLocalGlowingGloves(GameObject mc)
    {
        if (_localGlovesCached != null && _localGlovesCached.Pointer != IntPtr.Zero)
            return _localGlovesCached;

        var frame = Time.frameCount;
        if (frame == _lastLocalGlovesSearchFrame) return null;
        _lastLocalGlovesSearchFrame = frame;

        var gloves = mc.GetComponentInChildren<GlowingGloves>(true);
        if (gloves != null) _localGlovesCached = gloves;
        return gloves;
    }

    /// <summary>
    /// Vrai si les gants sont reellement affiches sur le joueur local. On lit l'etat APPLIQUE par
    /// le jeu : le GameObject du mesh des gants (mainRenderer) est actif uniquement quand ils sont
    /// equipes/sortis. (ShouldBeVisible() ne convient pas : il renvoie true pour la simple
    /// possession en inventaire, donc tous les joueurs partageant la save paraissaient gantes.)
    /// </summary>
    private static bool AreGlovesVisible(GlowingGloves gloves)
    {
        try
        {
            var r = gloves.mainRenderer;
            return r != null && r.enabled && r.gameObject.activeInHierarchy;
        }
        catch { return false; }
    }

    private static void ReadGlowTemplate(GlowingGloves gloves)
    {
        if (_glowTemplateRead) return;
        try
        {
            var src = gloves.leftLight ?? gloves.rightLight;
            if (src == null) return;
            var light = src.GetComponent<Light>();
            if (light == null) return;

            _glowColor = light.color;
            _glowIntensity = Mathf.Max(0.5f, light.intensity);
            _glowRange = Mathf.Max(0.5f, light.range);
            _glowTemplateRead = true;
            Mod.LogDebug($"[CosmeticSync] Glow template: color={_glowColor} intensity={_glowIntensity} range={_glowRange}");
        }
        catch { }
    }

    /// <summary>
    /// Resout les os de mains du fantome : Animator humanoide d'abord, repli par nom
    /// si le rig est generique (cf. FingerSync, le rig de Cairn n'est pas toujours humanoide).
    /// </summary>
    private static bool TryGetGhostHandBones(GameObject go, out Transform left, out Transform right)
    {
        left = null;
        right = null;

        var animator = TryGetHumanoidAnimator(go);
        if (animator != null && animator.isHuman)
        {
            left = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            right = animator.GetBoneTransform(HumanBodyBones.RightHand);
        }

        if (left == null || right == null)
            ResolveHandBonesByName(go, ref left, ref right);

        return left != null || right != null;
    }

    private static void ResolveHandBonesByName(GameObject go, ref Transform left, ref Transform right)
    {
        try
        {
            var transforms = go.GetComponentsInChildren<Transform>(true);
            if (transforms == null) return;

            for (int i = 0; i < transforms.Length; i++)
            {
                var t = transforms[i];
                if (t == null) continue;
                var low = t.name.ToLowerInvariant();
                if (!low.Contains("hand")) continue;
                // Exclut doigts, cibles IK, attaches.
                if (low.Contains("finger") || low.Contains("thumb") || low.Contains("index")
                    || low.Contains("middle") || low.Contains("ring") || low.Contains("pinky")
                    || low.Contains("little") || low.Contains("ik") || low.Contains("target")
                    || low.Contains("pole") || low.Contains("attach"))
                    continue;

                // Inclut l'infixe _l_ / _r_ (ex. loc_l_ContactHand, loc_r_ContactHand du squelette Cairn).
                if (left == null && (low.Contains("left") || low.Contains("_l_") || low.EndsWith("_l") || low.EndsWith(".l") || low.EndsWith(" l") || low.Contains("hand_l") || low.Contains("handl")))
                    left = t;
                else if (right == null && (low.Contains("right") || low.Contains("_r_") || low.EndsWith("_r") || low.EndsWith(".r") || low.EndsWith(" r") || low.Contains("hand_r") || low.Contains("handr")))
                    right = t;

                if (left != null && right != null) break;
            }
        }
        catch { }
    }

    /// <summary>Logue une fois les transforms ressemblant a une main sous le fantome,
    /// pour diagnostiquer si la resolution echoue en jeu.</summary>
    private static void DumpGhostHandCandidates(GameObject go)
    {
        if (_ghostHandDumpDone) return;
        _ghostHandDumpDone = true;
        try
        {
            var transforms = go.GetComponentsInChildren<Transform>(true);
            if (transforms == null) return;
            var sb = new System.Text.StringBuilder("[CosmeticSync] Ghost hand-bone candidates: ");
            int count = 0;
            for (int i = 0; i < transforms.Length; i++)
            {
                var t = transforms[i];
                if (t == null) continue;
                if (!t.name.ToLowerInvariant().Contains("hand")) continue;
                if (count > 0) sb.Append(", ");
                sb.Append(t.name);
                if (++count >= 30) break;
            }
            Mod.Log.Warning(count > 0 ? sb.ToString()
                : "[CosmeticSync] No hand-like bones found on ghost — glove glow unavailable.");
        }
        catch { }
    }

    private static void WarnOnce(string msg)
    {
        if (_cosmeticWarningLogged) return;
        _cosmeticWarningLogged = true;
        Mod.Log.Warning(msg);
    }

    private static Transform FindChildByName(GameObject root, string name)
    {
        var ts = root.GetComponentsInChildren<Transform>(true);
        if (ts == null) return null;
        for (int i = 0; i < ts.Length; i++)
        {
            var t = ts[i];
            if (t != null && t.name == name) return t;
        }
        return null;
    }

    // ---- Gants : clone du rig natif + pilotage depuis le corps du fantome -------------------
    // Les gants ont leur PROPRE squelette (rootBoneGloves='Armature', 165 os nommes comme le corps)
    // et le composant natif GlowingGloves.LateUpdate recopie le corps -> cet armature chaque frame.
    // Le prefab du fantome n'a rien de tout ca. On clone donc le sous-arbre des gants (mesh + son
    // armature, auto-suffisant) sur le fantome, puis on pilote l'armature des gants depuis les os du
    // CORPS du fantome chaque frame (memes noms) — exactement comme le natif. Aucun rebind fragile.
    private const string GhostGloveMeshName = "MP_GhostGloveRig";

    private sealed class GhostGloveRig
    {
        public GameObject Clone;
        public Transform[] GloveBones;   // os de l'armature de gants clonee
        public Transform[] BodyBones;    // os du corps du fantome correspondants (meme index)
    }
    private static readonly System.Collections.Generic.Dictionary<IntPtr, GhostGloveRig> _ghostGloveRigs = new();

    /// <summary>Cree (au besoin) puis affiche/masque le rig de gants sur le fantome.</summary>
    public static bool SetGhostGloveMesh(NetplayRemotePlayer ghost, bool on)
    {
        if (ghost == null || ghost.Pointer == IntPtr.Zero) return false;
        GameObject go;
        try { go = ghost.gameObject; } catch { return false; }
        if (go == null) return false;
        var key = go.Pointer;

        try
        {
            if (_ghostGloveRigs.TryGetValue(key, out var existing) && existing != null && existing.Clone != null)
            {
                if (existing.Clone.activeSelf != on) existing.Clone.SetActive(on);
                if (on) PoseGloveRig(existing);
                return true;
            }
            if (!on) return true;

            // Template : le sous-arbre des gants du joueur local (mesh GlowingGloves00.002 + Armature).
            var mc = TryGetLocalMCGameObject();
            var localGloves = mc != null ? TryGetLocalGlowingGloves(mc) : null;
            var srcRenderer = localGloves != null ? localGloves.mainRenderer : null;
            if (srcRenderer == null) return false;
            var innerGo = srcRenderer.transform.parent != null
                ? srcRenderer.transform.parent.gameObject : srcRenderer.gameObject;
            if (innerGo == null) return false;

            // Carte des os du CORPS du fantome — construite AVANT de parenter le clone (sinon les
            // os clones, nommes a l'identique, pollueraient la carte).
            var map = new System.Collections.Generic.Dictionary<string, Transform>();
            var ghostTs = go.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < ghostTs.Length; i++)
            {
                var t = ghostTs[i];
                if (t != null && !map.ContainsKey(t.name)) map[t.name] = t;
            }

            // Clone le sous-arbre (mesh skinne sur sa propre armature -> auto-suffisant).
            var clone = UnityEngine.Object.Instantiate(innerGo);
            clone.name = GhostGloveMeshName;
            clone.transform.SetParent(go.transform, worldPositionStays: false);
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;

            // Retire un eventuel composant GlowingGloves clone (on pilote l'armature nous-memes).
            foreach (var ggc in clone.GetComponentsInChildren<GlowingGloves>(true))
                if (ggc != null) UnityEngine.Object.Destroy(ggc);

            // Appaire les os de l'armature de gants clonee aux os du corps du fantome (par nom).
            var gloveBones = new System.Collections.Generic.List<Transform>();
            var bodyBones = new System.Collections.Generic.List<Transform>();
            var cloneTs = clone.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < cloneTs.Length; i++)
            {
                var t = cloneTs[i];
                if (t == null) continue;
                if (map.TryGetValue(t.name, out var bodyBone) && bodyBone != null)
                {
                    gloveBones.Add(t);
                    bodyBones.Add(bodyBone);
                }
            }

            foreach (var smr in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null) continue;
                try { smr.enabled = true; smr.updateWhenOffscreen = true; } catch { }
            }

            var rig = new GhostGloveRig
            {
                Clone = clone,
                GloveBones = gloveBones.ToArray(),
                BodyBones = bodyBones.ToArray(),
            };
            _ghostGloveRigs[key] = rig;
            clone.SetActive(true);
            PoseGloveRig(rig);
            Mod.LogDebug($"[CosmeticSync] Ghost glove rig created ({gloveBones.Count} bones paired).");
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce($"[CosmeticSync] Ghost glove rig failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Recopie la pose des os du corps du fantome sur l'armature des gants (bonesPairs natif).</summary>
    private static void PoseGloveRig(GhostGloveRig rig)
    {
        if (rig == null || rig.GloveBones == null) return;
        var gl = rig.GloveBones; var bd = rig.BodyBones;
        for (int i = 0; i < gl.Length; i++)
        {
            var c = gl[i]; var b = bd[i];
            if (c == null || b == null) continue;
            try { c.position = b.position; c.rotation = b.rotation; } catch { }
        }
    }

    /// <summary>A appeler chaque frame : repose les rigs de gants actifs sur le corps des fantomes.</summary>
    public static void TickGhostGloveRigs()
    {
        if (_ghostGloveRigs.Count == 0) return;
        foreach (var rig in _ghostGloveRigs.Values)
            if (rig != null && rig.Clone != null && rig.Clone.activeSelf)
                PoseGloveRig(rig);
    }

    // ---- Baton : sync de la position via le mode natif de l'AavaLightStickAnchor ---------
    // Le mode de l'anchor (LightStickMode) decide la position du baton cote LOCAL :
    //   Locator (=1) = en main, Default (=0) = range sur le sac.
    // Le fantome (prefab netplay allege) n'a PAS d'AavaLightStickAnchor -> impossible de lui poser
    // un mode. On reparente donc le mesh du baton du fantome : sur loc_Stick (sous bn_r_Wrist, deja
    // synchronise par le NetFrame -> suit le poignet) quand en main, ou sur son os de sac d'origine
    // (bn_Bag_Up, position native rangee) sinon. Le mode est transporte PACKE dans l'int de la
    // lampe (LampSync) -> aucun nouveau paquet reseau.

    private const int LightStickModeLocator = 1;   // LightStickMode.Locator = en main

    // Offset du baton (bn_Stick) relatif a loc_Stick quand en main : mesure en jeu (mode=Locator)
    // -> pos=(0, 0.62, 0), rotation identite. C'est exactement ce que fait l'anchor natif en mode
    // Locator (baton a 0.62 m le long de loc_Stick, sans rotation).
    private static readonly Vector3 StickHandLocalPos = new(0f, 0.62f, 0f);
    private static readonly Quaternion StickHandLocalRot = Quaternion.identity;

    /// <summary>Lit le mode de l'AavaLightStickAnchor local (LightStickMode en int), ou false.</summary>
    public static bool TryGetLocalStickAnchorMode(out int mode)
    {
        mode = 0;
        var mc = TryGetLocalMCGameObject();
        if (mc == null) return false;
        try
        {
            var anchor = mc.GetComponentInChildren<Il2Cpp.AavaLightStickAnchor>(true);
            if (anchor == null) return false;
            mode = (int)anchor.mode;
            return true;
        }
        catch { return false; }
    }

    // Etat repos (sac) de l'os bn_Stick de chaque fantome, capture avant tout deplacement.
    private struct StickBoneRest { public Transform Parent; public Vector3 Pos; public Quaternion Rot; }
    private static readonly System.Collections.Generic.Dictionary<IntPtr, StickBoneRest> _ghostStickRest = new();

    /// <summary>
    /// Place le baton du fantome selon le mode anchor recu. On deplace l'OS bn_Stick (qui porte le
    /// MESH du baton — la lumiere AavaStickLight n'est que la lueur) : main = bn_Stick sur loc_Stick
    /// + offset mesure, range = bn_Stick a sa pose native. La lumiere est parentee a bn_Stick (comme
    /// le joueur local) pour suivre le mesh. bn_Stick n'est pas anime sur le fantome -> reparent sur.
    /// </summary>
    public static bool ApplyGhostStickByAnchorMode(NetplayRemotePlayer ghost, int anchorMode)
    {
        if (ghost == null || ghost.Pointer == IntPtr.Zero) return false;
        GameObject go;
        try { go = ghost.gameObject; } catch { return false; }
        if (go == null) return false;

        try
        {
            var bnStick = FindChildByName(go, "bn_Stick");
            if (bnStick == null) return false;
            var key = go.Pointer;

            // Une fois : snapshot de la pose native de bn_Stick + parente la lumiere sur bn_Stick
            // (comme le local : AavaStickLight enfant de bn_Stick) pour qu'elle suive le mesh.
            if (!_ghostStickRest.ContainsKey(key))
            {
                _ghostStickRest[key] = new StickBoneRest { Parent = bnStick.parent, Pos = bnStick.localPosition, Rot = bnStick.localRotation };
                var stickComp = go.GetComponentInChildren<Il2Cpp.AavaLightStick>(true);
                if (stickComp != null)
                {
                    var lt = stickComp.transform;
                    lt.SetParent(bnStick, worldPositionStays: false);
                    lt.localPosition = Vector3.zero;
                    lt.localRotation = Quaternion.identity;
                }
            }

            if (anchorMode == LightStickModeLocator)
            {
                var loc = FindChildByName(go, "loc_Stick");
                if (loc == null) return false;
                bnStick.SetParent(loc, worldPositionStays: false);
                bnStick.localPosition = StickHandLocalPos;
                bnStick.localRotation = StickHandLocalRot;
            }
            else
            {
                var rest = _ghostStickRest[key];
                if (rest.Parent == null || rest.Parent.Pointer == IntPtr.Zero) return false;
                bnStick.SetParent(rest.Parent, worldPositionStays: false);
                bnStick.localPosition = rest.Pos;
                bnStick.localRotation = rest.Rot;
            }
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce($"[CosmeticSync] Ghost stick apply failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ---- Outfit : mirror de l'etat actif des meshes du joueur local vers le fantome --------
    // Le fantome (prefab netplay) a TOUS ses meshes d'outfit actifs a la fois (capuche + no-hood +
    // no-harness + 2 sacs + robot) -> superposition visuelle. Le local n'active que le bon
    // sous-ensemble (via PawnSkinHandler). On mirror donc l'etat visible de chaque mesh present sur
    // le fantome. L'etat est transporte en bitfield packe dans l'int de la lampe (bits 16+), un bit
    // par mesh dans l'ordre ci-dessous -> aucun nouveau paquet reseau.
    private static readonly string[] OutfitMeshes =
    {
        "NPC_Bot", "MC_Bag", "OBJ_Piolet", "MC_Body", "MC_Outfit",
        "MC_Outfit_NoHood", "MC_Outift_NoHarness", "MC_SmallBag", "OBJ_BloqueurPoulie",
    };
    public const int OutfitBitsMask = (1 << 9) - 1;   // 9 meshes

    /// <summary>Bitfield de visibilite (active && renderer enabled) des meshes d'outfit locaux.</summary>
    public static int GetLocalOutfitBits()
    {
        var mc = TryGetLocalMCGameObject();
        if (mc == null) return 0;
        int bits = 0;
        try
        {
            var smrs = mc.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (smrs == null) return 0;
            for (int m = 0; m < OutfitMeshes.Length; m++)
            {
                for (int i = 0; i < smrs.Length; i++)
                {
                    var r = smrs[i];
                    if (r == null || r.name != OutfitMeshes[m]) continue;
                    if (r.gameObject.activeInHierarchy && r.enabled) bits |= (1 << m);
                    break;
                }
            }
        }
        catch { }
        return bits;
    }

    /// <summary>Applique le bitfield d'outfit sur le fantome : chaque mesh actif/inactif comme le local.</summary>
    public static bool ApplyGhostOutfitBits(NetplayRemotePlayer ghost, int bits)
    {
        if (ghost == null || ghost.Pointer == IntPtr.Zero) return false;
        GameObject go;
        try { go = ghost.gameObject; } catch { return false; }
        if (go == null) return false;
        try
        {
            var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (smrs == null) return false;
            for (int m = 0; m < OutfitMeshes.Length; m++)
            {
                bool want = ((bits >> m) & 1) != 0;
                for (int i = 0; i < smrs.Length; i++)
                {
                    var r = smrs[i];
                    if (r == null || r.name != OutfitMeshes[m]) continue;
                    if (r.gameObject.activeSelf != want) r.gameObject.SetActive(want);
                    break;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce($"[CosmeticSync] Ghost outfit apply failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

}
