using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game.Life;

/// <summary>
/// The camera a dead climber watches from.
///
/// It is a Cinemachine camera with a priority nobody else claims, so the game's own brain
/// keeps driving the real camera, its blends and its post-processing — we only say where to
/// look from. Destroying it hands the view straight back to whatever the game was using.
/// The pose it opens on is the last one stored while the climber was alive. The camera that
/// is current at the moment of death is Cairn's falling hover, which follows the ragdoll
/// down through the ground.
/// </summary>
internal static class SpectatorCamera
{
    // Cairn re-activates ClimbingFallingHoverCamera from its own LateUpdate and that camera
    // keeps the priority it was given for the death shot. This stays above that.
    private const int Priority = 100000;
    private const float LookSpeed = 0.12f;
    private const float MoveSpeed = 14f;
    private const float BoostMultiplier = 4f;
    private const float MinPitch = -85f, MaxPitch = 85f;
    private const float MinFollowDistance = 2.5f, MaxFollowDistance = 18f;
    private const float FollowHeight = 1.6f;

    private static GameObject _root;
    private static CinemachineCamera _camera;
    private static Transform _transform;
    private static Transform _followTarget;
    private static float _yaw, _pitch;
    private static float _followDistance = 6f;
    private static SpectatorViewpoint _remembered;
    private static bool _blendOverridden;
    private static CinemachineBlendDefinition _previousBlend;

    internal static bool IsActive => _root != null;

    /// <summary>True while the view is locked onto a teammate rather than flying free.</summary>
    internal static bool IsFollowing => _followTarget != null;

    internal static void RememberViewpoint()
    {
        if (IsActive) return;
        var live = ReadLiveViewpoint();
        if (live.HasPose) _remembered = live;
    }

    internal static bool Enter()
    {
        if (IsActive) return true;
        var pose = SpectatorSpawn.Choose(_remembered, ReadLiveViewpoint());
        if (!pose.HasPose) return false;

        try
        {
            var position = new Vector3(pose.X, pose.Y, pose.Z);
            var rotation = Quaternion.Euler(pose.Pitch, pose.Yaw, 0f);
            _pitch = pose.Pitch;
            _yaw = pose.Yaw;

            _root = new GameObject("CairnMP_SpectatorCamera");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _transform = _root.transform;
            _transform.SetPositionAndRotation(position, rotation);

            _camera = _root.AddComponent<CinemachineCamera>();
            // A new camera's state is the origin until something writes it. The brain will
            // blend toward that origin if we only move the transform.
            Publish(position, rotation);
            UseCutBlend();
            SpectatorCameraDriver.Install();
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[Spectator] Could not open the spectator camera: {exception.Message}");
            var remembered = _remembered;
            Leave();
            _remembered = remembered;
            return false;
        }
    }

    internal static void Leave()
    {
        _followTarget = null;
        _camera = null;
        _transform = null;
        _remembered = default;
        RestoreBlend();
        var root = _root;
        _root = null;
        if (root == null) return;
        try { UnityEngine.Object.Destroy(root); }
        catch (Exception exception) { ModLog.SuppressedException("spectator.destroy-camera", exception); }
    }

    /// <summary>
    /// Makes this camera the one the brain is showing. Called after Cairn's camera manager
    /// and after the brain's own late update, both of which otherwise put the death hover
    /// shot back.
    /// </summary>
    internal static void ClaimView()
    {
        if (!IsActive || _camera == null || _transform == null) return;
        try
        {
            Publish(_transform.position, _transform.rotation);
            var brain = FindBrain();
            if (brain == null) return;
            UseCutBlend();
            brain.ManualUpdate();
            // A custom blend asset can still ease in from the hover shot. Finishing that
            // blend and updating again leaves this frame on the spectator pose.
            if (brain.IsBlending)
            {
                brain.ActiveBlend = null;
                brain.ManualUpdate();
            }
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("spectator.claim-view", exception);
        }
    }

    internal static void FreeLook() => _followTarget = null;

    internal static bool Follow(Transform target)
    {
        if (target == null) return false;
        _followTarget = target;
        return true;
    }

    /// <summary>
    /// Drives the view for one frame. Both modes read the same look input: flying free it
    /// turns the camera, locked on it swings around the climber.
    /// </summary>
    internal static void Tick(bool acceptInput)
    {
        if (!IsActive) return;
        try
        {
            if (acceptInput) ReadLook();

            if (_followTarget != null)
            {
                if (!TickFollow(acceptInput)) FreeLook();
                return;
            }

            _transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            if (acceptInput) _transform.position += ReadMove(_transform) * Time.unscaledDeltaTime;
        }
        catch (Exception exception)
        {
            // A destroyed target or a lost camera must not throw into the game loop; the
            // spectator falls back to a free view and keeps going.
            ModLog.SuppressedException("spectator.tick", exception);
            FreeLook();
        }
    }

    private static void Publish(Vector3 position, Quaternion rotation)
    {
        _camera.OutputChannel = OutputChannels.Default;
        // InheritPosition would copy the hover camera we are replacing.
        _camera.BlendHint = default;
        _camera.Priority = new PrioritySettings { Value = Priority };
        // OnEnable is what files the camera in the priority queue, and AddComponent already
        // ran it at priority 0. Turning it off and on files the priority set just above.
        _camera.enabled = false;
        _camera.enabled = true;
        _camera.ForceCameraPosition(position, rotation);
    }

    private static void UseCutBlend()
    {
        var brain = FindBrain();
        if (brain == null) return;
        if (!_blendOverridden)
        {
            _previousBlend = brain.DefaultBlend;
            _blendOverridden = true;
        }

        var cut = new CinemachineBlendDefinition();
        cut.Style = CinemachineBlendDefinition.Styles.Cut;
        cut.Time = 0f;
        brain.DefaultBlend = cut;
    }

    private static void RestoreBlend()
    {
        if (!_blendOverridden) return;
        _blendOverridden = false;
        var brain = FindBrain();
        if (brain == null) return;
        try { brain.DefaultBlend = _previousBlend; }
        catch (Exception exception) { ModLog.SuppressedException("spectator.restore-blend", exception); }
    }

    private static CinemachineBrain FindBrain()
    {
        try
        {
            var camera = Camera.main;
            return camera == null ? null : camera.GetComponent<CinemachineBrain>();
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("spectator.find-brain", exception);
            return null;
        }
    }

    private static SpectatorViewpoint ReadLiveViewpoint()
    {
        try
        {
            var camera = Camera.main;
            if (camera == null) return default;
            var transform = camera.transform;
            var position = transform.position;
            var angles = transform.eulerAngles;
            return SpectatorViewpoint.At(position.x, position.y, position.z,
                NormalizePitch(angles.x), angles.y);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("spectator.read-viewpoint", exception);
            return default;
        }
    }

    private static bool TickFollow(bool acceptInput)
    {
        // The ghost's GameObject is destroyed when its player leaves; Unity's null check
        // catches that, and the caller picks another target.
        if (_followTarget == null || _followTarget.gameObject == null) return false;

        if (acceptInput)
        {
            var scroll = Mouse.current?.scroll.ReadValue().y ?? 0f;
            if (Mathf.Abs(scroll) > 0.01f)
                _followDistance = Mathf.Clamp(_followDistance - scroll * 0.01f,
                    MinFollowDistance, MaxFollowDistance);
        }

        var focus = _followTarget.position + Vector3.up * FollowHeight;
        var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        _transform.position = focus - rotation * Vector3.forward * _followDistance;
        _transform.rotation = rotation;
        return true;
    }

    private static void ReadLook()
    {
        var mouse = Mouse.current;
        if (mouse != null)
        {
            var delta = mouse.delta.ReadValue();
            _yaw += delta.x * LookSpeed;
            _pitch = Mathf.Clamp(_pitch - delta.y * LookSpeed, MinPitch, MaxPitch);
        }

        var pad = Gamepad.current;
        if (pad == null) return;
        var look = pad.rightStick.ReadValue();
        var step = SpectatorFlight.LookDegreesPerSecond * Time.unscaledDeltaTime;
        _yaw += look.x * step;
        _pitch = Mathf.Clamp(_pitch - look.y * step, MinPitch, MaxPitch);
    }

    private static Vector3 ReadMove(Transform frame)
    {
        var keyboard = Keyboard.current;
        var keyForward = 0f;
        var keyStrafe = 0f;
        var keyUp = 0f;
        var keyFaster = false;
        if (keyboard != null)
        {
            if (keyboard.wKey.isPressed) keyForward += 1f;
            if (keyboard.sKey.isPressed) keyForward -= 1f;
            if (keyboard.dKey.isPressed) keyStrafe += 1f;
            if (keyboard.aKey.isPressed) keyStrafe -= 1f;
            if (keyboard.spaceKey.isPressed) keyUp += 1f;
            if (keyboard.leftCtrlKey.isPressed) keyUp -= 1f;
            keyFaster = keyboard.leftShiftKey.isPressed;
        }

        var pad = Gamepad.current;
        var stickX = 0f;
        var stickY = 0f;
        var rise = 0f;
        var sink = 0f;
        var stickFaster = false;
        if (pad != null)
        {
            var stick = pad.leftStick.ReadValue();
            stickX = stick.x;
            stickY = stick.y;
            rise = pad.rightTrigger.ReadValue();
            sink = pad.leftTrigger.ReadValue();
            stickFaster = pad.leftStickButton.isPressed;
        }

        var axes = SpectatorFlight.Combine(
            keyForward, keyStrafe, keyUp, keyFaster, stickX, stickY, rise, sink, stickFaster);
        var move = frame.forward * axes.Forward + frame.right * axes.Strafe + Vector3.up * axes.Up;
        if (move.sqrMagnitude > 1f) move.Normalize();
        return move * (MoveSpeed * (axes.Faster ? BoostMultiplier : 1f));
    }

    private static float NormalizePitch(float pitch)
        => pitch > 180f ? pitch - 360f : pitch;
}
