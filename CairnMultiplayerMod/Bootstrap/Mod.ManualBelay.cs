using System;
using CairnMultiplayerMod.Internal;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Bootstrap;

public partial class Mod
{
    private void TickBelayRouteInput()
    {
        if (_game?.Belay.IsLocalExercise == true && (Network?.IsConnected == true || _multiplayerModeActive))
        {
            _game.Belay.ToggleLocalExercise(out _);
            return;
        }
        if (ModConfig.BelayRouteExperiment?.Value != true || Network?.IsConnected == true
            || _multiplayerModeActive || !Application.isFocused || _game.Input.IsKeyboardCaptured) return;
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        try
        {
            if (keyboard.f11Key.wasPressedThisFrame && (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed))
            {
                if (!_game.Belay.ToggleLocalExercise(out var reason)) LoggerInstance.Warning("[ManualBelay] " + reason);
            }
            if (keyboard.spaceKey.wasPressedThisFrame || keyboard.f7Key.wasPressedThisFrame) _game.Belay.ToggleLocalBrake();
        }
        catch (Exception ex) { LoggerInstance.Warning("[ManualBelay] Local exercise input stopped: " + ex.Message); }
    }

    private void DrawLocalBelayExercise()
    {
        if (ModConfig.BelayRouteExperiment?.Value != true || _game?.Belay.IsLocalExercise != true) return;
        var status = _game.Belay.Status;
        var preview = _game.Belay.LocalRoutePreview;
        GUI.Box(new Rect(20, 20, 800, 106), "Local protection exercise — Ctrl+F11: end | Space: brake/release\n"
            + (status.Rebinding ? "Updating protection route..." : status.RequestedBrake ? "BRAKED" : "FREE ROPE")
            + $" | pitons {status.ProtectionCount} | generation {status.TopologyRevision}\n"
            + $"Applied limit {status.Limit:F2} m | physical distance {status.RealDistance:F2} m\n"
            + (preview.HasValue ? $"Planned route (simulated station at first piton): {preview.Value.AvailableToClimber:F2} m available"
                : "Planned route: waiting for stable protection"));
    }
}
