using System;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private GameObject _visualRoot;
    private LineRenderer _visualLine;
    private Material _visualMaterial;
    private readonly BelayRopeVisualPlayback _visualPlayback = new();
    private Il2CppStructArray<Vector3> _visualPositions;
    private double _nextVisualCaptureLog;
    private bool _visualCaptureReady;
    private bool _visualUsingFallback;
    private double _nextVisualMaterialAttempt, _nextVisualMaterialLog;

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
            if (_lease?.Session != session || _control == null || _stopping || !_partnerStation.HasValue
                || _localRoutePending || _rebinding || !ContextValid || !MappingMatches())
                return NoRopeVisual("Waiting for active control and a stable native protection route.");
            if (!TryReadPartnerRoute(out _, out var routeReason)) return NoRopeVisual(routeReason);
            var parts = _rope.ropeParts;
            var segments = new WorldPosition[_partnerRouteLegs][];
            var total = 0;
            var rendered = 0;
            for (var i = 0; i < _partnerRouteLegs; i++)
            {
                var part = parts[_partnerRouteSelected[i]];
                var rope = part?.obiRope;
                if (rope == null || !rope.isLoaded) return NoRopeVisual("Native particles are not loaded.");
                var start = _partnerRoutePositions[i]; var end = _partnerRoutePositions[i + 1];
                // Native drawing may correct the rendered endpoints independently
                // of the raw solver particles. Read this exact route part first.
                if (TryReadRenderedRopeSegment(part, start, end, 4096 - total, out var segment, out var rendererReason))
                    rendered++;
                else if (!TryReadParticleRopeSegment(part, start, end, 4096 - total, out segment, out var particleReason))
                {
                    return NoRopeVisual("Segment=" + i + "; renderer: " + rendererReason + "; particles: " + particleReason);
                }
                segments[i] = segment;
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
            if (!EnsureRopeVisualStyle()) return;
            if (!_visualPlayback.SetTarget(session, revision, points, Now)) return;
            if (_visualPositions == null || _visualPositions.Length != points.Length)
                _visualPositions = new Il2CppStructArray<Vector3>((long)points.Length);
            _visualLine.positionCount = points.Length;
            TickRopeVisual();
        }
        catch (Exception exception) { ModLog.SuppressedException("belay.draw-rope", exception); ClearRopeVisual(); }
    }

    private bool EnsureRopeVisualStyle()
    {
        var creating = _visualLine == null;
        if (!creating && (!_visualUsingFallback || Now < _nextVisualMaterialAttempt)) return true;
        if (creating && Now < _nextVisualMaterialAttempt) return false;
        if (creating) ClearRopeVisual();
        _nextVisualMaterialAttempt = Now + 5;

        var template = FindLocalRopeStyle(out var source);
        // A temporary flat fallback can acquire the real local rope style once
        // its renderer becomes available, without rebuilding the drawn geometry.
        if (!creating && template == null) return true;
        Material material;
        if (template != null)
            material = new Material(template.sharedMaterial);
        else
        {
            var shader = SupportedRopeFallbackShader();
            if (shader == null)
            {
                LogRopeVisualMaterial("unavailable", "No native template or supported fallback shader.");
                return false;
            }
            material = new Material(shader);
            source = "flat-fallback";
        }

        var previous = _visualMaterial;
        _visualMaterial = material; // Own the clone before any later native call can fail.
        try
        {
            if (creating)
            {
                _visualRoot = new GameObject("CairnMP.BelayRopeVisual");
                _visualLine = _visualRoot.AddComponent<LineRenderer>();
            }
            _visualLine.sharedMaterial = material;
            _visualLine.useWorldSpace = true;
            if (template != null)
            {
                // Reuse the game's existing LineRenderer material and UV style.
                // Never recolor or mutate the shared native material/renderer.
                _visualLine.widthMultiplier = template.widthMultiplier;
                _visualLine.widthCurve = template.widthCurve;
                _visualLine.colorGradient = template.colorGradient;
                _visualLine.numCapVertices = template.numCapVertices;
                _visualLine.numCornerVertices = template.numCornerVertices;
                _visualLine.alignment = template.alignment;
                _visualLine.textureMode = template.textureMode;
                _visualLine.textureScale = template.textureScale;
                _visualLine.generateLightingData = template.generateLightingData;
                _visualLine.shadowCastingMode = template.shadowCastingMode;
                _visualLine.receiveShadows = template.receiveShadows;
            }
            else
            {
                _visualLine.widthMultiplier = .018f;
                _visualLine.numCapVertices = _visualLine.numCornerVertices = 2;
                _visualLine.startColor = _visualLine.endColor = Color.white;
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
                if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
            }
            _visualUsingFallback = template == null;
            LogRopeVisualMaterial(source, template == null ? "Native rope style is not available yet." : "Cloned local native rope style.");
            return true;
        }
        finally
        {
            // Every material stored here was created by this overlay. The
            // game's template.sharedMaterial remains owned by the game.
            if (previous != null) UnityEngine.Object.Destroy(previous);
        }
    }

    private LineRenderer FindLocalRopeStyle(out string source)
    {
        source = "local-segment";
        var rope = _stationLifeline?.securingRope;
        var parts = rope?.ropeParts;
        if (parts != null && parts.Count <= 64)
            for (var i = parts.Count - 1; i >= 0; i--)
            {
                var part = parts[i];
                if (part?.obiRope?.isLoaded != true) continue;
                var line = part.RopeRenderer;
                if (UsableRopeStyle(line)) return line;
            }
        source = "local-prefab";
        var prefabLine = rope?.ropePartPrefab?.GetComponent<LineRenderer>();
        return UsableRopeStyle(prefabLine) ? prefabLine : null;
    }

    private static bool UsableRopeStyle(LineRenderer line)
    {
        if (line == null || !float.IsFinite(line.widthMultiplier) || line.widthMultiplier <= 0) return false;
        var material = line.sharedMaterial;
        var shader = material?.shader;
        return shader != null && shader.isSupported && shader.name != "Hidden/InternalErrorShader";
    }

    private static Shader SupportedRopeFallbackShader()
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader != null && shader.isSupported) return shader;
        shader = Shader.Find("Sprites/Default");
        if (shader != null && shader.isSupported) return shader;
        shader = Shader.Find("Unlit/Color");
        return shader != null && shader.isSupported ? shader : null;
    }

    private void LogRopeVisualMaterial(string source, string detail)
    {
        if (Now < _nextVisualMaterialLog) return;
        _nextVisualMaterialLog = Now + 5;
        var material = _visualMaterial;
        var shader = material?.shader;
        var textureProperty = material?.HasProperty("_BaseMap") == true ? "_BaseMap"
            : material?.HasProperty("_MainTex") == true ? "_MainTex" : null;
        var hasTexture = textureProperty != null && material.GetTexture(textureProperty) != null;
        ModLog.Info("[BelayVisual] Material source=" + source + ", shader=" + (shader != null ? shader.name : "none")
            + ", supported=" + (shader != null && shader.isSupported)
            + ", textureProperty=" + (textureProperty ?? "other-or-none") + ", hasTexture=" + hasTexture
            + ", textureMode=" + (_visualLine != null ? _visualLine.textureMode.ToString() : "none") + "; " + detail);
    }

    private void TickRopeVisual()
    {
        if (_visualLine == null) return;
        try
        {
            if (_stationSession != _visualPlayback.Session || !Enabled || !ContextValid || !StationValid || Now - _visualPlayback.UpdatedAt >= .75)
            { ClearRopeVisual(); return; }
            var points = _visualPlayback.Sample(Now);
            for (var i = 0; i < points.Length; i++)
            {
                // Field initialization avoids the generated Vector3 constructor
                // and Lerp wrappers, which invoke IL2CPP for each vertex.
                var p = points[i];
                var position = default(Vector3);
                position.x = p.X; position.y = p.Y; position.z = p.Z;
                _visualPositions[i] = position;
            }
            _visualLine.SetPositions(_visualPositions);
        }
        catch (Exception exception) { ModLog.SuppressedException("belay.update-rope-drawing", exception); ClearRopeVisual(); }
    }

    public void ClearRopeVisual()
    {
        if (_visualRoot != null) UnityEngine.Object.Destroy(_visualRoot);
        if (_visualMaterial != null) UnityEngine.Object.Destroy(_visualMaterial);
        _visualRoot = null; _visualLine = null; _visualMaterial = null;
        _visualPositions = null;
        _visualPlayback.Clear();
        _visualUsingFallback = false; _nextVisualMaterialAttempt = 0;
    }
}
