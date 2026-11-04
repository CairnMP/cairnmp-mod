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
            for (var i = first; i < parts.Count; i++)
            {
                var rope = parts[i]?.obiRope;
                if (rope == null || !rope.isLoaded) return NoRopeVisual("Native particles are not loaded.");
                // Cairn's GetParticleCount returns the structural element count;
                // GetParticlePositions appends each first particle AND the final second particle.
                var elements = ObiRopeExtensions.GetParticleCount(rope);
                if (elements < 1 || elements >= 4096) return NoRopeVisual("Native element count is outside the drawing budget.");
                var count = elements + 1;
                if ((total += count) > 4096) return NoRopeVisual("Native route exceeds the particle sampling budget.");
                var particles = ObiRopeExtensions.GetParticlePositions(rope);
                if (particles == null || particles.Count != count) return NoRopeVisual("Native particle/element counts disagree.");
                // The native extension reads solver.positions directly (solver space).
                var solver = rope.solver;
                if (solver == null) return NoRopeVisual("The segment solver is not available.");
                var transform = solver.transform;
                var start = holders[i].Position; var end = holders[i + 1].Position;
                var a = transform.TransformPoint(particles[0]); var b = transform.TransformPoint(particles[count - 1]);
                var reverse = (a - end).sqrMagnitude + (b - start).sqrMagnitude < (a - start).sqrMagnitude + (b - end).sqrMagnitude;
                if (Vector3.Distance(reverse ? b : a, start) > 1 || Vector3.Distance(reverse ? a : b, end) > 1)
                    return NoRopeVisual("Native particles have not settled at the segment attachments.");
                var segment = segments[i - first] = new WorldPosition[count];
                for (var j = 0; j < count; j++)
                {
                    var p = transform.TransformPoint(particles[reverse ? count - 1 - j : j]);
                    if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z))
                        return NoRopeVisual("A native particle position is not finite.");
                    segment[j] = new(p.x, p.y, p.z);
                }
            }
            points = BelayRopeVisualSampling.Sample(segments, 192);
            if (points != null && !_visualCaptureReady)
            {
                ModLog.Info("[BelayVisual] Native drawing ready; session=" + session + ", segments=" + segments.Length + ", points=" + points.Length);
                _visualCaptureReady = true;
            }
            return points != null;
        }
        catch (Exception exception) { ModLog.SuppressedException("belay.capture-rope-drawing", exception); return false; }
    }

    public void ShowRopeVisual(Guid session, int climber, uint revision, WorldPosition[] points)
    {
        try
        {
            if (_stationSession != session || !StationValid || points == null || points.Length < 2 || points.Length > 192) return;
            if (_visualLine == null)
            {
                var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                if (shader == null) return;
                _visualMaterial = new Material(shader);
                _visualRoot = new GameObject("CairnMP.BelayRopeVisual");
                _visualLine = _visualRoot.AddComponent<LineRenderer>();
                _visualLine.sharedMaterial = _visualMaterial;
                _visualLine.useWorldSpace = true; _visualLine.widthMultiplier = .018f;
                _visualLine.numCapVertices = _visualLine.numCornerVertices = 2;
                _visualLine.startColor = _visualLine.endColor = RemotePlayerManager.ColorForPlayer(climber);
                ModLog.Info("[BelayVisual] Receiving the climber's native rope drawing; session=" + session);
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
