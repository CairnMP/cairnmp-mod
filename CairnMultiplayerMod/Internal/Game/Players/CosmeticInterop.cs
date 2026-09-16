using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Players;

/// <summary>
/// Native ghosts omit outfit and glove state; fallback hand lights avoid the fragile skeleton
/// rebind required to reproduce the glove mesh.
/// </summary>
internal static unsafe class CosmeticInterop
{
    private const string GhostGloveLightName = "MP_GlowGloveLight";

    private static GlowingGloves _localGlovesCached;
    private static int _lastLocalGlovesSearchFrame;
    private static bool _cosmeticWarningLogged;
    private static bool _ghostHandDumpDone;

    // Read native values because Cairn's configured glove glow is not always the default cyan.
    private static bool _glowTemplateRead;
    private static Color _glowColor = new(0.45f, 0.85f, 1f);
    private static float _glowIntensity = 2.5f;
    private static float _glowRange = 1.6f;

    public static bool TryGetLocalCosmetics(out byte flags)
    {
        flags = 0;
        var mc = LocalPlayerInterop.TryGetMCGameObject();
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

    public static void ResetCaches()
    {
        _localGlovesCached = null;
        _lastLocalGlovesSearchFrame = 0;
        _cosmeticWarningLogged = false;
    }

    /// <summary>
    /// Forgets the per-ghost caches (glove rig + stick rest pose). The key is derived here,
    /// like the setters do, because the component can sit on a child of the ghost root.
    /// Call before destroying the ghost: IL2CPP recycles pointers, so a stale entry would let
    /// a new ghost inherit a departed player's rig.
    /// </summary>
    public static void ResetGhostCosmeticCaches(NetplayRemotePlayer ghost)
    {
        if (ghost == null || ghost.Pointer == IntPtr.Zero) return;
        GameObject go;
        try { go = ghost.gameObject; }
        catch (Exception exception)
        {
            ModLog.SuppressedException("cosmetics.resolve-ghost-cache-object", exception);
            return;
        }
        if (go == null) return;
        _ghostGloveRigs.Remove(go.Pointer);
        _ghostStickRest.Remove(go.Pointer);
    }

    public static void ResetGhostCosmeticCaches()
    {
        _ghostGloveRigs.Clear();
        _ghostStickRest.Clear();
    }

    public static bool SetGhostGlowingGloves(NetplayRemotePlayer ghost, bool on)
    {
        if (ghost == null || ghost.Pointer == IntPtr.Zero) return false;

        GameObject go;
        try { go = ghost.gameObject; }
        catch (Exception exception)
        {
            ModLog.SuppressedException("cosmetics.resolve-ghost-object", exception);
            return false;
        }
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
            if (existing != null) return true;

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

    /// <summary><c>ShouldBeVisible</c> reports ownership, so visibility must come from the applied mesh state.</summary>
    private static bool AreGlovesVisible(GlowingGloves gloves)
    {
        try
        {
            var r = gloves.mainRenderer;
            return r != null && r.enabled && r.gameObject.activeInHierarchy;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("cosmetics.read-local-glove-state", exception);
            return false;
        }
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
            ModLog.Debug($"[CosmeticSync] Glow template: color={_glowColor} intensity={_glowIntensity} range={_glowRange}");
        }
        catch (Exception exception) { ModLog.SuppressedException("cosmetics.capture-glow-template", exception); }
    }

    private static bool TryGetGhostHandBones(GameObject go, out Transform left, out Transform right)
    {
        left = null;
        right = null;

        var animator = FingerInterop.TryGetHumanoidAnimator(go);
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
                // Exclude fingers, IK targets, attach points.
                if (low.Contains("finger") || low.Contains("thumb") || low.Contains("index")
                    || low.Contains("middle") || low.Contains("ring") || low.Contains("pinky")
                    || low.Contains("little") || low.Contains("ik") || low.Contains("target")
                    || low.Contains("pole") || low.Contains("attach"))
                    continue;

                // Include the _l_ / _r_ infix (e.g. loc_l_ContactHand, loc_r_ContactHand of the Cairn skeleton).
                if (left == null && (low.Contains("left") || low.Contains("_l_") || low.EndsWith("_l") || low.EndsWith(".l") || low.EndsWith(" l") || low.Contains("hand_l") || low.Contains("handl")))
                    left = t;
                else if (right == null && (low.Contains("right") || low.Contains("_r_") || low.EndsWith("_r") || low.EndsWith(".r") || low.EndsWith(" r") || low.Contains("hand_r") || low.Contains("handr")))
                    right = t;

                if (left != null && right != null) break;
            }
        }
        catch (Exception exception) { ModLog.SuppressedException("cosmetics.apply-ghost-appearance", exception); }
    }

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
            ModLog.Warning(count > 0 ? sb.ToString()
                : "[CosmeticSync] No hand-like bones found on ghost — glove glow unavailable.");
        }
        catch (Exception exception) { ModLog.SuppressedException("cosmetics.create-glove-glow", exception); }
    }

    private static void WarnOnce(string msg)
    {
        if (_cosmeticWarningLogged) return;
        _cosmeticWarningLogged = true;
        ModLog.Warning(msg);
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

    // The gloves have their OWN skeleton (rootBoneGloves='Armature', 165 bones named like the body)
    // and the native GlowingGloves.LateUpdate component copies the body -> this armature every frame.
    // The ghost prefab has none of that. So we clone the glove sub-tree (mesh + its armature,
    // self-contained) onto the ghost, then drive the glove armature from the ghost's BODY bones
    // every frame (same names) — exactly like the native code. No fragile rebind.
    private const string GhostGloveMeshName = "MP_GhostGloveRig";

    private sealed class GhostGloveRig
    {
        public GameObject Clone;
        public Transform[] GloveBones;
        public Transform[] BodyBones;
    }
    private static readonly System.Collections.Generic.Dictionary<IntPtr, GhostGloveRig> _ghostGloveRigs = new();

    public static bool SetGhostGloveMesh(NetplayRemotePlayer ghost, bool on)
    {
        if (ghost == null || ghost.Pointer == IntPtr.Zero) return false;
        GameObject go;
        try { go = ghost.gameObject; }
        catch (Exception exception)
        {
            ModLog.SuppressedException("cosmetics.resolve-glow-rig-object", exception);
            return false;
        }
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

            var mc = LocalPlayerInterop.TryGetMCGameObject();
            var localGloves = mc != null ? TryGetLocalGlowingGloves(mc) : null;
            var srcRenderer = localGloves != null ? localGloves.mainRenderer : null;
            if (srcRenderer == null) return false;
            var innerGo = srcRenderer.transform.parent != null
                ? srcRenderer.transform.parent.gameObject : srcRenderer.gameObject;
            if (innerGo == null) return false;

            // Map of the ghost's BODY bones — built BEFORE parenting the clone (otherwise the
            // cloned bones, named identically, would pollute the map).
            var map = new System.Collections.Generic.Dictionary<string, Transform>();
            var ghostTs = go.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < ghostTs.Length; i++)
            {
                var t = ghostTs[i];
                if (t != null && !map.ContainsKey(t.name)) map[t.name] = t;
            }

            var clone = UnityEngine.Object.Instantiate(innerGo);
            clone.name = GhostGloveMeshName;
            clone.transform.SetParent(go.transform, worldPositionStays: false);
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;

            // Remove any cloned GlowingGloves component (we drive the armature ourselves).
            foreach (var ggc in clone.GetComponentsInChildren<GlowingGloves>(true))
                if (ggc != null) UnityEngine.Object.Destroy(ggc);

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
                try { smr.enabled = true; smr.updateWhenOffscreen = true; }
                catch (Exception exception) { ModLog.SuppressedException("cosmetics.enable-skinned-renderer", exception); }
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
            ModLog.Debug($"[CosmeticSync] Ghost glove rig created ({gloveBones.Count} bones paired).");
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce($"[CosmeticSync] Ghost glove rig failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void PoseGloveRig(GhostGloveRig rig)
    {
        if (rig == null || rig.GloveBones == null) return;
        var gl = rig.GloveBones; var bd = rig.BodyBones;
        for (int i = 0; i < gl.Length; i++)
        {
            var c = gl[i]; var b = bd[i];
            if (c == null || b == null) continue;
            try { c.position = b.position; c.rotation = b.rotation; }
            catch (Exception exception) { ModLog.SuppressedException("cosmetics.sync-glow-transform", exception); }
        }
    }

    public static void TickGhostGloveRigs()
    {
        if (_ghostGloveRigs.Count == 0) return;
        foreach (var rig in _ghostGloveRigs.Values)
            if (rig != null && rig.Clone != null && rig.Clone.activeSelf)
                PoseGloveRig(rig);
    }

    // The anchor's mode (LightStickMode) decides the stick's position on the LOCAL side:
    //   Locator (=1) = in hand, Default (=0) = stowed on the bag.
    // The ghost (stripped-down netplay prefab) does NOT have an AavaLightStickAnchor -> we can't
    // set a mode on it. So we reparent the ghost's stick mesh: onto loc_Stick (under bn_r_Wrist,
    // already synced by the NetFrame -> follows the wrist) when in hand, or onto its original bag
    // bone (bn_Bag_Up, native stowed position) otherwise. The mode is carried PACKED in the lamp's
    // int (LampInterop) -> no new network packet.

    private const int LightStickModeLocator = 1;

    // Stick (bn_Stick) offset relative to loc_Stick when in hand: measured in-game (mode=Locator)
    // -> pos=(0, 0.62, 0), identity rotation. This is exactly what the native anchor does in
    // Locator mode (stick 0.62 m along loc_Stick, no rotation).
    private static readonly Vector3 StickHandLocalPos = new(0f, 0.62f, 0f);
    private static readonly Quaternion StickHandLocalRot = Quaternion.identity;

    public static bool TryGetLocalStickAnchorMode(out int mode)
    {
        mode = 0;
        var mc = LocalPlayerInterop.TryGetMCGameObject();
        if (mc == null) return false;
        try
        {
            var anchor = mc.GetComponentInChildren<Il2Cpp.AavaLightStickAnchor>(true);
            if (anchor == null) return false;
            mode = (int)anchor.mode;
            return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("cosmetics.read-lamp-mode", exception);
            return false;
        }
    }

    private struct StickBoneRest { public Transform Parent; public Vector3 Pos; public Quaternion Rot; }
    private static readonly System.Collections.Generic.Dictionary<IntPtr, StickBoneRest> _ghostStickRest = new();

    /// <summary>The ghost lacks the native anchor component, so its unanimated stick bone is reparented.</summary>
    public static bool ApplyGhostStickByAnchorMode(NetplayRemotePlayer ghost, int anchorMode)
    {
        if (ghost == null || ghost.Pointer == IntPtr.Zero) return false;
        GameObject go;
        try { go = ghost.gameObject; }
        catch (Exception exception)
        {
            ModLog.SuppressedException("cosmetics.resolve-stick-object", exception);
            return false;
        }
        if (go == null) return false;

        try
        {
            var bnStick = FindChildByName(go, "bn_Stick");
            if (bnStick == null) return false;
            var key = go.Pointer;

            // Once: snapshot bn_Stick's native pose + parent the light onto bn_Stick
            // (like the local player: AavaStickLight child of bn_Stick) so it follows the mesh.
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

    // The ghost (netplay prefab) has ALL its outfit meshes active at once (hood + no-hood +
    // no-harness + 2 bags + robot) -> visual overlap. The local player only enables the right
    // subset (via PawnSkinHandler). So we mirror the visible state of each mesh present on the
    // ghost. The state is carried as a bitfield packed in the lamp's int (bits 16+), one bit per
    // mesh in the order below -> no new network packet.
    private static readonly string[] OutfitMeshes =
    {
        "NPC_Bot", "MC_Bag", "OBJ_Piolet", "MC_Body", "MC_Outfit",
        "MC_Outfit_NoHood", "MC_Outift_NoHarness", "MC_SmallBag", "OBJ_BloqueurPoulie",
    };
    public const int OutfitBitsMask = (1 << 9) - 1;

    public static int GetLocalOutfitBits()
    {
        var mc = LocalPlayerInterop.TryGetMCGameObject();
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
        catch (Exception exception) { ModLog.SuppressedException("cosmetics.capture-appearance-bits", exception); }
        return bits;
    }

    public static bool ApplyGhostOutfitBits(NetplayRemotePlayer ghost, int bits)
    {
        if (ghost == null || ghost.Pointer == IntPtr.Zero) return false;
        GameObject go;
        try { go = ghost.gameObject; }
        catch (Exception exception)
        {
            ModLog.SuppressedException("cosmetics.resolve-appearance-object", exception);
            return false;
        }
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
