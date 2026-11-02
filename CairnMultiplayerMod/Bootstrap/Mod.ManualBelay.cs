using System;
using CairnMultiplayerMod.Internal;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Bootstrap;

public partial class Mod
{
    private string _localBelayNotice;
    private double _localBelayNoticeUntil;
    private GUIStyle _localBelayStyle;

    private void TickBelayRouteInput()
    {
        // Held commands expire every input frame, including pause/focus/capture.
        _game?.Belay.SetLocalRopeMotion(0);
        if (_game?.Belay.IsLocalExercise == true && (Network?.IsConnected == true || _multiplayerModeActive))
        {
            _game.Belay.ToggleLocalExercise(out _);
            return;
        }
        if (_game == null || (ModConfig.BelayRouteExperiment?.Value != true && ModConfig.BelayRopeExperiment?.Value != true)
            || Network?.IsConnected == true
            || _multiplayerModeActive || !Application.isFocused || _game.Input.IsKeyboardCaptured) return;
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        try
        {
            if (keyboard.f11Key.wasPressedThisFrame && (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed))
            {
                var started = _game.Belay.ToggleLocalExercise(out var reason);
                _localBelayNotice = reason;
                _localBelayNoticeUntil = Time.realtimeSinceStartupAsDouble + 15;
                if (!started) LoggerInstance.Warning("[ManualBelay] " + reason);
            }
            if (keyboard.spaceKey.wasPressedThisFrame || keyboard.f7Key.wasPressedThisFrame) _game.Belay.ToggleLocalBrake();
            var motion = (keyboard.upArrowKey.isPressed ? 1 : 0) - (keyboard.downArrowKey.isPressed ? 1 : 0);
            _game.Belay.SetLocalRopeMotion(motion);
        }
        catch (Exception ex)
        {
            _localBelayNotice = "Local exercise could not start: " + ex.Message;
            _localBelayNoticeUntil = Time.realtimeSinceStartupAsDouble + 15;
            LoggerInstance.Warning("[ManualBelay] " + _localBelayNotice);
        }
    }

    private void DrawLocalBelayExercise()
    {
        if ((ModConfig.BelayRouteExperiment?.Value != true && ModConfig.BelayRopeExperiment?.Value != true) || _game == null) return;
        _localBelayStyle ??= new GUIStyle(GUI.skin.box)
        {
            fontSize = 14, wordWrap = true, alignment = TextAnchor.UpperLeft,
            padding = new RectOffset(10, 10, 10, 10)
        };
        var rect = new Rect(20, 20, Math.Min(800f, Screen.width - 40f), 136);
        if (!_game.Belay.IsLocalExercise)
        {
            var hasNotice = Time.realtimeSinceStartupAsDouble < _localBelayNoticeUntil && !string.IsNullOrEmpty(_localBelayNotice);
            if ((!_game.State.IsLocalPlayerInGame && !hasNotice) || Network?.IsConnected == true || _multiplayerModeActive) return;
            var notice = hasNotice ? _localBelayNotice : _game.Belay.LocalExerciseNotice
                ?? "Connect your rope to a protection piton, then press Ctrl+F11 to start.";
            GUI.Box(rect, "Local protection exercise — NOT ACTIVE\n" + notice, _localBelayStyle);
            return;
        }
        var status = _game.Belay.Status;
        var rope = _game.Belay.LocalRopeStatus;
        if (rope.HasValue)
        {
            var length = rope.Value;
            rect.height = 178;
            GUI.Box(rect, "Local rope-budget exercise — Ctrl+F11: end | Space/F7: brake/release\n"
                + "Hold Up: feed | Down: take in slack (1 m/s) | Fixed protection route\n"
                + (length.Braked ? "BRAKED — feed blocked; take-in removes slack only" : "FREE — a fall runs rope until braking or the rope end")
                + $"\nFinal segment: released {length.Released:F2} m | slack {length.Slack:F2} m | reserve {length.Reserve:F2} m"
                + $"\nGeometry {length.Distance:F2} m | native rest {length.RestLength:F2} m | particles {length.ParticleLength:F2} m"
                + (status.Suspended ? $"\nSuspension: applied {status.Limit:F2} m | real {status.RealDistance:F2} m"
                    : "\nClimbing — length is retained for the next suspension"), _localBelayStyle);
            return;
        }
        var preview = _game.Belay.LocalRoutePreview;
        GUI.Box(rect, "Local protection exercise — Ctrl+F11: end | Space: brake/release\n"
            + (status.Rebinding ? "Updating protection route..." : status.RequestedBrake ? "BRAKED" : "FREE ROPE")
            + $" | pitons {status.ProtectionCount} | generation {status.TopologyRevision}\n"
            + $"Applied limit {status.Limit:F2} m | physical distance {status.RealDistance:F2} m\n"
            + (preview.HasValue ? $"Planned route (simulated station at first piton): {preview.Value.AvailableToClimber:F2} m available"
                : "Planned route: waiting for stable protection"), _localBelayStyle);
    }
}
