using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Plants Aava's walking stick at a lip. The real bone is used once. Another stick on the
/// same pitch is a clone. LateUpdate wins over the hand and bag reparent.
/// </summary>
internal static class TopAnchorPlant
{
    private sealed class Planted
    {
        internal Transform Bone;
        internal Transform RestParent;
        internal Vector3 RestLocalPosition;
        internal Quaternion RestLocalRotation;
        internal bool Clone;
        internal Vector3 Lip;
        internal Quaternion Upright;
        internal float Lift;
        internal float ShaftTop;
        internal string ShaftName;
        internal bool Seated;
        internal bool LoggedMeshes;
        internal bool Held;
    }

    private static readonly Dictionary<uint, Planted> Plants = new();
    private static bool _missingLogged;
    private static AavaLightStickAnchor _stickAnchor;
    private static bool _stickAnchorWasEnabled;

    /// <summary>
    /// True after a late update left a rendered stick on the lip and nothing moved it away
    /// before the next pass. A bone that snapped back to the hand does not count.
    /// </summary>
    internal static bool StickStayedAtLip(uint id)
    {
        if (!Plants.TryGetValue(id, out var planted) || planted.Bone == null || !planted.Held) return false;
        try
        {
            if (!planted.Seated || planted.ShaftTop < planted.Lip.y + 0.25f) return false;
            var at = planted.Bone.position;
            var dx = at.x - planted.Lip.x;
            var dy = at.y - (planted.Lip.y + planted.Lift);
            var dz = at.z - planted.Lip.z;
            if (dx * dx + dy * dy + dz * dz > 0.25f) return false;
            return EnabledRendererCount(planted) > 0;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.staff-visual", exception);
            return false;
        }
    }

    internal static bool Plant(uint id, Vector3 lip)
    {
        try
        {
            return PlantCore(id, lip);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.staff-plant", exception);
            return false;
        }
    }

    private static bool PlantCore(uint id, Vector3 lip)
    {
        if (Plants.ContainsKey(id))
        {
            Plants[id].Lip = lip;
            return true;
        }

        var bone = FindStaffBone();
        var clone = false;
        if (bone == null || BoneInUse(bone))
        {
            var source = bone ?? FindLocalStaffBone();
            if (source == null)
            {
                if (!_missingLogged)
                {
                    _missingLogged = true;
                    ModLog.Warning("[RopeTeam] Aava's stick bone was not found, so the anchor has no staff.");
                }

                return false;
            }

            var copy = Object.Instantiate(source.gameObject);
            copy.name = "CairnMP Planted Staff";
            bone = copy.transform;
            clone = true;
        }

        var planted = new Planted
        {
            Bone = bone,
            RestParent = bone.parent,
            RestLocalPosition = bone.localPosition,
            RestLocalRotation = bone.localRotation,
            Clone = clone,
            Lip = lip,
            Upright = Quaternion.identity,
        };
        Plants[id] = planted;
        if (!clone) PauseLocalStickAnchor();
        ApplyOne(planted);
        LogPlacement(planted);
        return true;
    }

    internal static void Restore(uint id)
    {
        if (!Plants.TryGetValue(id, out var planted)) return;
        Plants.Remove(id);
        RestoreOne(planted);
        if (!RealBoneStillPlanted()) ResumeLocalStickAnchor();
    }

    private static bool RealBoneStillPlanted()
    {
        foreach (var planted in Plants.Values)
            if (!planted.Clone) return true;
        return false;
    }

    internal static void DropMissing()
    {
        if (Plants.Count == 0) return;
        _gone.Clear();
        foreach (var id in Plants.Keys)
            if (!TopAnchorSession.TryGetPosition(id, out _)) _gone.Add(id);
        foreach (var id in _gone) Restore(id);
    }

    /// <summary>Called from the mod's late update, after appearance has placed the stick.</summary>
    internal static void Apply()
    {
        if (Plants.Count == 0) return;
        if (RealBoneStillPlanted()) PauseLocalStickAnchor();
        foreach (var planted in Plants.Values)
        {
            ApplyOne(planted);
            planted.Held = true;
        }
    }

    private static readonly List<uint> _gone = new();

    private static bool BoneInUse(Transform bone)
    {
        foreach (var planted in Plants.Values)
            if (planted.Bone == bone) return true;
        return false;
    }

    private static void ApplyOne(Planted planted)
    {
        var bone = planted.Bone;
        if (bone == null) return;
        bone.SetParent(null, worldPositionStays: true);
        bone.gameObject.SetActive(true);
        EnableRenderers(bone);
        // Identity keeps the head up. The shaft hangs below the bone, so the bone
        // itself on the lip puts the stick inside the rock. The glow bounds hang
        // further than the shaft, and seating those left the stick in the air.
        if (!planted.Seated)
        {
            planted.Upright = Quaternion.identity;
            bone.SetPositionAndRotation(planted.Lip, planted.Upright);
            planted.Seated = TrySeatShaft(bone, planted);
        }

        var seat = planted.Lip;
        seat.y += planted.Lift;
        bone.SetPositionAndRotation(seat, planted.Upright);
    }

    private static void EnableRenderers(Transform bone)
    {
        var renderers = bone.GetComponentsInChildren<Renderer>(true);
        if (renderers == null) return;
        for (var i = 0; i < renderers.Length; i++)
            if (renderers[i] != null) renderers[i].enabled = true;
    }

    /// <summary>
    /// The upright pose that showed the staff. The bone's children are the lamp glow, and
    /// their lowest corner sat 2.05 m down, which left the shaft in the air. The shaft
    /// itself is skinned to this bone on the body. Seat that tip in the lip.
    /// </summary>
    private static bool TrySeatShaft(Transform bone, Planted planted)
    {
        // The wooden mesh is skinned onto the body. The flare is only a stand-in when that
        // mesh cannot be read, and it is still shorter than the billboard that floated the stick.
        var found = TrySkinnedStickSpan(bone, out var tip, out var top, out var name)
                    || TryVerticalFlareSpan(bone, out tip, out top, out name);
        if (!found || !TopAnchorPolicy.TrySeatLift(planted.Lip.y, tip, out var lift))
        {
            if (!planted.LoggedMeshes)
            {
                planted.LoggedMeshes = true;
                LogMeshSpans(bone);
            }

            return false;
        }

        planted.Lift = lift;
        planted.ShaftTop = top + lift;
        planted.ShaftName = name;
        ModLog.Info(
            $"[RopeTeam] Staff shaft '{name}' seats {lift:F2} m up, tip {TopAnchorPolicy.SeatEmbedMeters:F2} m into the lip.");
        return true;
    }

    /// <summary>
    /// How far the wooden stick hangs from bn_Stick. Vertices weighted to that bone, or to
    /// a child of it, keep their offset when the bone stands upright.
    /// </summary>
    private static bool TrySkinnedStickSpan(Transform bone, out float tip, out float top, out string meshName)
    {
        tip = 0f;
        top = 0f;
        meshName = null;
        var body = LocalPlayerInterop.TryGetMCGameObject();
        if (body == null || bone == null) return false;

        var stickBones = new HashSet<int>();
        CollectInstanceIds(bone, stickBones);
        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        string foundName = null;
        var count = 0;
        try
        {
            var skinned = body.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (skinned == null) return false;
            for (var i = 0; i < skinned.Length; i++)
            {
                if (!TryStickVerts(skinned[i], bone, stickBones, ref min, ref max, ref count)) continue;
                foundName = skinned[i].name;
            }
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.staff-mesh", exception);
            return false;
        }

        // A handful of lamp verts is not the shaft. The glow seat was the miss.
        if (count < 8 || !float.IsFinite(min) || !float.IsFinite(max)) return false;
        var origin = bone.position.y;
        tip = origin + min;
        top = origin + max;
        meshName = foundName ?? "bn_Stick";
        return max - min >= 0.45f;
    }

    private static bool TryStickVerts(
        SkinnedMeshRenderer renderer, Transform bone, HashSet<int> stickBones,
        ref float min, ref float max, ref int count)
    {
        if (renderer == null) return false;
        var mesh = renderer.sharedMesh;
        if (mesh == null || !mesh.isReadable) return false;
        var bones = renderer.bones;
        var bind = mesh.bindposes;
        if (bones == null || bind == null) return false;

        var boneIndex = -1;
        var weighted = new HashSet<int>();
        var boneId = bone.GetInstanceID();
        for (var i = 0; i < bones.Length && i < bind.Length; i++)
        {
            var joint = bones[i];
            if (joint == null) continue;
            var id = joint.GetInstanceID();
            if (id == boneId) boneIndex = i;
            if (stickBones.Contains(id)) weighted.Add(i);
        }

        if (boneIndex < 0 || weighted.Count == 0) return false;
        var vertices = mesh.vertices;
        var weights = mesh.boneWeights;
        if (vertices == null || weights == null || vertices.Length == 0 || weights.Length != vertices.Length)
            return false;

        var toBone = bind[boneIndex];
        var any = false;
        for (var i = 0; i < vertices.Length; i++)
        {
            if (!WeightedToStick(weights[i], weighted)) continue;
            var local = toBone.MultiplyPoint3x4(vertices[i]);
            var radial = (local.x * local.x) + (local.z * local.z);
            if (radial > 0.25f * 0.25f) continue;
            if (local.y < min) min = local.y;
            if (local.y > max) max = local.y;
            count++;
            any = true;
        }

        return any;
    }

    private static bool WeightedToStick(BoneWeight weight, HashSet<int> stickBones)
    {
        return Influence(weight.boneIndex0, weight.weight0, stickBones)
               || Influence(weight.boneIndex1, weight.weight1, stickBones)
               || Influence(weight.boneIndex2, weight.weight2, stickBones)
               || Influence(weight.boneIndex3, weight.weight3, stickBones);
    }

    private static bool Influence(int boneIndex, float weight, HashSet<int> stickBones)
        => weight >= 0.35f && stickBones.Contains(boneIndex);

    private static void CollectInstanceIds(Transform root, HashSet<int> ids)
    {
        if (root == null) return;
        ids.Add(root.GetInstanceID());
        for (var i = 0; i < root.childCount; i++)
            CollectInstanceIds(root.GetChild(i), ids);
    }

    /// <summary>The long lamp flare along the shaft. The 4 m billboards are what sat the stick in the air.</summary>
    private static bool TryVerticalFlareSpan(Transform bone, out float tip, out float top, out string meshName)
    {
        tip = float.PositiveInfinity;
        top = float.NegativeInfinity;
        meshName = null;
        var found = false;
        var filters = bone.GetComponentsInChildren<MeshFilter>(true);
        if (filters == null) return false;
        for (var i = 0; i < filters.Length; i++)
        {
            var filter = filters[i];
            if (filter == null || filter.sharedMesh == null) continue;
            if (filter.name.IndexOf("QuadFlare_Vertical", System.StringComparison.Ordinal) < 0) continue;
            if (!TryBoundsSpan(filter.transform, filter.sharedMesh.bounds, out var minY, out var maxY)) continue;
            found = true;
            if (minY >= tip) continue;
            tip = minY;
            top = maxY;
            meshName = filter.name;
        }

        return found;
    }

    private static bool TryBoundsSpan(Transform transform, Bounds bounds, out float minY, out float maxY)
    {
        minY = float.PositiveInfinity;
        maxY = float.NegativeInfinity;
        var extents = bounds.extents;
        if (extents.x <= 0f && extents.y <= 0f && extents.z <= 0f) return false;
        var matrix = transform.localToWorldMatrix;
        var center = bounds.center;
        for (var corner = 0; corner < 8; corner++)
        {
            var point = matrix.MultiplyPoint3x4(center + new Vector3(
                (corner & 1) == 0 ? -extents.x : extents.x,
                (corner & 2) == 0 ? -extents.y : extents.y,
                (corner & 4) == 0 ? -extents.z : extents.z));
            if (point.y < minY) minY = point.y;
            if (point.y > maxY) maxY = point.y;
        }

        return float.IsFinite(minY) && float.IsFinite(maxY);
    }

    private static void LogMeshSpans(Transform bone)
    {
        var renderers = bone.GetComponentsInChildren<Renderer>(true);
        if (renderers == null || renderers.Length == 0)
        {
            ModLog.Warning("[RopeTeam] The staff bone has no mesh, so the piton stays visible.");
            return;
        }

        for (var i = 0; i < renderers.Length; i++)
        {
            var renderer = renderers[i];
            if (renderer == null) continue;
            var size = renderer.bounds.size;
            var typeName = renderer.GetIl2CppType()?.Name ?? renderer.GetType().Name;
            ModLog.Info(
                $"[RopeTeam] Staff mesh '{renderer.name}' is a {typeName} sized ({size.x:F2},{size.y:F2},{size.z:F2}).");
        }
    }

    /// <summary>
    /// The native anchor's LateUpdate puts bn_Stick back in the hand after we place it.
    /// Pausing that behaviour leaves the planted bone where this mod set it.
    /// </summary>
    private static void PauseLocalStickAnchor()
    {
        if (_stickAnchor != null)
        {
            _stickAnchor.enabled = false;
            return;
        }

        var body = LocalPlayerInterop.TryGetMCGameObject();
        if (body == null) return;
        var anchor = body.GetComponentInChildren<AavaLightStickAnchor>(true);
        if (anchor == null) return;
        _stickAnchor = anchor;
        _stickAnchorWasEnabled = anchor.enabled;
        anchor.enabled = false;
    }

    private static void ResumeLocalStickAnchor()
    {
        if (_stickAnchor == null) return;
        _stickAnchor.enabled = _stickAnchorWasEnabled;
        _stickAnchor = null;
    }

    private static void RestoreOne(Planted planted)
    {
        var bone = planted.Bone;
        if (bone == null) return;
        if (planted.Clone)
        {
            Object.Destroy(bone.gameObject);
            return;
        }

        if (planted.RestParent != null)
            bone.SetParent(planted.RestParent, worldPositionStays: false);
        bone.localPosition = planted.RestLocalPosition;
        bone.localRotation = planted.RestLocalRotation;
    }

    private static int EnabledRendererCount(Planted planted)
    {
        var bone = planted?.Bone;
        if (bone == null) return 0;
        try
        {
            var renderers = bone.GetComponentsInChildren<Renderer>(true);
            if (renderers == null) return 0;
            var count = 0;
            for (var i = 0; i < renderers.Length; i++)
                if (renderers[i] != null && renderers[i].enabled) count++;
            return count;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.staff-visual", exception);
            return 0;
        }
    }

    private static void LogPlacement(Planted planted)
    {
        var bone = planted.Bone;
        if (bone == null) return;
        var at = bone.position;
        var renderers = EnabledRendererCount(planted);
        ModLog.Info(
            $"[RopeTeam] Staff bone is at ({at.x:F1},{at.y:F1},{at.z:F1}) with {renderers} renderer(s), seat {planted.Lift:F2} m.");
    }

    private static Transform FindStaffBone()
    {
        if (TeamAnchorSession.LocalPlayerId == TeamAnchorSession.LeaderId)
            return FindLocalStaffBone();
        if (!RemotePlayerManager.TryGetGhostTransform(TeamAnchorSession.LeaderId, out var ghost) || ghost == null)
            return null;
        return FindNamed(ghost, "bn_Stick");
    }

    private static Transform FindLocalStaffBone()
    {
        var body = LocalPlayerInterop.TryGetMCGameObject();
        if (body == null) return null;
        return FindNamed(body.transform, "bn_Stick");
    }

    private static Transform FindNamed(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        for (var i = 0; i < root.childCount; i++)
        {
            var found = FindNamed(root.GetChild(i), name);
            if (found != null) return found;
        }

        return null;
    }
}
