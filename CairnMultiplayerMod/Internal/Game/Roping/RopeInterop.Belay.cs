using System;
using System.Collections.Generic;
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
    private static bool _dropRopeAfterPhysics;
    public static bool HasRopeTeamAnchors => _ropeSession != null;
    public static bool IsNativeBelayEngaged => _ropeSession?.State == RopeBindingState.Attached;

    public static bool UpdateRopeTeamAnchor(int partnerId, Harness partner)
    {
        try
        {
            if (_dropRopeAfterPhysics)
            {
                // Physics lost the harnesses. Drop the drawing and try again. Do not untie.
                ReleaseAllAnchors();
                return true;
            }
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
            // A piton is the catch. Park before Tick so this frame's Maintain does not
            // treat the detach as a failed attachment and untie the pair.
            var duty = HarnessRopeDuty.ForChain(
                tied: true,
                _ropeBinding.HarnessPullParked,
                TeamAnchorSession.Count,
                _ropeBinding.CoilLength);
            if (!duty.Simulate) _ropeBinding.ParkHarnessPull();
            if (_ropeSession.Tick(Time.realtimeSinceStartupAsDouble)) return true;
            var harnessLost = _ropeSession.HarnessLost;
            ReleaseAllAnchors();
            // A missing harness drops the drawing. The next tick builds it again.
            // A rope that cannot be created still unties a real partner.
            return harnessLost;
        }
        catch (Exception ex)
        {
            ModLog.Warning("[RopeTeam] Direct harness attachment failed: " + ex.Message);
            ReleaseAllAnchors();
            return false;
        }
    }

    /// <summary>The coil and the two harnesses of the rope tied for this client.</summary>
    internal static bool TryGetLiveTeamRope(out Harness local, out Harness partner, out float coil)
    {
        local = null;
        partner = null;
        coil = 0f;
        var binding = _ropeBinding;
        if (binding == null || !binding.CanMeasure) return false;
        local = binding.MeasuredLocal;
        partner = binding.MeasuredPartner;
        coil = binding.CoilLength;
        if (float.IsFinite(coil) && coil > 0f) _heldCoil = coil;
        return float.IsFinite(coil) && coil > 0f;
    }

    private static float _heldCoil;

    /// <summary>The last coil length, kept after the leader unclips and the partner rope is gone.</summary>
    internal static bool TryGetHeldCoil(out float coil)
    {
        coil = _heldCoil;
        return float.IsFinite(coil) && coil > 0f;
    }

    internal static bool BeforeRopePhysics(LogicalRope rope)
    {
        // A retie calls ForceInitialize while the session is still waiting. Skipping
        // that FixedUpdate aborts the new rope's setup, and the prefab length stays.
        if (_dropRopeAfterPhysics) return false;
        if (_ropeBinding?.Owns(rope) != true) return true;
        if (!IsNativeBelayEngaged) return true;
        // The harness rope is parked. Skip this FixedUpdate without dropping the tie.
        if (_ropeBinding.HarnessPullParked) return false;
        try
        {
            // SetLengthInstant rejects calls from Update. This prefix is inside the
            // rope's own FixedUpdate, which is the only place Cairn applies it.
            _ropeBinding.ApplyPendingSnap();
            if (_ropeBinding.Maintain()) return true;
        }
        catch (Exception ex) { ModLog.Warning("[RopeTeam] Physics attachment lost: " + ex.Message); }
        // Destroying the rope from this prefix closes the process. Update drops it.
        _dropRopeAfterPhysics = true;
        return false;
    }

    internal static void AfterRopePhysics(LogicalRope rope)
    {
        if (_dropRopeAfterPhysics) return;
        if (_ropeBinding?.Owns(rope) != true || !IsNativeBelayEngaged) return;
        try { _ropeBinding.FinishSnapAfterPhysics(); }
        catch (Exception ex) { ModLog.Warning("[RopeTeam] Could not lay the rope after physics: " + ex.Message); }
    }

    internal static void AfterRopeRendered(LogicalRopeRenderer renderer)
    {
        if (_ropeBinding?.OwnsRenderer(renderer) != true) return;
        try { _ropeBinding.DrawHarnessLine(); }
        catch (Exception ex) { ModLog.Warning("[RopeTeam] Could not draw the harness line: " + ex.Message); }
    }

    public static void ReleaseAllAnchors()
    {
        var session = _ropeSession;
        _ropeSession = null; _ropeBinding = null; _ropePartner = -1;
        _dropRopeAfterPhysics = false;
        session?.Dispose();
    }

    /// <summary>
    /// The visible rope. A two-point span sags in the middle. A longer span sags on each
    /// segment, which is how the line runs through pitons and up to a planted staff.
    /// </summary>
    private static void DrawRopeSpan(LineRenderer line, List<Vector3> points, bool taut)
    {
        if (line == null || points == null || points.Count < 2)
        {
            if (line != null) line.enabled = false;
            return;
        }

        line.useWorldSpace = true;
        if (points.Count == 2)
        {
            const int pointCount = 12;
            var span = Vector3.Distance(points[0], points[1]);
            var sag = taut ? 0f : Mathf.Min(0.35f, span * 0.08f);
            line.positionCount = pointCount;
            for (var i = 0; i < pointCount; i++)
            {
                var t = i / (float)(pointCount - 1);
                var point = Vector3.Lerp(points[0], points[1], t);
                point.y -= 4f * t * (1f - t) * sag;
                line.SetPosition(i, point);
            }
            line.enabled = true;
            return;
        }

        const int samples = 4;
        var segments = points.Count - 1;
        line.positionCount = segments * samples + 1;
        var index = 0;
        for (var segment = 0; segment < segments; segment++)
        {
            var from = points[segment];
            var to = points[segment + 1];
            var sag = taut ? 0f : Mathf.Min(0.35f, Vector3.Distance(from, to) * 0.08f);
            for (var i = 0; i < samples; i++)
            {
                var t = i / (float)samples;
                var point = Vector3.Lerp(from, to, t);
                point.y -= 4f * t * (1f - t) * sag;
                line.SetPosition(index++, point);
            }
        }
        line.SetPosition(index, points[points.Count - 1]);
        line.enabled = true;
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
        private readonly LogicalRope _source;
        private readonly float _length;
        private GameObject _root;
        private LogicalRope _rope;
        private LogicalRopePart _part;
        private LogicalRopeRenderer _renderer;
        private GameObject _outfitWithHarness, _outfitWithoutHarness;
        private bool _outfitWithHarnessWasActive, _outfitWithoutHarnessWasActive;
        private int _createdFrame;
        private Vector3 _lastLocal, _lastPartner;
        private const int MaxSnapAttempts = 8;
        private bool _attached, _disposed;
        private float _pendingSnapLength = -1f;
        private int _snapAttempts;
        private bool _harnessLineLogged;
        private readonly List<Vector3> _linePoints = new();
        private readonly List<System.Numerics.Vector3> _chainPoints = new();
        private readonly List<uint> _chainIds = new();

        internal NativeRopeBinding(Harness local, Harness partner, Lifeline lifeline, LogicalRope source, float length)
        {
            _local = local; _partner = partner; _lifeline = lifeline;
            _source = source; _length = length;
        }
        internal bool Matches(Harness local, Harness partner)
            => _local != null && _partner != null && _local.Pointer == local.Pointer && _partner.Pointer == partner.Pointer;
        internal bool Owns(LogicalRope rope) => _rope != null && rope != null && _rope.Pointer == rope.Pointer;
        internal bool OwnsRenderer(LogicalRopeRenderer renderer)
            => _renderer != null && renderer != null && _renderer.Pointer == renderer.Pointer;
        internal bool HasBothAttachments => !_disposed && _attached && _rope != null && _local != null && _partner != null
            && _rope.IsAttached(_local.Cast<IRopeHolder>()) && _rope.IsAttached(_partner.Cast<IRopeHolder>());
        internal bool CanMeasure => !_disposed && _local != null && _partner != null;
        internal Harness MeasuredLocal => _local;
        internal Harness MeasuredPartner => _partner;
        internal float CoilLength => _length;
        internal bool HarnessPullParked => _pullParked;
        private bool _pullParked;

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
            var line = _root.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 0;
            line.enabled = false;
            if (sourceLine != null)
            {
                line.sharedMaterial = sourceLine.sharedMaterial;
                line.widthMultiplier = sourceLine.widthMultiplier;
                line.widthCurve = sourceLine.widthCurve;
                line.colorGradient = sourceLine.colorGradient;
                line.numCapVertices = sourceLine.numCapVertices;
                line.numCornerVertices = sourceLine.numCornerVertices;
                line.alignment = sourceLine.alignment;
                line.textureMode = sourceLine.textureMode;
                line.shadowCastingMode = sourceLine.shadowCastingMode;
                line.receiveShadows = sourceLine.receiveShadows;
            }
            else
            {
                line.widthMultiplier = 0.02f;
                var shader = Shader.Find("Sprites/Default");
                if (shader != null) line.material = new Material(shader);
            }
            var renderer = _root.AddComponent<LogicalRopeRenderer>();
            renderer.lineRenderer = line;
            // AddComponent does not deserialize the prefab links. Without these
            // references the renderer can use an empty/stale part list, leaving
            // one end of the visible rope at an invalid world position.
            renderer.parts = _rope.ropeParts;
            renderer.shouldUseRopeLineRendererLateUpdate = false;
            _rope.logicalRopeRenderer = renderer;
            _rope.shouldUseRopeLineRendererLateUpdate = false;
            _renderer = renderer;
            segment.SetActive(true);
            _root.SetActive(true);
            // The cloned part's mesh follows its prefab particles in a straight line.
            // That mesh stays off. The harness line is written onto our renderer.
            SetRopeRendered(false);
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
            // Cairn applies rope length from LogicalRope.FixedUpdate. Calling
            // SetLengthInstant here, during Update, is rejected and leaves the
            // cloned part's full-length particles in place. The physics prefix
            // shortens the rope and lays it between the harnesses.
            _pendingSnapLength = initialLength;
            _snapAttempts = 0;
            _rope.SetCollisionsFilter(_source.category, _source.masks);
            // SetVisible keeps the simulation running. The cloned mesh stays off,
            // and the visible line is the short one between the harnesses.
            _rope.SetVisible(true);
            _rope.shouldUseRopeLineRendererLateUpdate = false;
            SetRopeRendered(false);
            _attached = true;
            if (!HasBothAttachments) return false;
            // Never the Lifeline's securing rope. Cairn would hang new quickdraws on it, measure
            // the rope left from a piton it does not hold (shown as "0 m") and look for that
            // piton in a secured fall. The climber's own rope and pitons keep protecting them.
            _rope.enabled = true;
            SetHarnessVisual();
            DrawHarnessLine();
            ModLog.Info("[RopeTeam] Direct harness rope attached (no piton), max=" + _length
                + ", initial=" + initialLength);
            // Tick can attach on the same frame the chain already has a piton.
            // Drop the pins again so that attach does not leave the rope pulling.
            if (_pullParked) ReleaseHarnessPins();
            return true;
        }

        /// <summary>
        /// A piton is on the chain. Detach the harness rope and leave it out of the
        /// physics step. The line renderer and the coil length stay on this binding.
        /// </summary>
        internal void ParkHarnessPull()
        {
            if (_pullParked || _disposed) return;
            _pullParked = true;
            _pendingSnapLength = -1f;
            ReleaseHarnessPins();
            ModLog.Info("[RopeTeam] A piton is on the chain. The harness rope stops pulling.");
        }

        private void ReleaseHarnessPins()
        {
            try
            {
                if (_rope != null && _rope.IsInitialized) _rope.DetachAll();
            }
            catch (Exception ex)
            {
                ModLog.Warning("[RopeTeam] Could not detach the harness rope: " + ex.Message);
            }

            if (_rope != null) _rope.enabled = false;
            if (_part != null && _part.gameObject != null) _part.gameObject.SetActive(false);
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
                // IsSecured counts real pitons only: one placed during the link keeps its
                // native harness appearance.
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

        internal void ApplyPendingSnap()
        {
            if (_pendingSnapLength < 0f || _rope == null || !_rope.IsInitialized) return;
            var length = _pendingSnapLength;
            _rope.SetLengthInstant(length);
            _rope.RequestSetLength(length, false, true);
            _rope.TeleportOnAttachPoints(RopeTeleportMode.PreTeleportOnLineAndFabrik, false, false);
            _snapAttempts++;
        }

        internal void FinishSnapAfterPhysics()
        {
            DrawHarnessLine();
            if (_pendingSnapLength < 0f || _rope == null || !_rope.IsInitialized) return;
            var length = _pendingSnapLength;
            LayParticlesOnSegment();
            _rope.TeleportOnAttachPoints(RopeTeleportMode.PreTeleportOnLineAndFabrik, false, false);
            float reported;
            try { reported = _rope.GetLength(false, true); }
            catch (Exception ex)
            {
                ModLog.Warning("[RopeTeam] Could not read the paid-out length: " + ex.Message);
                reported = float.NaN;
            }
            // GetLength stays 0 in game, so this only stops the physics retries.
            // The visible line is drawn from the harnesses either way.
            if (!RopeAttachmentPolicy.SnapLanded(length, reported) && _snapAttempts < MaxSnapAttempts)
                return;
            _pendingSnapLength = -1f;
            ModLog.Info("[RopeTeam] Rope length request finished, requested=" + length
                + ", reported=" + reported);
        }

        internal void DrawHarnessLine()
        {
            SetRopeRendered(false);
            var line = _renderer != null ? _renderer.lineRenderer : null;
            if (_disposed || line == null) return;
            if (_rope != null) _rope.shouldUseRopeLineRendererLateUpdate = false;
            if (_renderer != null) _renderer.shouldUseRopeLineRendererLateUpdate = false;
            var held = TeamAnchorSession.LeaderLeft;
            // The leader's harness is off this rope. The follower's line is drawn
            // on its own, including on the leader's screen for the practice ghost.
            if (held && TeamAnchorSession.IsLocalLeader)
            {
                line.enabled = false;
                return;
            }

            var partner = default(Vector3);
            if (!TryGetHarnessAttachPosition(_local, out var local)
                || !IsFinitePosition(local)
                || (!held && (!TryGetHarnessAttachPosition(_partner, out partner) || !IsFinitePosition(partner))))
            {
                line.enabled = false;
                return;
            }

            _linePoints.Clear();
            _chainPoints.Clear();
            _chainIds.Clear();
            if (TeamAnchorSession.CopyPath(_chainPoints))
            {
                // The stored point is the piton in the rock. The rope runs through the
                // quickdraw's rope carabiner, which hangs off that piton and swings.
                // A staff anchor ties at the top of the planted stick instead.
                TeamAnchorSession.CopyIds(_chainIds);
                if (TeamAnchorSession.IsLocalLeader) _linePoints.Add(partner);
                else _linePoints.Add(local);
                for (var i = 0; i < _chainPoints.Count; i++)
                {
                    var stored = _chainPoints[i];
                    var id = i < _chainIds.Count ? _chainIds[i] : 0;
                    var point = ChainDrawPoint(id, new System.Numerics.Vector3(stored.X, stored.Y, stored.Z));
                    if (IsFinitePosition(point)) _linePoints.Add(point);
                }

                if (!held) _linePoints.Add(TeamAnchorSession.IsLocalLeader ? local : partner);
            }
            else if (!held)
            {
                _linePoints.Add(local);
                _linePoints.Add(partner);
            }
            else
            {
                line.enabled = false;
                return;
            }

            // The line goes straight once the coil is paid out. The sag is only drawn while there is slack.
            var length = 0f;
            for (var i = 1; i < _linePoints.Count; i++)
                length += Vector3.Distance(_linePoints[i - 1], _linePoints[i]);
            DrawSagged(line, _linePoints, RopeLengthPolicy.IsPaidOut(length, _length));
            if (_harnessLineLogged) return;
            _harnessLineLogged = true;
            ModLog.Info("[RopeTeam] Rope drawn between the harnesses, points=" + _linePoints.Count);
        }

        private static void DrawSagged(LineRenderer line, List<Vector3> points, bool taut)
            => DrawRopeSpan(line, points, taut);

        private static bool IsFinitePosition(Vector3 point)
            => float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z);

        private void LayParticlesOnSegment()
        {
            var actor = _part?.obiRope;
            if (actor == null || !actor.isLoaded) return;
            int count = actor.activeParticleCount;
            if (count < 2) return;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                actor.TeleportParticle(i, Vector3.Lerp(_lastLocal, _lastPartner, t));
            }
        }

        private void SetRopeRendered(bool rendered)
        {
            if (_part != null)
            {
                var renderers = _part.GetComponentsInChildren<Renderer>(true);
                if (renderers != null)
                {
                    for (int i = 0; i < renderers.Length; i++)
                        if (renderers[i] != null) renderers[i].enabled = rendered;
                }
            }
        }

        public bool Maintain()
        {
            if (_pullParked) return MaintainParkedLine();
            SynchronizePartnerAttachment();
            if (!HasBothAttachments || !TryGetHarnessAttachPosition(_local, out var local)
                || !TryGetHarnessAttachPosition(_partner, out var partner)) return false;
            // A long move snaps the particles onto the new segment. Dragging them
            // across the mountain is what the guard used to prevent by dropping the tie.
            FollowHarnesses(local, partner);
            return true;
        }

        /// <summary>
        /// The rope is not pulling. A missing pin is expected. A harness that jumps
        /// past the guard snaps the line. It does not end the tie.
        /// </summary>
        private bool MaintainParkedLine()
        {
            if (!TryGetHarnessAttachPosition(_local, out var local)
                || !TryGetHarnessAttachPosition(_partner, out var partner)) return false;
            FollowHarnesses(local, partner);
            return true;
        }

        /// <summary>Remember the harnesses. A jump lays the rope on the new segment and keeps the tie.</summary>
        private void FollowHarnesses(Vector3 local, Vector3 partner)
        {
            var jumped = RopeAttachmentPolicy.NeedsSnap(Vector3.Distance(local, _lastLocal))
                || RopeAttachmentPolicy.NeedsSnap(Vector3.Distance(partner, _lastPartner));
            _lastLocal = local;
            _lastPartner = partner;
            if (jumped)
            {
                try
                {
                    if (_rope != null && _rope.IsInitialized && !_pullParked)
                    {
                        _rope.TeleportOnAttachPoints(RopeTeleportMode.PreTeleportOnLineAndFabrik, false, false);
                        LayParticlesOnSegment();
                    }
                }
                catch (Exception ex)
                {
                    ModLog.Warning("[RopeTeam] Could not snap the rope after a harness jump: " + ex.Message);
                }

                ModLog.Info("[RopeTeam] Harness jumped. The rope snapped and the tie stayed.");
            }

            DrawHarnessLine();
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _attached = false;
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
