using System;
using CairnMultiplayerMod.Internal;
using CairnMultiplayerMod.Internal.Diagnostics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Bootstrap;

public partial class Mod
{
    private string _localBelayNotice;
    private double _localBelayNoticeUntil;
    private GUIStyle _localBelayStyle;
    private readonly BelayExerciseInput _localBelayExerciseInput = new();

    private void TickBelayRouteInput()
    {
        // Held commands expire every input frame, including pause/focus/capture.
        _game?.Belay.SetLocalRopeMotion(0);
        if (_game?.Belay.IsLocalExercise == true && (Network?.IsConnected == true || _multiplayerModeActive))
        {
            _localBelayExerciseInput.Reset();
            _game.Belay.ToggleLocalExercise(out _);
            return;
        }
        if (_game == null || (ModConfig.BelayRouteExperiment?.Value != true && ModConfig.BelayRopeExperiment?.Value != true)
            || Network?.IsConnected == true
            || _multiplayerModeActive || !Application.isFocused || _game.Input.IsKeyboardCaptured || !(Time.timeScale > 0))
        { _localBelayExerciseInput.Reset(); return; }
        var keyboard = Keyboard.current;
        if (keyboard == null) { _localBelayExerciseInput.Reset(); return; }
        try
        {
            if (keyboard.f10Key.wasPressedThisFrame && (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed))
            {
                var recorded = _game.Belay.ToggleLocalStation(out var stationReason);
                _localBelayNotice = stationReason;
                _localBelayNoticeUntil = Time.realtimeSinceStartupAsDouble + 20;
                if (LocalAppendGuideEnabled)
                {
                    if (recorded) _localBelayAppendTrial.StationChanged(_game.Belay.LocalStationStatus.HasValue);
                    else _localBelayAppendTrial.RejectAction();
                }
                if (!recorded) LoggerInstance.Warning("[BelayStation] " + stationReason);
            }
            if (keyboard.f11Key.wasPressedThisFrame && (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed))
            {
                if (LocalAppendGuideEnabled && !_game.Belay.IsLocalExercise) _localBelayAppendTrial.ExerciseRequested();
                var started = _game.Belay.ToggleLocalExercise(out var reason);
                _localBelayExerciseInput.Reset();
                _localBelayNotice = reason;
                _localBelayNoticeUntil = Time.realtimeSinceStartupAsDouble + 15;
                if (!started && LocalAppendGuideEnabled) _localBelayAppendTrial.RejectAction();
                if (!started) LoggerInstance.Warning("[ManualBelay] " + reason);
            }
            if (keyboard.spaceKey.wasPressedThisFrame || keyboard.f7Key.wasPressedThisFrame) _game.Belay.ToggleLocalBrake();
            var takingIn = _localBelayExerciseInput.TakingIn;
            var toggleTakeIn = keyboard.downArrowKey.wasPressedThisFrame
                && (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed);
            var motion = _localBelayExerciseInput.Resolve(_game.Belay.IsLocalExercise && _game.Belay.LocalRopeStatus.HasValue,
                toggleTakeIn, keyboard.upArrowKey.isPressed, keyboard.downArrowKey.isPressed, Time.realtimeSinceStartupAsDouble);
            _game.Belay.SetLocalRopeMotion(motion);
            if (takingIn != _localBelayExerciseInput.TakingIn)
                LoggerInstance.Msg("[BelayRope] Test belayer take-in=" + _localBelayExerciseInput.TakingIn);
        }
        catch (Exception ex)
        {
            _localBelayExerciseInput.Reset();
            _localBelayNotice = "Local exercise could not start: " + ex.Message;
            _localBelayNoticeUntil = Time.realtimeSinceStartupAsDouble + 15;
            LoggerInstance.Warning("[ManualBelay] " + _localBelayNotice);
        }
    }

    private void DrawLocalBelayExercise()
    {
        if ((ModConfig.BelayRouteExperiment?.Value != true && ModConfig.BelayRopeExperiment?.Value != true) || _game == null) return;
        if (LocalAppendGuideEnabled) { DrawBelayAppendGuide(); return; }
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
                ?? (_game.Belay.LocalStationStatus.HasValue
                    ? "Station recorded. Release your personal anchor and keep the station piton on your rope; Ctrl+F11 starts the rehearsal. Ctrl+F10 clears the station."
                    : "Connect your rope to a protection piton, then Ctrl+F11 starts. To record a station: anchor yourself on a piton and press Ctrl+F10 first.");
            GUI.Box(rect, "Local protection exercise — NOT ACTIVE\n" + notice, _localBelayStyle);
            return;
        }
        var status = _game.Belay.Status;
        var rope = _game.Belay.LocalRopeStatus;
        if (rope.HasValue)
        {
            var length = rope.Value;
            var station = _game.Belay.LocalStationStatus;
            rect.height = station.HasValue ? 254 : 202;
            GUI.Box(rect, "Local rope-budget exercise — Ctrl+F11: end | Space/F7: brake/release\n"
                + "Hold Up: feed | Down: take in slack (1 m/s) | Recorded station: unloaded piton additions can rebind\n"
                + (length.Braked ? "BRAKED — climbing away stops at the paid-out rope limit; release/feed for more" : "FREE — a fall runs rope until braking or the rope end")
                + $"\nFinal segment: released {length.Released:F2} m | slack {length.Slack:F2} m | reserve {length.Reserve:F2} m"
                + $"\nGeometry {length.Distance:F2} m | native rest {length.RestLength:F2} m | particles {length.ParticleLength:F2} m"
                + (length.LengthApplied ? "\nLength readback matches target" : "\nWAITING for native rope length — do not start a fall comparison")
                + (_localBelayExerciseInput.TakingIn
                    ? $"\nTEST BELAYER: taking in ({_localBelayExerciseInput.RemainingSeconds(Time.realtimeSinceStartupAsDouble):F0}s left) | Ctrl+Down: stop"
                    : "\nCtrl+Down: simulate belayer take-in for 20 seconds; manual arrows cancel")
                + (status.Suspended ? $"\nSuspension: applied {status.Limit:F2} m | real {status.RealDistance:F2} m"
                    : "\nClimbing — length is retained for the next suspension")
                + (station.HasValue
                    ? $"\nSTATION REHEARSAL: fixed path {station.Value.FixedLength:F2} m | intermediate protections {station.Value.IntermediateProtections}"
                        + "\nReal piton on native route; local controls simulate the absent belayer"
                    : "")
                + "\nPausing or switching windows ends this exercise.", _localBelayStyle);
            return;
        }
        var preview = _game.Belay.LocalRoutePreview;
        GUI.Box(rect, "Local protection exercise — Ctrl+F11: end | Space: brake/release\n"
            + (status.Rebinding ? "UPDATING ROUTE — manual length writes paused; remain stable on the wall"
                : status.RequestedBrake ? "BRAKED" : "FREE ROPE")
            + $" | pitons {status.ProtectionCount} | generation {status.TopologyRevision}\n"
            + $"Applied limit {status.Limit:F2} m | physical distance {status.RealDistance:F2} m\n"
            + (status.Rebinding ? "Recorded station retained; wait before continuing or testing a fall."
                : preview.HasValue ? $"Planned route (simulated station at first piton): {preview.Value.AvailableToClimber:F2} m available"
                : "Planned route: waiting for stable protection"), _localBelayStyle);
    }
}
