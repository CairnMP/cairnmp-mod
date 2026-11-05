using System;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private GameObject _visualRoot;
    private LineRenderer _visualLine;
    private Material _visualMaterial;
    private Vector3[] _visualFrom, _visualTarget;
    private Guid _visualSession;
    private uint _visualRevision;
    private double _visualUpdated;
    private double _nextVisualCaptureLog;
    private bool _visualCaptureReady;

    private bool NoRopeVisual(string reason)
    {
        _visualCaptureReady = false;
        if (Now >= _nextVisualCaptureLog)
        {
            _nextVisualCaptureLog = Now + 5;
            ModLog.Info("[BelayVisual] Native drawing unavailable; " + reason);
        }
        return false;
    }

    public bool TryCaptureRopeVisual(Guid session, out WorldPosition[] points)
    {
        points = null;
        try
        {
            if (_lease?.Session != session || _control == null || _stopping || _localExercise || !_partnerStation.HasValue
                || _localRoutePending || _rebinding || !ContextValid || !MappingMatches())
                return NoRopeVisual("Waiting for active control and a stable native protection route.");
            var holders = _rope.ropeHolders;
            var parts = _rope.ropeParts;
            if (holders == null || parts == null || parts.Count > 64 || parts.Count != holders.Count - 1)
                return NoRopeVisual("Native holder/segment counts do not yet agree.");
            var first = -1;
            for (var i = 0; i < holders.Count; i++)
                if (Id(holders[i]?.TryCast<LogicalQuickdraw>()?.Piton) == Id(_localStationPiton))
                { if (first >= 0) return NoRopeVisual("Station repeats in the native holder walk."); first = i; }
            if (first < 0 || first >= parts.Count) return NoRopeVisual("The agreed station has no outgoing segment.");
            var segments = new WorldPosition[parts.Count - first][];
            var total = 0;
            var rendered = 0;
            for (var i = first; i < parts.Count; i++)
            {
                var part = parts[i];
                var rope = part?.obiRope;
                if (rope == null || !rope.isLoaded) return NoRopeVisual("Native particles are not loaded.");
                var start = holders[i].Position; var end = holders[i + 1].Position;
                // Native drawing may correct the rendered endpoints independently
                // of the raw solver particles. Read this exact route part first.
                if (TryReadRenderedRopeSegment(part, start, end, 4096 - total, out var segment, out var rendererReason))
                    rendered++;
                else if (!TryReadParticleRopeSegment(part, start, end, 4096 - total, out segment, out var particleReason))
                {
                    return NoRopeVisual("Segment=" + i + "; renderer: " + rendererReason + "; particles: " + particleReason);
                }
                segments[i - first] = segment;
                total += segment.Length;
            }
            points = BelayRopeVisualSampling.Sample(segments, 192);
            if (points != null && !_visualCaptureReady)
            {
                ModLog.Info("[BelayVisual] Native drawing ready; session=" + session + ", segments=" + segments.Length
                    + ", points=" + points.Length + ", renderedSegments=" + rendered);
                _visualCaptureReady = true;
            }
            return points != null;
        }
        catch (Exception exception) { ModLog.SuppressedException("belay.capture-rope-drawing", exception); return false; }
    }

    private static bool TryReadRenderedRopeSegment(LogicalRopePart part, Vector3 start, Vector3 end, int maximum,
        out WorldPosition[] segment, out string reason)
    {
        segment = null;
        reason = "No native segment renderer is available.";
        try
        {
            var line = part.RopeRenderer;
            if (line == null) return false;
            var count = line.positionCount;
            reason = "Native renderer point count is outside the remaining drawing budget: " + count;
            if (count < 2 || count > maximum) return false;
            var worldSpace = line.useWorldSpace;
            var transform = worldSpace ? null : line.transform;
            segment = new WorldPosition[count];
            for (var j = 0; j < count; j++)
            {
                var p = line.GetPosition(j);
                if (!worldSpace) p = transform.TransformPoint(p);
                segment[j] = new(p.x, p.y, p.z);
            }
            var valid = OrientRopeVisualSegment(segment, start, end, out reason);
            if (!valid) reason = "worldSpace=" + worldSpace + ", " + reason;
            return valid;
        }
        catch (Exception exception) { reason = "Native renderer read failed: " + exception.GetType().Name; return false; }
    }

    private static bool TryReadParticleRopeSegment(LogicalRopePart part, Vector3 start, Vector3 end, int maximum,
        out WorldPosition[] segment, out string reason)
    {
        segment = null;
        reason = "Native solver particles are unavailable.";
        try
        {
            var rope = part.obiRope;
            // GetParticleCount returns structural elements; positions also include
            // the final element's second particle, so the list has one extra point.
            var elements = ObiRopeExtensions.GetParticleCount(rope);
            if (elements < 1 || elements >= maximum)
            { reason = "Native element count is outside the remaining drawing budget: " + elements; return false; }
            var count = elements + 1;
            var particles = ObiRopeExtensions.GetParticlePositions(rope);
            if (particles == null || particles.Count != count)
            { reason = "Native particle/element counts disagree."; return false; }
            var solver = rope.solver;
            if (solver == null) { reason = "The segment solver is not available."; return false; }
            var transform = solver.transform;
            segment = new WorldPosition[count];
            for (var j = 0; j < count; j++)
            {
                var p = transform.TransformPoint(particles[j]);
                segment[j] = new(p.x, p.y, p.z);
            }
            return OrientRopeVisualSegment(segment, start, end, out reason);
        }
        catch (Exception exception) { reason = "Native particle read failed: " + exception.GetType().Name; return false; }
    }

    private static bool OrientRopeVisualSegment(WorldPosition[] segment, Vector3 start, Vector3 end, out string reason)
    {
        if (!BelayRopeVisualSampling.TryOrient(segment, new(start.x, start.y, start.z), new(end.x, end.y, end.z),
            out var reverse, out var startError, out var endError))
        {
            reason = FormattableString.Invariant($"Native endpoints are not ready; startError={startError:F3} m, endError={endError:F3} m.");
            return false;
        }
        if (reverse) Array.Reverse(segment);
        reason = "";
        return true;
    }

    public void ShowRopeVisual(Guid session, int climber, uint revision, WorldPosition[] points)
    {
        try
        {
            if (_stationSession != session || !StationValid || points == null || points.Length < 2 || points.Length > 192) return;
            if (_visualLine == null)
            {
                var spriteShader = Shader.Find("Sprites/Default");
                var shader = spriteShader ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                if (shader == null) return;
                _visualMaterial = new Material(shader);
                _visualRoot = new GameObject("CairnMP.BelayRopeVisual");
                _visualLine = _visualRoot.AddComponent<LineRenderer>();
                _visualLine.sharedMaterial = _visualMaterial;
                _visualLine.useWorldSpace = true; _visualLine.widthMultiplier = .018f;
                _visualLine.numCapVertices = _visualLine.numCornerVertices = 2;
                var color = RemotePlayerManager.ColorForPlayer(climber);
                // Sprites/Default multiplies the vertex color. The fallback unlit
                // shaders use a material tint instead; keep vertices white there.
                _visualLine.startColor = _visualLine.endColor = spriteShader != null ? color : Color.white;
                var tint = spriteShader != null ? Color.white : color;
                if (_visualMaterial.HasProperty("_BaseColor")) _visualMaterial.SetColor("_BaseColor", tint);
                if (_visualMaterial.HasProperty("_Color")) _visualMaterial.SetColor("_Color", tint);
                ModLog.Info("[BelayVisual] Receiving the climber's native rope drawing; session=" + session + ", shader=" + shader.name);
            }
            var blend = _visualSession == session && _visualRevision == revision && _visualTarget?.Length == points.Length;
            _visualFrom = new Vector3[points.Length]; _visualTarget = new Vector3[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                var p = points[i];
                _visualTarget[i] = new(p.X, p.Y, p.Z);
                _visualFrom[i] = blend && _visualLine.positionCount == points.Length ? _visualLine.GetPosition(i) : _visualTarget[i];
            }
            _visualLine.positionCount = points.Length;
            _visualSession = session; _visualRevision = revision; _visualUpdated = Now;
            TickRopeVisual();
        }
        catch (Exception exception) { ModLog.SuppressedException("belay.draw-rope", exception); ClearRopeVisual(); }
    }

    private void TickRopeVisual()
    {
        if (_visualLine == null) return;
        try
        {
            if (_stationSession != _visualSession || !Enabled || !ContextValid || !StationValid || Now - _visualUpdated >= .75)
            { ClearRopeVisual(); return; }
            var t = Mathf.Clamp01((float)((Now - _visualUpdated) / .1));
            for (var i = 0; i < _visualTarget.Length; i++) _visualLine.SetPosition(i, Vector3.Lerp(_visualFrom[i], _visualTarget[i], t));
        }
        catch (Exception exception) { ModLog.SuppressedException("belay.update-rope-drawing", exception); ClearRopeVisual(); }
    }

    public void ClearRopeVisual()
    {
        if (_visualRoot != null) UnityEngine.Object.Destroy(_visualRoot);
        if (_visualMaterial != null) UnityEngine.Object.Destroy(_visualMaterial);
        _visualRoot = null; _visualLine = null; _visualMaterial = null;
        _visualFrom = _visualTarget = null; _visualSession = Guid.Empty;
    }
}
