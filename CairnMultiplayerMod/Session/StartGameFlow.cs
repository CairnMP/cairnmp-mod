using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Session;

/// <summary>
/// Drives the MainMenu toward the game's native save menu during an authoritative
/// launch by the host. Ticked every frame from OnUpdate().
/// <para>
/// The mod no longer loads any save: it simply brings each player to the native save
/// menu (StoryModeManageSave), exactly like in solo. Each player creates a new game
/// or picks an existing save there themselves.
///   Phase 1 (attempts 1-3, spaced 0.5s apart): force ModeSelect so the menu is in
///       the right state.
///   Phase 2 (attempt 4): pre-set the next new game's difficulty, then open the native
///       save menu, and stop.
/// </para>
/// </summary>
internal sealed class StartGameFlow
{
    private readonly IMultiplayerPanel _panel;

    private ServerStartGame? _pendingStart;
    private float _pendingStartRetryTimer;
    private int _pendingStartRetries;

    // New-game options to RE-APPLY every frame while at the menu: the native save-creation flow
    // rewrites newGameOptions (skip=false by default) when "new game" is clicked, AFTER our
    // pre-setting. So we force it continuously so it's correct at the moment the game reads it.
    // Cleared as soon as we leave the menu (loading).
    private ServerStartGame? _forceNewGameOpts;

    internal StartGameFlow(IMultiplayerPanel panel)
    {
        _panel = panel;
    }

    /// <summary>True while an authoritative launch is queued and not yet consumed.</summary>
    internal bool HasPending => _pendingStart.HasValue;

    /// <summary>Queues an authoritative launch received from the server, applied in Tick
    /// once the MainMenu scene is active.</summary>
    internal void Begin(ServerStartGame pkt)
    {
        MainMenuMultiplayerButton.RestoreMainMenuInput();
        _pendingStart = pkt;
        _pendingStartRetries = 0;
        _pendingStartRetryTimer = 1.0f;
        _panel.SetStatus($"Launching game ({(GameDifficulty)pkt.Difficulty})...", true);
    }

    internal void Tick()
    {
        var currentScene = Mod.Instance.CurrentScene;

        // Continuously re-apply the new-game options while at the menu (the native flow
        // rewrites them on the "new game" click). Stop as soon as we leave the menu.
        if (_forceNewGameOpts.HasValue)
        {
            if (currentScene != null && currentScene.StartsWith("MainMenu"))
            {
                var s = _forceNewGameOpts.Value;
                // We respect the difficulty chosen by the player in the native screen
                // (FreeRoam included): we force ONLY the skip/assist flags, never the
                // difficulty. Otherwise this per-frame re-forcing would overwrite the native choice.
                GameOptionsApi.SetNextGameSkipOptions(
                    s.SkipTutorials, s.SkipPractice, s.AssistEnabled, verbose: false);
            }
            else
            {
                _forceNewGameOpts = null;
            }
        }

        if (_pendingStart.HasValue && currentScene != null && currentScene.StartsWith("MainMenu"))
        {
            // CRUCIAL: when the MP panel opens, the `MainMenu` component was DISABLED
            // (MainMenuMultiplayerButton.SuspendMainMenuInput -> behaviour.enabled=false). But
            // it's ITS Update() loop that reads `ForceStepTransition` and runs TransitionToStep.
            // While it's disabled, our ForceMainMenuStep calls have NO effect -> empty save menu
            // and stuck player. So we re-enable it before driving the steps (idempotent).
            MainMenuMultiplayerButton.RestoreMainMenuInput();

            _pendingStartRetryTimer += Time.unscaledDeltaTime;

            // Phase 1: bring the menu to ModeSelect
            if (_pendingStartRetries < 3 && _pendingStartRetryTimer >= 0.5f)
            {
                _pendingStartRetryTimer = 0f;
                _pendingStartRetries++;
                MainMenuApi.ForceMainMenuStep(MainMenuApi.MainMenuStep.ModeSelect);
                Mod.Log.Msg($"[StartGame] Force ModeSelect (retry #{_pendingStartRetries})");
            }
            // Phase 2: pre-set the difficulty then open the native save menu
            else if (_pendingStartRetries == 3 && _pendingStartRetryTimer >= 0.5f)
            {
                var start = _pendingStart.Value;
                // Respect the native choice: we do NOT pre-set the difficulty, we only
                // force the skip/assist flags (the player chooses their difficulty in the
                // native screen, FreeRoam included).
                GameOptionsApi.SetNextGameSkipOptions(start.SkipTutorials, start.SkipPractice, start.AssistEnabled);

                // Keep forcing these options every frame until we leave the menu (cf. field).
                _forceNewGameOpts = start;

                MainMenuApi.ForceMainMenuStep(MainMenuApi.MainMenuStep.StoryModeManageSave);
                Mod.Log.Msg("[StartGame] Opened native save menu — player chooses new/existing (options re-applied each frame)");

                _pendingStart = null;
                _pendingStartRetries = 0;
                _pendingStartRetryTimer = 0f;
                _panel.Hide();
            }
        }
    }
}
