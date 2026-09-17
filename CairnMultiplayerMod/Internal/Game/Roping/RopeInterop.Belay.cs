using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>A dedicated native rope connects two harness holders. No synthetic pitons.</summary>
internal static partial class RopeInterop
{
    private static RopeBindingSession _ropeSession;
    private static NativeRopeBinding _ropeBinding;
    private static int _ropePartner = -1;
    public static bool HasRopeTeamAnchors => _ropeSession != null;
    public static bool IsNativeBelayEngaged => _ropeSession?.State == RopeBindingState.Attached;

    public static bool UpdateRopeTeamAnchor(int partnerId, Harness partner)
    {
        try
        {
            if (!RopeTeamFallPatch.IsInstalled) return false;
            var local = ResolveLocalHarness();
            if (local == null || partner == null) { ReleaseAllAnchors(); return true; }
            if (_ropeBinding != null && (_ropePartner != partnerId || !_ropeBinding.Matches(local, partner)))
                ReleaseAllAnchors();
            if (_ropeSession == null)
            {
                var lifeline = local.PersonalLifeline;
                if (lifeline == null || lifeline.pawn == null) return true;
                var source = lifeline.securingRope;
                if (source == null)
                {
                    var climbot = ResolveLocalClimbot();
                    source = climbot != null ? climbot.GetRope() : null;
                }
                if (source == null || source.ropePartPrefab == null) return true;
                if (!TryGetHarnessAttachPosition(local, out var a) || !TryGetHarnessAttachPosition(partner, out var b)) return true;
                var length = source.MaxLengthMeters;
                // Inactive companion ropes may not have initialized their maximum yet.
                if (!float.IsFinite(length) || length <= 0) length = SharedRopeGamemode.SettingsType.Default.ropeLengthMeters;
                if (!RopeAttachmentPolicy.CanReach(length, Vector3.Distance(a, b))) return false;
                _ropeBinding = new NativeRopeBinding(local, partner, lifeline, source, length);
                _ropePartner = partnerId;
                _ropeSession = new RopeBindingSession(_ropeBinding, Time.realtimeSinceStartupAsDouble);
            }
            if (_ropeSession.Tick(Time.realtimeSinceStartupAsDouble)) return true;
            ReleaseAllAnchors();
            return false;
        }
        catch (Exception ex)
        {
            ModLog.Warning("[RopeTeam] Direct harness attachment failed: " + ex.Message);
            ReleaseAllAnchors();
            return false;
        }
    }

    internal static bool BeforeRopePhysics(LogicalRope rope)
    {
        if (_ropeBinding?.Owns(rope) != true) return true;
        if (!IsNativeBelayEngaged) return false;
        try { if (_ropeBinding.Maintain()) return true; }
        catch (Exception ex) { ModLog.Warning("[RopeTeam] Physics attachment lost: " + ex.Message); }
        ReleaseAllAnchors();
        return false; // Never enter native physics after disposing its attachments.
    }
    internal static bool ProvidesBelay(Lifeline lifeline)
        => IsNativeBelayEngaged && _ropeBinding?.Owns(lifeline) == true && _ropeBinding.CanProvideBelay;
    internal static bool BeginPersonalRopeOperation(Lifeline lifeline)
        => _ropeBinding?.BeginPersonalOperation(lifeline) == true;
    internal static void EndPersonalRopeOperation(Lifeline lifeline)
        => _ropeBinding?.EndPersonalOperation(lifeline);

    public static void ReleaseAllAnchors()
    {
        var session = _ropeSession;
        _ropeSession = null; _ropeBinding = null; _ropePartner = -1;
        session?.Dispose();
    }

    private static Il2CppTheGameBakers.Cairn.RobotPawnController _localClimbotCached;
    private static int _lastClimbotSearchFrame;
    private static Il2CppTheGameBakers.Cairn.RobotPawnController ResolveLocalClimbot()
    {
        if (_localClimbotCached != null) return _localClimbotCached;
        if (_lastClimbotSearchFrame != 0 && Time.frameCount - _lastClimbotSearchFrame < 30) return null;
        _lastClimbotSearchFrame = Time.frameCount;
        var all = Object.FindObjectsOfType<Il2CppTheGameBakers.Cairn.RobotPawnController>();
        if (all != null) foreach (var robot in all) if (robot != null) return _localClimbotCached = robot;
        return null;
    }

    private sealed class NativeRopeBinding : IRopeBinding
    {
        private readonly Harness _local, _partner;
        private readonly Lifeline _lifeline;
        private LogicalRope _personalRope;
        private readonly LogicalRope _source;
        private readonly float _length;
        private GameObject _root;
        private LogicalRope _rope;
        private LogicalRopePart _part;
        private LogicalRopeRenderer _renderer;
        private GameObject _outfitWithHarness, _outfitWithoutHarness;
        private bool _outfitWithHarnessWasActive, _outfitWithoutHarnessWasActive;
        private int _createdFrame, _personalOperationDepth;
        private Vector3 _lastLocal, _lastPartner;
        private bool _attached, _disposed;

        internal NativeRopeBinding(Harness local, Harness partner, Lifeline lifeline, LogicalRope source, float length)
        {
            _local = local; _partner = partner; _lifeline = lifeline;
            _personalRope = lifeline.securingRope; _source = source; _length = length;
        }
        internal bool Matches(Harness local, Harness partner)
            => _local != null && _partner != null && _local.Pointer == local.Pointer && _partner.Pointer == partner.Pointer;
        internal bool Owns(LogicalRope rope) => _rope != null && rope != null && _rope.Pointer == rope.Pointer;
        internal bool Owns(Lifeline lifeline) => _lifeline != null && lifeline != null && _lifeline.Pointer == lifeline.Pointer;
        internal bool HasBothAttachments => !_disposed && _attached && _rope != null && _local != null && _partner != null
            && _rope.IsAttached(_local.Cast<IRopeHolder>()) && _rope.IsAttached(_partner.Cast<IRopeHolder>());
        internal bool CanProvideBelay => _personalOperationDepth == 0 && HasBothAttachments;

        public bool IsReady
        {
            get
            {
                if (_root == null) Create();
                // Awake/Start and Obi actor loading must finish before creating pins.
                return Time.frameCount > _createdFrame && _rope != null && _part != null
                    && _part.obiRope != null && _part.obiRope.isLoaded && _part.pinConstraintHandler != null;
            }
        }

        private void Create()
        {
            _root = new GameObject("CairnMP.DirectRope");
            _root.SetActive(false);
            var solver = _source.ropeParts != null && _source.ropeParts.Count > 0
                ? _source.ropeParts[0]?.obiRope?.solver : null;
            _root.transform.SetParent(solver != null ? solver.transform : _source.transform.parent, false);
            _rope = _root.AddComponent<LogicalRope>();
            _rope.enabled = false;
            _rope.intializeOnEnable = false;
            _rope.ropePartPrefab = _source.ropePartPrefab;
            _rope.minimalNewDistAfterSplit = _source.minimalNewDistAfterSplit;
            _rope.minimalRemainingDistAfterAttach = _source.minimalRemainingDistAfterAttach;
            _rope.minStretchingScale = _source.minStretchingScale;
            _rope.slackGlobalFactorUser = _source.slackGlobalFactorUser;
            _rope.slackMinOnAttachedPart = _source.slackMinOnAttachedPart;
            _rope.slackOnAttachedPart = _source.slackOnAttachedPart;
            _rope.slackUpdateSpeed = _source.slackUpdateSpeed;
            _rope.compensateBadSimulationStrength = _source.compensateBadSimulationStrength;
            _rope.compensateBadSimulationActive = _source.compensateBadSimulationActive;
            _rope.category = _source.category;
            _rope.masks = _source.masks;
            _rope.MaxLengthMeters = _length;

            // Clone only a segment prefab, never a live rope's holder list or particles.
            var segment = Object.Instantiate(_source.ropePartPrefab.gameObject, _root.transform);
            _part = segment.GetComponent<LogicalRopePart>();
            // LogicalRope.ropeParts is normally populated by Unity prefab deserialization.
            // A LogicalRope created with AddComponent keeps this field null.
            _rope.ropeParts = new Il2CppSystem.Collections.Generic.List<LogicalRopePart>();
            _rope.ropeParts.Add(_part);
            var sourceLine = _source.GetComponent<LineRenderer>() ?? _source.ropePartPrefab.GetComponent<LineRenderer>();
            if (sourceLine != null)
            {
                var line = _root.AddComponent<LineRenderer>();
                line.sharedMaterial = sourceLine.sharedMaterial;
                line.widthMultiplier = sourceLine.widthMultiplier;
                line.widthCurve = sourceLine.widthCurve;
                line.colorGradient = sourceLine.colorGradient;
                line.useWorldSpace = true;
                line.positionCount = 0;
                var renderer = _root.AddComponent<LogicalRopeRenderer>();
                renderer.lineRenderer = line;
                // AddComponent does not deserialize the prefab links. Without these
                // references the renderer can use an empty/stale part list, leaving
                // one end of the visible rope at an invalid world position.
                renderer.parts = _rope.ropeParts;
                _rope.logicalRopeRenderer = renderer;
                _renderer = renderer;
            }
            segment.SetActive(true);
            _root.SetActive(true);
            _createdFrame = Time.frameCount;
        }

        public bool Attach()
        {
            SynchronizePartnerAttachment();
            if (_local == null || _partner == null || _lifeline == null
                || !TryGetHarnessAttachPosition(_local, out _lastLocal)
                || !TryGetHarnessAttachPosition(_partner, out _lastPartner)
                || !RopeAttachmentPolicy.CanReach(_length, Vector3.Distance(_lastLocal, _lastPartner))) return false;
            _rope.ForceInitialize();
            if (!_rope.IsInitialized) return false;
            _part.pinConstraintHandler.DetachAll();
            // Native SharedRopeGamemode uses these same endpoint sides.
            _rope.AttachTo(_local.Cast<IRopeHolder>(), RopeSide.End, true);
            _rope.AttachTo(_partner.Cast<IRopeHolder>(), RopeSide.Begin, true);
            _rope.MaxLengthMeters = _length;
            if (!RopeAttachmentPolicy.TryGetInitialLength(
                    _length, Vector3.Distance(_lastLocal, _lastPartner), out var initialLength))
                return false;
            // Cairn queues rope-length changes for LogicalRope.FixedUpdate. Start at
            // the actual harness separation, plus a small slack, never fully paid out.
            _rope.RequestSetLength(initialLength, false);
            _rope.SetCollisionsFilter(_source.category, _source.masks);
            _rope.TeleportOnAttachPoints();
            _rope.SetVisible(true);
            _renderer?.SyncFromRope(_rope);
            _rope.SyncRenderer();
            _attached = true;
            if (!HasBothAttachments) return false;
            // Cairn's shared-rope mode selects its dedicated rope on the Lifeline.
            // Secured-fall code reads this property again to calculate rope length.
            _lifeline.securingRope = _rope;
            _rope.enabled = true;
            SetHarnessVisual();
            ModLog.Info("[RopeTeam] Direct harness rope attached (no piton), max=" + _length
                + ", initial=" + initialLength);
            return true;
        }

        // The piton path does not enable a component on Harness itself. PawnSkinHandler
        // swaps these two Aava outfit meshes; reproducing only that visual switch keeps
        // the direct cooperative rope out of the piton, inventory and save lifecycles.
        private void SetHarnessVisual()
        {
            try
            {
                var mc = LocalPlayerInterop.TryGetMCGameObject();
                if (mc == null) return;
                var meshes = mc.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (meshes == null) return;
                for (var i = 0; i < meshes.Length; i++)
                {
                    var mesh = meshes[i];
                    if (mesh == null) continue;
                    if (mesh.name == "MC_Outfit")
                    {
                        _outfitWithHarness = mesh.gameObject;
                        _outfitWithHarnessWasActive = _outfitWithHarness.activeSelf;
                        if (!_outfitWithHarnessWasActive) _outfitWithHarness.SetActive(true);
                    }
                    else if (mesh.name == "MC_Outift_NoHarness")
                    {
                        _outfitWithoutHarness = mesh.gameObject;
                        _outfitWithoutHarnessWasActive = _outfitWithoutHarness.activeSelf;
                        if (_outfitWithoutHarnessWasActive) _outfitWithoutHarness.SetActive(false);
                    }
                }
            }
            catch (Exception ex) { ModLog.Warning("[RopeTeam] Could not enable harness visual: " + ex.Message); }
        }

        private void RestoreHarnessVisual()
        {
            try
            {
                // Once our direct rope is detached, IsSecured reports real pitons only.
                // A piton placed during the link must keep its native harness appearance.
                var keepHarness = _lifeline != null && _lifeline.IsSecured(1);
                if (_outfitWithHarness != null)
                {
                    var desired = keepHarness || _outfitWithHarnessWasActive;
                    if (_outfitWithHarness.activeSelf != desired) _outfitWithHarness.SetActive(desired);
                }
                if (_outfitWithoutHarness != null)
                {
                    var desired = !keepHarness && _outfitWithoutHarnessWasActive;
                    if (_outfitWithoutHarness.activeSelf != desired) _outfitWithoutHarness.SetActive(desired);
                }
            }
            catch (Exception ex) { ModLog.Warning("[RopeTeam] Could not restore harness visual: " + ex.Message); }
            finally
            {
                _outfitWithHarness = null;
                _outfitWithoutHarness = null;
            }
        }

        public bool Maintain()
        {
            SynchronizePartnerAttachment();
            if (!HasBothAttachments || !TryGetHarnessAttachPosition(_local, out var local)
                || !TryGetHarnessAttachPosition(_partner, out var partner)) return false;
            // Teleports must not drag solver particles across the world.
            if (Vector3.Distance(local, _lastLocal) > 5 || Vector3.Distance(partner, _lastPartner) > 5) return false;
            _lastLocal = local; _lastPartner = partner;
            return true;
        }

        private void SynchronizePartnerAttachment()
        {
            // A remote harness can have physics disabled while its skeleton is animated.
            // Pins must follow that native skeleton marker, not a stale physics transform.
            if (_partner == null || _partner.IsActivePhysics()) return;
            var marker = _partner.skeletonAttachPointRoot;
            var collider = _partner.GetAttachEnd();
            if (marker == null || collider == null) return;
            collider.transform.SetPositionAndRotation(marker.position, marker.rotation);
            var body = collider.GetComponent<Rigidbody>();
            if (body != null) { body.position = marker.position; body.rotation = marker.rotation; }
        }

        internal bool BeginPersonalOperation(Lifeline lifeline)
        {
            if (!Owns(lifeline) || !_attached || _disposed) return false;
            if (_personalOperationDepth == 0) _lifeline.securingRope = _personalRope;
            _personalOperationDepth++;
            return true;
        }
        internal void EndPersonalOperation(Lifeline lifeline)
        {
            if (!Owns(lifeline) || _personalOperationDepth <= 0) return;
            if (--_personalOperationDepth == 0)
            {
                _personalRope = _lifeline.securingRope;
                if (!_disposed && _attached) _lifeline.securingRope = _rope;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _attached = false;
            try
            {
                if (_lifeline != null && _rope != null && _lifeline.securingRope != null
                    && _lifeline.securingRope.Pointer == _rope.Pointer)
                    _lifeline.securingRope = _personalRope;
            }
            catch (Exception ex) { ModLog.Warning("[RopeTeam] Personal rope restoration failed: " + ex.Message); }
            try { if (_rope != null && _rope.IsInitialized) _rope.DetachAll(); }
            catch (Exception ex) { ModLog.Warning("[RopeTeam] Native detach failed: " + ex.Message); }
            finally
            {
                RestoreHarnessVisual();
                if (_root != null) { _root.SetActive(false); Object.Destroy(_root); }
                _root = null; _rope = null; _part = null; _renderer = null;
            }
        }
    }
}
