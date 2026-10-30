using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.MainMenu;
using CairnMultiplayerMod.Internal.UI;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Keeps launch on Cairn's native menus so every player owns their save choice: the story
/// save selection for Mount Kami, the Summit world selection for a DLC destination.
/// </summary>
internal sealed class StartGameFlow
{
    /// <summary>How long a DLC launch waits for the Summit screens to load before giving up.</summary>
    private const float SummitMenuWaitSeconds = 5f;

    private readonly IMultiplayerPanel _panel;
    private readonly Action _restoreMainMenuInput;
    private readonly RuntimeState _state;

    private ServerStartGame? _pendingStart;
    private float _pendingStartRetryTimer;
    private int _pendingStartRetries;

    // Cairn overwrites newGameOptions after the New Game click, so keep the host's flags applied
    // until loading begins.
    private ServerStartGame? _forceNewGameOpts;

    // A story launch always enters through Cairn's save manager so players can resume an
    // existing save. Choosing an empty slot continues through the native new-game flow.
    internal static MainMenuInterop.MainMenuStep MultiplayerLaunchEntryStep =>
        MainMenuInterop.MainMenuStep.StoryModeManageSave;

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

    private static MultiplayerModeRules RulesOf(ServerStartGame start)
        => MultiplayerModes.Parse(start.Mode);

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
        var world = (ClimbWorld)pkt.World;
        _panel.SetStatus(ClimbWorlds.IsStory(world)
            ? "Opening save selection..."
            : $"Opening {ClimbWorlds.Name(world)}...", true);
    }

    internal void Tick()
    {
        var currentScene = _state.CurrentScene;

        if (_forceNewGameOpts.HasValue)
        {
            if (SceneRoles.IsMainMenuArea(currentScene))
            {
                var s = _forceNewGameOpts.Value;
                // The lobby's mode decides the difficulty, so it is re-applied every frame:
                // the native New Game flow rewrites these options when the player walks
                // through it, and the last write before the launch is the one that counts.
                if (!GameOptionsInterop.SetNextGameMode(RulesOf(s), s.SkipTutorials,
                        s.SkipPractice, s.AssistEnabled, verbose: false))
                {
                    GameOptionsInterop.SetNextGameSkipOptions(
                        s.SkipTutorials, s.SkipPractice, s.AssistEnabled, verbose: false);
                }
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
                var world = (ClimbWorld)start.World;
                if (ClimbWorlds.IsStory(world)) LaunchStory(start);
                else LaunchSummit(start, world);
            }
        }
    }

    private void LaunchStory(ServerStartGame start)
    {
        var rules = RulesOf(start);
        if (!GameOptionsInterop.SetNextGameMode(rules, start.SkipTutorials,
                start.SkipPractice, start.AssistEnabled))
        {
            GameOptionsInterop.SetNextGameSkipOptions(
                start.SkipTutorials, start.SkipPractice, start.AssistEnabled);
        }
        _state.ModeRules = rules;

        _forceNewGameOpts = start;

        MainMenuInterop.ForceMainMenuStep(MultiplayerLaunchEntryStep);
        ModLog.Info("[StartGame] Opened native save selection — player chooses an existing or new save (options re-applied each frame)");

        ClearPendingStart();
        _panel.Hide();
    }

    /// <summary>
    /// A DLC destination is a Summit-mode world. Its difficulty is the game's own -- the run
    /// is set up by the world selection, not by the new-game options a story launch forces --
    /// so the mode only brings its session rules there: revives, spectating, the race.
    /// </summary>
    private void LaunchSummit(ServerStartGame start, ClimbWorld world)
    {
        if (!ClimbWorlds.IsKnown(world))
        {
            FailLaunch($"This lobby climbs somewhere this version of CairnMP does not know ({(int)world}).");
            return;
        }

        var access = SummitWorldInterop.Check(world);
        // The retry timer kept running since the launch began, so it doubles as the wait.
        if (access == SummitWorldAccess.MenuNotReady && _pendingStartRetryTimer < SummitMenuWaitSeconds)
            return;

        var name = ClimbWorlds.Name(world);
        if (access != SummitWorldAccess.Ready)
        {
            FailLaunch(SummitWorldInterop.Explain(world, access)
                ?? $"Summit mode did not open for {name}. Ask the host to start again.");
            return;
        }

        _state.ModeRules = RulesOf(start);
        if (!SummitWorldInterop.TryOpenWorldSelect(world))
        {
            FailLaunch($"Summit mode did not open for {name}. Ask the host to start again.");
            return;
        }

        ModLog.Info($"[StartGame] Opened the Summit world selection on {world} — player starts or continues their run");
        ClearPendingStart();
        _panel.Hide();
    }

    /// <summary>The panel stays open with the reason: the player is still in the lobby and
    /// can leave it, or fix what is missing and wait for the host's next start.</summary>
    private void FailLaunch(string reason)
    {
        ModLog.Warning($"[StartGame] Launch refused: {reason}");
        ClearPendingStart();
        _panel.SetStatus(reason, true);
    }

    private void ClearPendingStart()
    {
        _pendingStart = null;
        _pendingStartRetries = 0;
        _pendingStartRetryTimer = 0f;
    }
}
