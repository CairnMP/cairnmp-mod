using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.MainMenu;
using CairnMultiplayerMod.Internal.UI;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Keeps launch on Cairn's native save-selection path so every player owns their save choice.
/// </summary>
internal sealed class StartGameFlow
{
    private readonly IMultiplayerPanel _panel;
    private readonly Action _restoreMainMenuInput;
    private readonly RuntimeState _state;

    private ServerStartGame? _pendingStart;
    private float _pendingStartRetryTimer;
    private int _pendingStartRetries;

    // Cairn overwrites newGameOptions after the New Game click, so keep the host's flags applied
    // until loading begins.
    private ServerStartGame? _forceNewGameOpts;

    // Multiplayer must stop at the native difficulty menu. Going directly to save selection
    // bypasses the only screen that exposes the mod-unlocked Free Roam option.
    internal static MainMenuInterop.MainMenuStep MultiplayerLaunchEntryStep =>
        MainMenuInterop.MainMenuStep.DifficultySelect;

    internal StartGameFlow(
        IMultiplayerPanel panel,
        Action restoreMainMenuInput,
        RuntimeState state)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _restoreMainMenuInput = restoreMainMenuInput
            ?? throw new ArgumentNullException(nameof(restoreMainMenuInput));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    internal bool HasPending => _pendingStart.HasValue;

    internal void Cancel()
    {
        _pendingStart = null;
        _forceNewGameOpts = null;
        _pendingStartRetries = 0;
        _pendingStartRetryTimer = 0f;
    }

    internal void Begin(ServerStartGame pkt)
    {
        _restoreMainMenuInput();
        _pendingStart = pkt;
        _pendingStartRetries = 0;
        _pendingStartRetryTimer = 1.0f;
        _panel.SetStatus($"Launching game ({(GameDifficulty)pkt.Difficulty})...", true);
    }

    internal void Tick()
    {
        var currentScene = _state.CurrentScene;

        if (_forceNewGameOpts.HasValue)
        {
            if (SceneRoles.IsMainMenuArea(currentScene))
            {
                var s = _forceNewGameOpts.Value;
                // Difficulty stays local because this native screen lets each player choose it.
                GameOptionsInterop.SetNextGameSkipOptions(
                    s.SkipTutorials, s.SkipPractice, s.AssistEnabled, verbose: false);
            }
            else
            {
                _forceNewGameOpts = null;
            }
        }

        if (_pendingStart.HasValue && SceneRoles.IsMainMenuArea(currentScene))
        {
            // ForceStepTransition is consumed by MainMenu.Update, which the panel disables.
            _restoreMainMenuInput();

            _pendingStartRetryTimer += Time.unscaledDeltaTime;

            if (_pendingStartRetries < 3 && _pendingStartRetryTimer >= 0.5f)
            {
                _pendingStartRetryTimer = 0f;
                _pendingStartRetries++;
                MainMenuInterop.ForceMainMenuStep(MainMenuInterop.MainMenuStep.ModeSelect);
                ModLog.Info($"[StartGame] Force ModeSelect (retry #{_pendingStartRetries})");
            }
            else if (_pendingStartRetries == 3 && _pendingStartRetryTimer >= 0.5f)
            {
                var start = _pendingStart.Value;
                GameOptionsInterop.SetNextGameSkipOptions(start.SkipTutorials, start.SkipPractice, start.AssistEnabled);

                _forceNewGameOpts = start;

                MainMenuInterop.ForceMainMenuStep(MultiplayerLaunchEntryStep);
                ModLog.Info("[StartGame] Opened native difficulty selection — player chooses mode before save selection (options re-applied each frame)");

                _pendingStart = null;
                _pendingStartRetries = 0;
                _pendingStartRetryTimer = 0f;
                _panel.Hide();
            }
        }
    }
}
