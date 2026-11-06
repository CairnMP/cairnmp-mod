using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Unity.Cinemachine;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Players;

/// <summary>
/// The view used while flying the practice climber. The game camera comes back when it closes.
/// </summary>
internal static class PracticePilotCamera
{
    private const float Priority = 1000f;
    private const float LookSpeed = 0.12f;
    private const float MinPitch = -85f;
    private const float MaxPitch = 85f;
    private const float FollowDistance = 4.5f;
    private const float FollowHeight = 1.4f;

    private static GameObject _root;
    private static Transform _transform;
    private static float _yaw;
    private static float _pitch;

    internal static bool IsActive => _root != null;

    internal static bool Enter()
    {
        if (IsActive) return true;
        try
        {
            var start = Camera.main != null ? Camera.main.transform : null;
            _root = new GameObject("CairnMP_PracticePilotCamera");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _transform = _root.transform;
            if (start != null)
            {
                _transform.SetPositionAndRotation(start.position, start.rotation);
                var angles = start.eulerAngles;
                _pitch = angles.x > 180f ? angles.x - 360f : angles.x;
                _yaw = angles.y;
            }

            var camera = _root.AddComponent<CinemachineCamera>();
            camera.Priority = new PrioritySettings { Value = (int)Priority };
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[Practice] Could not open the pilot camera: {exception.Message}");
            Leave();
            return false;
        }
    }

    internal static void Leave()
    {
        _transform = null;
        var root = _root;
        _root = null;
        if (root == null) return;
        try { UnityEngine.Object.Destroy(root); }
        catch (Exception exception) { ModLog.SuppressedException("practice.destroy-camera", exception); }
    }

    internal static void Aim(float yawDelta, float pitchDelta)
    {
        if (!IsActive) return;
        _yaw += yawDelta * LookSpeed;
        _pitch = Mathf.Clamp(_pitch - pitchDelta * LookSpeed, MinPitch, MaxPitch);
    }

    internal static void LookAxes(out float forwardX, out float forwardY, out float forwardZ,
        out float rightX, out float rightY, out float rightZ)
    {
        var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        var forward = rotation * Vector3.forward;
        var right = rotation * Vector3.right;
        forwardX = forward.x;
        forwardY = forward.y;
        forwardZ = forward.z;
        rightX = right.x;
        rightY = right.y;
        rightZ = right.z;
    }

    internal static void Place(float x, float y, float z)
    {
        if (!IsActive) return;
        var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        var focus = new Vector3(x, y + FollowHeight, z);
        _transform.SetPositionAndRotation(focus - rotation * Vector3.forward * FollowDistance, rotation);
    }
}
