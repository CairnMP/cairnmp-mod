using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Players;

internal static unsafe class PawnCaptureInterop
{
    private static NetplayPawnCapture _typedPawnCaptureCached;
    private static NetplayPawnCapture _typedClimbotCaptureCached;
    private static int _lastTypedCaptureSearchFrame;
    private static float _nextPlayerCaptureRetryAt;
    private static float _nextClimbotCaptureRetryAt;
    private static float _lastPlayerCaptureFailureLogAt;
    private static float _lastClimbotCaptureFailureLogAt;
    private static bool _localPlayerFallbackCaptureLogged;
    // Last flags byte produced by a successful native capture (PawnState + PawnFlags, the
    // Player target being 0). The fallback capture replays it instead of claiming Walking:
    // those bits drive the target pose the remote rig resolves to (climb vs walk, secured),
    // and they also gate remote teleports, which only accept a partner who is Walking.
    private static byte _lastNativePlayerFlags;
    // Il2CppExceptions are heavy (stack traces marshalled from native).
    // If the native capture fails, we space out the retries so we don't tank
    // the frame when the pathology persists (player near a piton, bivouac, etc.).
    private const float CaptureRetryDelaySeconds = 2f;
    private const float CaptureFailureLogIntervalSeconds = 10f;
    // PawnState (bits 0-2) + PawnFlags (bits 3-4). Bits 5-7 carry PawnTarget, which the
    // native SetFrame asserts to be Player on a player frame; the capture already reports 0
    // there, and masking keeps a stale target byte from ever reaching that assertion.
    private const byte PlayerFlagsMask = 0x1F;

    /// <summary>Forgets the scene-bound pawn-capture references (called on scene reload):
    /// Cairn destroys then recreates these objects after a death/reload, and keeping the
    /// old pointers can crash on the first CaptureFrame.</summary>
    internal static void ResetCaches()
    {
        _typedPawnCaptureCached = null;
        _typedClimbotCaptureCached = null;
        _lastTypedCaptureSearchFrame = 0;
        _nextPlayerCaptureRetryAt = 0f;
        _nextClimbotCaptureRetryAt = 0f;
        _lastPlayerCaptureFailureLogAt = 0f;
        _lastClimbotCaptureFailureLogAt = 0f;
        _localPlayerFallbackCaptureLogged = false;
        // Invalid (0) until the native capture tells us otherwise: announcing a stale
        // Walking would let a partner teleport onto a pawn whose state we do not know.
        _lastNativePlayerFlags = 0;
    }

    /// <summary>Captures the local player's native frame via the game's Netplay pipeline.</summary>
    public static bool TryCaptureLocalPlayerFrame(out NetFrameData frameData)
    {
        frameData = default;

        var capture = TryGetTypedPawnCapture();
        if (capture == null) return false;
        if (!IsPlayerCaptureReady(capture))
        {
            BackOffCaptureRetry(ref _nextPlayerCaptureRetryAt, "player", "capture object is not ready", ref _lastPlayerCaptureFailureLogAt);
            return false;
        }

        if (Time.unscaledTime >= _nextPlayerCaptureRetryAt)
        {
            try
            {
                var frame = capture.CaptureFrame();
                // An Invalid PawnState is NOT a capture failure. The native
                // NetplayPawnCapture.GetPlayerPawnState (VA 0x18315DBF0) only maps
                // PawnControllerSwitcher.mode Walking -> Walking and Climbing -> Climbing;
                // every other mode (None while transitioning, Flying, Hovering) falls through
                // to Invalid, while the captured bones stay perfectly good. Rejecting those
                // frames armed a 2s back-off and pushed the fallback capture instead, whose
                // hardcoded flags claim "Walking" -- so a climber transitioning (secured fall,
                // abseil, wall <-> ground) was replicated with the wrong target pose, or not
                // at all once the frames went stale on the other side (root-pose T-pose).
                if (frame != null && frame.isValid)
                {
                    frameData = ToNetFrameData(frame);
                    if (frameData.IsValid && frameData.Positions != null && frameData.Positions.Length > 0)
                    {
                        _lastNativePlayerFlags = (byte)(frameData.Flags & PlayerFlagsMask);
                        return true;
                    }
                }

                BackOffCaptureRetry(ref _nextPlayerCaptureRetryAt, "player", "native frame is invalid", ref _lastPlayerCaptureFailureLogAt);
            }
            catch (Exception ex)
            {
                BackOffCaptureFailure("player", ex, ref _nextPlayerCaptureRetryAt, ref _lastPlayerCaptureFailureLogAt);
            }
        }

        return TryCaptureLocalPlayerFallbackFrame(capture, out frameData);
    }

    private static float _lastPawnStateCheckTime;
    private static NetFrame.PawnStateType _lastPawnState = NetFrame.PawnStateType.Invalid;

    /// <summary>
    /// Current state of the local pawn (Climbing / Walking / Falling / Dead...), read via the
    /// Netplay capture with throttling (~10 Hz) because belay reconciliation calls it
    /// every frame. Invalid if unavailable.
    /// </summary>
    public static NetFrame.PawnStateType GetLocalPawnState()
    {
        if (Time.unscaledTime - _lastPawnStateCheckTime < 0.1f)
            return _lastPawnState;
        _lastPawnStateCheckTime = Time.unscaledTime;

        try
        {
            var capture = TryGetTypedPawnCapture();
            if (capture == null) return _lastPawnState = NetFrame.PawnStateType.Invalid;

            var frame = capture.CaptureFrame();
            if (frame == null || !frame.isValid) return _lastPawnState = NetFrame.PawnStateType.Invalid;

            return _lastPawnState = frame.PawnState;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("pawn.capture-local-state", exception);
            return _lastPawnState = NetFrame.PawnStateType.Invalid;
        }
    }

    /// <summary>True if the local pawn is walking on the ground (Walking) — a safe basis for allowing a teleport.</summary>
    public static bool IsLocalPlayerWalking() => GetLocalPawnState() == NetFrame.PawnStateType.Walking;

    /// <summary>
    /// Decodes the PawnState (Walking / Climbing / Falling / Dead) encoded in the `flags` byte
    /// of a remote NetFrameData: we rebuild a native NetFrame and read its PawnState
    /// getter (which derives from the flags bits). Invalid if the frame is absent/unreadable.
    /// Used for teleport gating (we only teleport to a player who is WALKING).
    /// </summary>
    public static NetFrame.PawnStateType GetPawnStateFromFrame(NetFrameData data)
    {
        if (!data.IsValid) return NetFrame.PawnStateType.Invalid;
        try
        {
            var frame = new NetFrame { isValid = true, flags = data.Flags };
            return frame.PawnState;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("pawn.decode-state", exception);
            return NetFrame.PawnStateType.Invalid;
        }
    }

    /// <summary>Captures the local climbot's native frame via the game's Netplay pipeline.</summary>
    public static bool TryCaptureLocalClimbotFrame(out NetFrameData frameData)
    {
        frameData = default;
        if (Time.unscaledTime < _nextClimbotCaptureRetryAt) return false;

        var capture = TryGetTypedClimbotCapture();
        if (capture == null) return false;
        if (!IsCaptureObjectReady(capture, NetFrame.PawnTargetType.Climbot))
        {
            BackOffCaptureRetry(ref _nextClimbotCaptureRetryAt, "climbot", "capture object is not ready", ref _lastClimbotCaptureFailureLogAt);
            return false;
        }

        try
        {
            var frame = capture.CaptureFrame();
            if (frame == null || !frame.isValid || frame.ClimbotPawnState == NetFrame.ClimbotPawnStateType.Invalid)
                return false;

            frameData = ToNetFrameData(frame);
            return frameData.IsValid && frameData.Positions != null && frameData.Positions.Length > 0;
        }
        catch (Exception ex)
        {
            _typedClimbotCaptureCached = null;
            BackOffCaptureFailure("climbot", ex, ref _nextClimbotCaptureRetryAt, ref _lastClimbotCaptureFailureLogAt);
            return false;
        }
    }

    private static bool IsPlayerCaptureReady(NetplayPawnCapture capture)
    {
        return IsCaptureObjectReady(capture, NetFrame.PawnTargetType.Player);
    }

    private static bool TryCaptureLocalPlayerFallbackFrame(NetplayPawnCapture capture, out NetFrameData frameData)
    {
        frameData = default;

        try
        {
            if (capture == null || !IsPlayerCaptureReady(capture)) return false;

            var anchors = capture.anchors;
            var root = anchors.root;
            var relatives = anchors.relatives;
            if (root == null || relatives == null || relatives.Length <= 0) return false;

            int relativeCount = Math.Min(relatives.Length, 128);
            var positions = new float[(relativeCount + 1) * 3];
            var eulers = new float[(relativeCount + 1) * 3];

            WriteWorldTransform(root, positions, eulers, 0);

            for (int i = 0; i < relativeCount; i++)
            {
                var t = relatives[i];
                if (t == null) continue;

                int index = i + 1;
                WriteLocalTransform(t, positions, eulers, index);
            }

            frameData = new NetFrameData
            {
                IsValid = true,
                Flags = _lastNativePlayerFlags,
                Positions = positions,
                Eulers = eulers,
            };

            if (!_localPlayerFallbackCaptureLogged)
            {
                _localPlayerFallbackCaptureLogged = true;
                ModLog.Warning($"[PawnCapture] Local player NetFrame fallback capture active bones={relativeCount}");
            }

            return true;
        }
        catch (Exception ex)
        {
            LogCaptureUnavailable("player", $"fallback failed: {ex.GetType().Name}: {GameInterop.FirstLine(ex.Message)}", ref _lastPlayerCaptureFailureLogAt);
            return false;
        }
    }

    private static void WriteWorldTransform(Transform t, float[] positions, float[] eulers, int index)
    {
        int i = index * 3;
        var pos = t.position;
        var rot = t.eulerAngles;
        positions[i] = pos.x;
        positions[i + 1] = pos.y;
        positions[i + 2] = pos.z;
        eulers[i] = rot.x;
        eulers[i + 1] = rot.y;
        eulers[i + 2] = rot.z;
    }

    private static void WriteLocalTransform(Transform t, float[] positions, float[] eulers, int index)
    {
        int i = index * 3;
        var pos = t.localPosition;
        var rot = t.localEulerAngles;
        positions[i] = pos.x;
        positions[i + 1] = pos.y;
        positions[i + 2] = pos.z;
        eulers[i] = rot.x;
        eulers[i + 1] = rot.y;
        eulers[i + 2] = rot.z;
    }

    private static bool IsCaptureObjectReady(NetplayPawnCapture capture, NetFrame.PawnTargetType target)
    {
        try
        {
            if (capture == null || capture.Pointer == IntPtr.Zero) return false;
            if (capture.target != target) return false;
            if (!capture.isActiveAndEnabled) return false;

            var go = capture.gameObject;
            if (go == null || !go.activeInHierarchy) return false;

            var anchors = capture.anchors;
            if (anchors.root == null) return false;
            var relatives = anchors.relatives;
            return relatives != null && relatives.Length > 0;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("pawn.inspect-relative-anchors", exception);
            return false;
        }
    }

    private static void BackOffCaptureRetry(ref float nextRetryAt, string target, string reason, ref float lastLogAt)
    {
        nextRetryAt = Time.unscaledTime + CaptureRetryDelaySeconds;
        LogCaptureUnavailable(target, reason, ref lastLogAt);
    }

    private static void BackOffCaptureFailure(string target, Exception ex, ref float nextRetryAt, ref float lastLogAt)
    {
        nextRetryAt = Time.unscaledTime + CaptureRetryDelaySeconds;
        LogCaptureUnavailable(target, $"{ex.GetType().Name}: {GameInterop.FirstLine(ex.Message)}", ref lastLogAt);
    }

    private static void LogCaptureUnavailable(string target, string reason, ref float lastLogAt)
    {
        var now = Time.unscaledTime;
        if (now - lastLogAt < CaptureFailureLogIntervalSeconds) return;

        lastLogAt = now;
        ModLog.Warning($"[PawnCapture] Capture {target} NetFrame unavailable: {reason}");
    }

    private static NetplayPawnCapture TryGetTypedPawnCapture()
    {
        if (_typedPawnCaptureCached != null) return _typedPawnCaptureCached;

        var mc = LocalPlayerInterop.TryGetMCGameObject();
        if (mc != null)
        {
            try
            {
                _typedPawnCaptureCached = mc.GetComponent<NetplayPawnCapture>() ??
                                          mc.GetComponentInChildren<NetplayPawnCapture>(true);
                if (_typedPawnCaptureCached != null)
                {
                    ModLog.Debug($"[PawnCapture] Typed player NetplayPawnCapture found on '{_typedPawnCaptureCached.gameObject.name}'");
                    return _typedPawnCaptureCached;
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"[PawnCapture] Typed player capture lookup failed: {ex.Message}");
            }
        }

        return TryFindTypedCapture(NetFrame.PawnTargetType.Player, ref _typedPawnCaptureCached);
    }

    private static NetplayPawnCapture TryGetTypedClimbotCapture()
    {
        if (_typedClimbotCaptureCached != null) return _typedClimbotCaptureCached;
        return TryFindTypedCapture(NetFrame.PawnTargetType.Climbot, ref _typedClimbotCaptureCached);
    }

    private static NetplayPawnCapture TryFindTypedCapture(NetFrame.PawnTargetType target, ref NetplayPawnCapture cache)
    {
        int frame = Time.frameCount;
        if (frame - _lastTypedCaptureSearchFrame < 60) return null;
        _lastTypedCaptureSearchFrame = frame;

        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<NetplayPawnCapture>();
            if (all == null) return null;

            foreach (var capture in all)
            {
                if (capture == null) continue;
                if (capture.target != target) continue;

                cache = capture;
                ModLog.Debug($"[PawnCapture] Typed {target} NetplayPawnCapture found on '{capture.gameObject.name}'");
                return capture;
            }
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[PawnCapture] Typed {target} capture scan failed: {ex.Message}");
        }

        return null;
    }

    public static NetFrameData ToNetFrameData(NetFrame frame)
    {
        return new NetFrameData
        {
            IsValid = frame != null && frame.isValid,
            Flags = frame?.flags ?? 0,
            Positions = CopyVectorArray(frame?.positions),
            Eulers = CopyVectorArray(frame?.eulers),
        };
    }

    public static NetFrame ToNativeNetFrame(NetFrameData data)
    {
        var frame = new NetFrame
        {
            isValid = data.IsValid,
            flags = data.Flags,
            positions = BuildVectorArray(data.Positions),
            eulers = BuildVectorArray(data.Eulers),
        };
        return frame;
    }

    public static NetFrame ToNativePlayerNetFrame(NetFrameData data)
    {
        // Episure-style: we write the frame VERBATIM. The `flags` byte is a bitfield
        // encoding PawnState/PawnFlags/PawnTarget; the native IK reads it to pick
        // the target pose (climb vs walk, secured...). Rewriting these sub-fields (forcing
        // Climbing, clearing NonPlayer) made the rig resolve to the wrong target
        // -> limbs clipping the wall. We only force the Player target (pawn routing).
        var frame = ToNativeNetFrame(data);
        frame.PawnTarget = NetFrame.PawnTargetType.Player;
        return frame;
    }

    public static NetFrame ToNativeClimbotNetFrame(NetFrameData data)
    {
        // Same: flags verbatim, we only force the Climbot target.
        var frame = ToNativeNetFrame(data);
        frame.PawnTarget = NetFrame.PawnTargetType.Climbot;
        return frame;
    }

    private static float[] CopyVectorArray(Il2CppStructArray<Vector3> vectors)
    {
        if (vectors == null || vectors.Length <= 0)
            return Array.Empty<float>();

        var values = new float[vectors.Length * 3];
        for (int i = 0; i < vectors.Length; i++)
        {
            var v = vectors[i];
            int offset = i * 3;
            values[offset] = v.x;
            values[offset + 1] = v.y;
            values[offset + 2] = v.z;
        }
        return values;
    }

    private static Il2CppStructArray<Vector3> BuildVectorArray(float[] values)
    {
        var count = values == null ? 0 : values.Length / 3;
        var vectors = new Il2CppStructArray<Vector3>((long)count);
        for (int i = 0; i < count; i++)
        {
            int offset = i * 3;
            vectors[i] = new Vector3(values[offset], values[offset + 1], values[offset + 2]);
        }
        return vectors;
    }

}
