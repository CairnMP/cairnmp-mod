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
/// </summary>
internal static class SpectatorCamera
{
    private const float Priority = 1000f;
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

    internal static bool IsActive => _root != null;

    /// <summary>True while the view is locked onto a teammate rather than flying free.</summary>
    internal static bool IsFollowing => _followTarget != null;

    internal static bool Enter()
    {
        if (IsActive) return true;
        try
        {
            var start = Camera.main != null ? Camera.main.transform : null;
            _root = new GameObject("CairnMP_SpectatorCamera");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _transform = _root.transform;
            if (start != null)
            {
                _transform.position = start.position;
                _transform.rotation = start.rotation;
                var angles = start.eulerAngles;
                _pitch = NormalizePitch(angles.x);
                _yaw = angles.y;
            }

            _camera = _root.AddComponent<CinemachineCamera>();
            _camera.Priority = new PrioritySettings { Value = (int)Priority };
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[Spectator] Could not open the spectator camera: {exception.Message}");
            Leave();
            return false;
        }
    }

    internal static void Leave()
    {
        _followTarget = null;
        _camera = null;
        _transform = null;
        var root = _root;
        _root = null;
        if (root == null) return;
        try { UnityEngine.Object.Destroy(root); }
        catch (Exception exception) { ModLog.SuppressedException("spectator.destroy-camera", exception); }
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
        if (mouse == null) return;
        var delta = mouse.delta.ReadValue();
        _yaw += delta.x * LookSpeed;
        _pitch = Mathf.Clamp(_pitch - delta.y * LookSpeed, MinPitch, MaxPitch);
    }

    private static Vector3 ReadMove(Transform frame)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return Vector3.zero;

        var move = Vector3.zero;
        if (keyboard.wKey.isPressed) move += frame.forward;
        if (keyboard.sKey.isPressed) move -= frame.forward;
        if (keyboard.dKey.isPressed) move += frame.right;
        if (keyboard.aKey.isPressed) move -= frame.right;
        if (keyboard.spaceKey.isPressed) move += Vector3.up;
        if (keyboard.leftCtrlKey.isPressed) move -= Vector3.up;
        if (move.sqrMagnitude > 1f) move.Normalize();

        var speed = MoveSpeed * (keyboard.leftShiftKey.isPressed ? BoostMultiplier : 1f);
        return move * speed;
    }

    private static float NormalizePitch(float pitch)
        => pitch > 180f ? pitch - 360f : pitch;
}
