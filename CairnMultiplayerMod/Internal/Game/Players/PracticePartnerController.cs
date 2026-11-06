using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game;
using CairnMultiplayerMod.Internal.Game.Roping;
using CairnMultiplayerMod.Internal.Networking;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game.Players;

/// <summary>
/// F9 spawns or removes the practice climber. H takes over his body and flies it
/// with the mouse and keyboard. Gameplay input stays live, so the same keys can
/// also move the real climber.
/// </summary>
internal static class PracticePartnerController
{
    internal const string SpawnKeyName = "F9";
    internal const string MoveKeyName = "H";
    internal const string PilotMessageId = "practice.pilot";
    internal const string PilotText = "Flying the practice climber — H returns you";

    private static readonly Key SpawnKey = Key.F9;
    private static readonly Key MoveKey = Key.H;
    private static bool _spawned;
    private static bool _piloting;
    private static HudAdapter _hud;

    internal static void Bind(HudAdapter hud) => _hud = hud;

    internal static void Tick(NetworkManager network, RuntimeState state)
    {
        if (network == null || state == null) return;
        if (!network.CanHostPracticePartner || SceneRoles.IsMainMenuArea(state.CurrentScene))
        {
            if (_spawned) Despawn(network);
            else StopPiloting();
            return;
        }

        if (!InputCaptureState.IsKeyboardCaptured && !MultiplayerPausePatch.IsPauseMenuActive)
            HandleKeys(network, state);
        if (_spawned) MaintainPose(network, state);
        else StopPiloting();
    }

    private static void HandleKeys(NetworkManager network, RuntimeState state)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        if (keyboard[SpawnKey].wasPressedThisFrame)
        {
            if (_spawned) Despawn(network);
            else Spawn(network, state);
            return;
        }

        if (!_spawned || !keyboard[MoveKey].wasPressedThisFrame) return;
        if (_piloting) StopPiloting();
        else StartPiloting();
    }

    private static void Spawn(NetworkManager network, RuntimeState state)
    {
        if (!LocalPlayerInterop.TryGetPose(out var position, out var yaw)
            || !PracticePartnerMotion.TryBeside(
                position.x, position.y, position.z, yaw, PracticePartnerMotion.SpawnSideMeters,
                out var x, out var y, out var z))
        {
            ModLog.Info("[Practice] Could not read where you are standing.");
            return;
        }

        if (!network.TrySpawnPracticePartner(x, y, z, yaw + 180f, state.CurrentScene))
        {
            ModLog.Info("[Practice] His id is already in this lobby, so he was not spawned.");
            return;
        }

        _spawned = true;
        ModLog.Info("[Practice] Spawned beside you. H flies him. Tie with L or the right stick.");
    }

    private static void Despawn(NetworkManager network)
    {
        StopPiloting();
        _spawned = false;
        if (RopeLinkState.IsLinked(network.LocalPlayerId, PracticePartner.PlayerId))
            network.SendRopeClip(PracticePartner.PlayerId, false);
        network.DespawnPracticePartner();
        ModLog.Info("[Practice] Removed.");
    }

    private static void StartPiloting()
    {
        if (!PracticePilotCamera.Enter())
        {
            ModLog.Info("[Practice] Could not take over his view.");
            return;
        }

        _piloting = true;
        ModLog.Info("[Practice] Flying him. Mouse looks, WASD moves, Space up, Ctrl down. H returns you.");
    }

    private static void StopPiloting()
    {
        if (!_piloting && !PracticePilotCamera.IsActive)
        {
            _hud?.HideMessage(PilotMessageId);
            return;
        }

        _piloting = false;
        PracticePilotCamera.Leave();
        _hud?.HideMessage(PilotMessageId);
    }

    private static void MaintainPose(NetworkManager network, RuntimeState state)
    {
        if (!network.RemotePlayers.TryGetValue(PracticePartner.PlayerId, out var remote) || remote == null)
        {
            _spawned = false;
            StopPiloting();
            return;
        }

        var x = remote.X;
        var y = remote.Y;
        var z = remote.Z;
        var yaw = remote.YawDeg;
        if (_piloting && state.LocalPlayerState == PlayerState.InGame && !InputCaptureState.IsKeyboardCaptured
            && !MultiplayerPausePatch.IsPauseMenuActive)
        {
            Fly(x, y, z, ref x, ref y, ref z, ref yaw);
            _hud?.ShowMessage(PilotMessageId, PilotText, 0.35f);
        }
        else
        {
            _hud?.HideMessage(PilotMessageId);
        }

        network.TryMovePracticePartner(x, y, z, yaw, state.CurrentScene);
        if (_piloting) PracticePilotCamera.Place(x, y, z);
    }

    private static void Fly(float fromX, float fromY, float fromZ, ref float x, ref float y, ref float z, ref float yaw)
    {
        var mouse = Mouse.current;
        if (mouse != null)
        {
            var delta = mouse.delta.ReadValue();
            PracticePilotCamera.Aim(delta.x, delta.y);
        }

        PracticePilotCamera.LookAxes(
            out var forwardX, out var forwardY, out var forwardZ,
            out var rightX, out var rightY, out var rightZ);
        var keyboard = Keyboard.current;
        var strafe = 0f;
        var ahead = 0f;
        var vertical = 0f;
        var speed = PracticePartnerMotion.FlySpeedMetersPerSecond;
        if (keyboard != null)
        {
            if (keyboard.dKey.isPressed) strafe += 1f;
            if (keyboard.aKey.isPressed) strafe -= 1f;
            if (keyboard.wKey.isPressed) ahead += 1f;
            if (keyboard.sKey.isPressed) ahead -= 1f;
            if (keyboard.spaceKey.isPressed) vertical += 1f;
            if (keyboard.leftCtrlKey.isPressed) vertical -= 1f;
            if (keyboard.leftShiftKey.isPressed) speed *= PracticePartnerMotion.FlyBoost;
        }

        if (!PracticePartnerMotion.TryFly(
                fromX, fromY, fromZ, forwardX, forwardY, forwardZ, rightX, rightY, rightZ,
                strafe, ahead, vertical, speed, Time.unscaledDeltaTime, PracticePartnerMotion.MaxStepMeters,
                out var flownX, out var flownY, out var flownZ))
            return;
        x = flownX;
        y = flownY;
        z = flownZ;
        yaw = PracticePilotCamera.IsActive ? Mathf.Atan2(forwardX, forwardZ) * Mathf.Rad2Deg : yaw;
    }
}
