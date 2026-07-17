using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Features.Roping;

/// <summary>
/// Roping players together (coop belay) — local physics SPIKE (step 1).
///
/// Goal of the spike: verify that placing a piton (Lifeline.AddPiton) at another
/// player's position and moving it every frame produces a correct rope / distance
/// lock. No network here: we clip an anchor locally onto a ghost's (already synced)
/// position and observe the behavior.
///
/// We reuse the existing piton machinery (PitonSync): SpawnRemotePiton places a
/// piton WITHOUT rebroadcasting it (it increments _remotePitonsAdded so
/// CheckForNewPiton ignores it), TryGetLastPitonPointer captures its pointer, and
/// TryDetachPitonViaLifeline removes it cleanly.
///
/// Robustness: everything in try/catch, no-op if Lifeline/piton is absent, never crashes.
/// </summary>
internal static unsafe partial class RopeApi
{
    private static IntPtr _anchorPitonPtr;

    // Piton rigidbody offsets (resolved once; -1 = absent). Episure makes all three
    // KINEMATIC (root RigidBody + quickdrawBegin/End) and pins them onto the body root
    // every frame. Moving only the piton's transform let the rope ends diverge AND the
    // non-kinematic root got shoved around by physics (jitter/drift).
    private static bool _pitonRbFieldsResolved;
    private static int _pitonRootRbOffset = -1;
    private static int _quickdrawBeginOffset = -1;
    private static int _quickdrawEndOffset = -1;

    public static bool IsAnchorActive => _anchorPitonPtr != IntPtr.Zero;

    /// <summary>Places an anchor (a non-rebroadcast local piton) at the given position.</summary>
    public static bool ClipAnchorTo(Vector3 position)
    {
        ReleaseAnchor();

        try
        {
            // Reuse the "silent" (non-rebroadcast) local piton spawn.
            if (!SpawnRemotePiton(position, Quaternion.identity, quality: 5, hp: 100, itemId: 3))
                return false;

            if (TryGetLastPitonPointer(out var ptr) && ptr != IntPtr.Zero)
            {
                _anchorPitonPtr = ptr;
                // Make the piton's rigidbodies kinematic + pin them onto the anchor
                // (like Episure), so physics doesn't leave them behind.
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

    /// <summary>Moves the anchor to the partner's position (every frame).</summary>
    public static void UpdateAnchor(Vector3 position)
    {
        if (_anchorPitonPtr == IntPtr.Zero) return;

        try
        {
            var t = new MonoBehaviour(_anchorPitonPtr).transform;
            if (t == null || t.Pointer == IntPtr.Zero)
            {
                _anchorPitonPtr = IntPtr.Zero; // stale pointer (piton destroyed)
                return;
            }
            t.position = position;
            // Keep the piton root + rope ends coincident with the anchor,
            // otherwise the native rope stretches toward their old position.
            PinPitonRigidbodies(position);
        }
        catch
        {
            _anchorPitonPtr = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Makes the piton's rigidbodies kinematic and pins them onto
    /// <paramref name="position"/> (root + quickdrawBegin/End), like Episure. Resolves the
    /// offsets once; no-op per absent field. isKinematic is reasserted every frame to cover
    /// the rigidbodies created by the rope AFTER the clip.
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
        body.isKinematic = true; // idempotent: covers bodies created after the clip
        body.position = position;
    }

    // Diagnostic (#4 green beam): dump once the shader/color of every LineRenderer in
    // the scene to identify the rope rendered in bright green. To be read in the log after
    // a remote rope has bugged out.
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

    /// <summary>Removes the anchor (detaches the piton from the rope).</summary>
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

    /// <summary>Resets the anchor (scene change / disconnect).</summary>
    public static void ResetPlayerAnchorCache()
    {
        // The pointer becomes stale on a scene change: we forget it without
        // attempting a detach (the Lifeline has been recreated).
        _anchorPitonPtr = IntPtr.Zero;
    }
}
