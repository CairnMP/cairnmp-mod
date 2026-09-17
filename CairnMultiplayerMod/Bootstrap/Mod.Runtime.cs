using System;
using System.Linq;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Game.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Bootstrap;

public partial class Mod
{
    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        => RunGuarded("Mod.OnSceneWasLoaded", () => HandleSceneLoaded(buildIndex, sceneName));

    private void HandleSceneLoaded(int buildIndex, string sceneName)
    {
        MarkPerformanceScene("scene-loaded", sceneName);
        _loadedScenes.Add(sceneName);
        if (SceneRoles.IsGameplayRoot(sceneName))
            _runtimeState.LastGameplayScene = sceneName;

        _runtimeState.CurrentScene = sceneName;
        // Cairn streams art/audio/LOD layers constantly while the pawn remains valid.
        // Only a real gameplay boundary should restart the graph-stability timer.
        if (SceneRoles.IsSyncResetPoint(sceneName) || SceneRoles.IsBivouac(sceneName))
            _runtimeState.TimeSinceLastSceneLoad = 0f;
        LogDebug($"Scene loaded: [{buildIndex}] {sceneName}");

        var isMainMenu = SceneRoles.IsMainMenu(sceneName);

        // FreeRoam unlock: available to every player at the MainMenu. Forcing the flag
        // during boot or in game sends Cairn onto an unready init path -> black screen.
        FreeRoamUnlockPatch.SetActive(isMainMenu);

        if (!isMainMenu)
            _panel.DestroyResources();

        if (SceneRoles.IsBivouac(sceneName))
        {
            LogDebug($"[State] Scene {sceneName} treated as Loading for multiplayer");
            Bivouac.LogPhase("scene-loaded");
        }

        // Note: we do NOT clear the pings here — they're positioned in the world and
        // expire on their own (15 s). Clearing them on every scene stream would make
        // them disappear while we're still in the area.
        if (SceneRoles.IsSyncResetPoint(sceneName))
        {
            ResetSceneBoundSyncState();
            RemotePlayerManager.ClearAll();
        }

        _mainMenu.OnSceneLoaded(sceneName);

        if (!isMainMenu)
            return;

        var inLobby = Lobby.IsInLobby;
        // Still connected but no lobby left to belong to: cut it here so the player can
        // reconnect cleanly instead of dragging a half-dead session around the menu.
        var strandedConnection = Network.IsConnected && !inLobby;
        if (strandedConnection)
        {
            LoggerInstance.Msg("[State] Returned to MainMenu — auto-disconnecting");
            Network.Disconnect();
            _panel.SetStatus("Disconnected (returned to menu)", false);
        }
        else if (!inLobby)
        {
            // The ghosts and rope links belong to the session we just left. Nothing to
            // clear if we were neither connected nor in a lobby.
            return;
        }

        RemotePlayerManager.ClearAll();
        Rope.ClearLinks();
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        => RunGuarded("Mod.OnSceneWasUnloaded", () => HandleSceneUnloaded(buildIndex, sceneName));

    private void HandleSceneUnloaded(int buildIndex, string sceneName)
    {
        MarkPerformanceScene("scene-unloaded", sceneName);
        _loadedScenes.Remove(sceneName);

        // Same reason as in SetLocalState: a persistent ghost collider must never be
        // registered with the physics scene that is going away.
        if (SceneRoles.IsGameplayRoot(sceneName) || SceneRoles.IsSyncResetPoint(sceneName))
            RemotePlayerManager.SuspendPhysics();

        if (string.Equals(CurrentScene, sceneName, StringComparison.Ordinal))
            _runtimeState.CurrentScene = ResolveCurrentSceneAfterUnload(sceneName);

        if (!SceneRoles.IsBivouac(sceneName))
            return;

        _runtimeState.TimeSinceLastSceneLoad = 0f;
        LogDebug($"Scene unloaded: [{buildIndex}] {sceneName}");
        LogDebug($"[State] Scene {sceneName} overlay unloaded for multiplayer");
        ResetSceneBoundSyncState();
    }

    private void ResetSceneBoundSyncState()
    {
        SceneCache.Reset();
        _inventory?.InvalidateNativeUiCache();
        PhotoModeNamesRow.InvalidateNativeUiCache();
        _hud?.Clear();
        ResetSyncTimers();
        Features?.NotifySceneReset();
    }

    private void ResetSyncTimers()
    {
        Player.ResetSyncState();
        Player.ResetTimers();
    }

    private bool IsGameplaySyncSuspended() => Bivouac.BlocksGameplaySync();

    private string ResolveCurrentSceneAfterUnload(string unloadedScene)
    {
        var loadedGameplayScene = _loadedScenes.FirstOrDefault(SceneRoles.IsGameplayRoot);
        if (loadedGameplayScene != null)
            return loadedGameplayScene;

        return !string.Equals(LastGameplayScene, unloadedScene, StringComparison.Ordinal)
            ? LastGameplayScene
            : null;
    }

    public override void OnUpdate()
    {
        if (CrashHandler.IsFatal)
        {
            TickFatalShutdown();
            return;
        }

        try
        {
            TickPerformance();
            using var performance = Measure(PerformanceArea.ModUpdate);
            TickMod();
        }
        catch (Exception exception)
        {
            CrashHandler.Fatal(exception, "Mod.OnUpdate");
            TickFatalShutdown();
        }
    }

    private void TickMod()
    {
        _runtimeState.TimeSinceLastSceneLoad += Time.unscaledDeltaTime;

        // Arm controller shortcuts only after their modifier has already blocked Cairn for
        // one frame. This runs before inventory/features so every consumer sees one coherent
        // controller snapshot and secondary buttons cannot trigger both action sets.
        ModControllerInput.Update();
        InputCaptureState.IsControllerShortcutCaptured = ModControllerInput.IsModifierHeld
            && (_multiplayerModeActive || SceneRoles.IsMainMenu(CurrentScene));
        InputInterop.ReconcileGameplayInput(InputCaptureState.WantsGameplayBlocked);

        if (_multiplayerModeActive)
        {
            using var performance = Measure(PerformanceArea.Ui);
            _hud?.Tick();
            _inventory?.Tick();
        }

        // PANIC failsafe (F10): force-unblock the game's inputs whatever the state. Read
        // from the raw keyboard device (never affected by the block) and placed BEFORE any
        // early return below, so it stays reachable even mid-bivouac. Whoever captured the
        // keyboard releases it on its own side — the chat feature also listens for F10.
        var panicKeyboard = Keyboard.current;
        var panicPressed = panicKeyboard != null && panicKeyboard.f10Key.wasPressedThisFrame;
        panicPressed |= ModControllerInput.WasPressed(ControllerShortcut.Panic);
        if (panicPressed)
        {
            InputInterop.ForceClearBlock();
            LoggerInstance.Msg("[CairnMP] Panic: input force-cleared");
        }

        // While a mod UI captures the keyboard for chat, suppress the mod's shortcuts so
        // typing cannot trigger actions. InputManager independently blocks the game input.
        var chatTyping = _game.Input.IsKeyboardCaptured;

        if (SceneRoles.IsMainMenu(CurrentScene))
        {
            using var performance = Measure(PerformanceArea.Ui);
            _mainMenu.Tick();
            // Unlock FreeRoam: force the tweakable field as soon as it's loaded (no-op
            // once it succeeds). Complements the Harmony postfix on the public property.
            FreeRoamUnlockPatch.SetActive(true);
            FreeRoamUnlockPatch.TryForceTweakableField();
            FreeRoamUnlockPatch.TryUnhideDifficulty();
        }

        using (Measure(PerformanceArea.Network))
        {
            Lobby.Pump(Time.unscaledDeltaTime);
            CompleteBrowserRequest();
        }

        Bivouac.Update();

        using (Measure(PerformanceArea.Network)) Network.Update();

        // Features that must keep running whatever the state (input, HUD upkeep) — placed
        // before the bivouac early-return, like the other always-on ticks below.
        using (Measure(PerformanceArea.Features)) Features.Tick(FeaturePhase.Always);

        // Name toggle (N) — placed BEFORE the bivouac/photo suspension return so it stays
        // reachable in photo mode (where gameplay is suspended).
        if (_multiplayerModeActive && !chatTyping) TickNameToggleInput();

        if (_multiplayerModeActive)
        {
            try
            {
                using var performance = Measure(PerformanceArea.Ui);
                PhotoModeNamesRow.Tick();
            }
            catch (Exception ex) { LoggerInstance.Error($"[PhotoNames] tick failed: {ex.Message}"); }
        }

        // During a bivouac, Cairn itself drives the pawn, the camera and the taping
        // hands. The mod only keeps a minimal network presence.
        if (Bivouac.IsSuspended)
        {
            Bivouac.TickSuspendedLog();
            using (Measure(PerformanceArea.Players)) Player.TickSuspendedNetworkPresence();
            TickConnectingStatus();
            return;
        }

        using (Measure(PerformanceArea.Ui)) _panel.Tick(Time.unscaledDeltaTime);

        var newState = Player.ComputeLocalState();
        SetLocalState(newState);

        Bivouac.TickRecoveryLog();

        StartGame.Tick();

        try
        {
            using var performance = Measure(PerformanceArea.Players);
            Player.Tick();
        }
        catch (Exception ex)
        {
            LoggerInstance.Error(ex.ToString());
            CrashHandler.RecordRecoverableExceptionOnce(ex, "Mod.TickPlayerSync");
        }

        using (Measure(PerformanceArea.Features)) Features.Tick(FeaturePhase.Gameplay);

        TickConnectingStatus();

        // Long-distance post-teleport settle (prevents falling into the void + fixes the
        // zone-load repositioning). No-op if no teleport is pending.
        TeleportInterop.TickSettle(LocalState == PlayerState.InGame);

        // Inter-player roping: dedicated keyboard/controller input + maintenance of the NATIVE rope team.
        // A dedicated native rope attaches to both harnesses and provides the visual
        // rope and belay without creating pitons or changing personal rope topology.
        try
        {
            using var performance = Measure(PerformanceArea.Ropes);
            Rope.Tick(); // Input is gated inside; safety and anchor maintenance always run.
        }
        catch (Exception ex) { LoggerInstance.Error($"[RopeCouple] tick failed: {ex.Message}"); }

        if (chatTyping) return;

        var keyboard = Keyboard.current;
        var togglePanel = keyboard != null && keyboard[_connectKey].wasPressedThisFrame;
        togglePanel |= ModControllerInput.WasPressed(ControllerShortcut.TogglePanel);
        if (_panel.IsVisible && Gamepad.current?.buttonEast.wasPressedThisFrame == true)
            togglePanel = true;
        if (togglePanel)
        {
            if (_panel.IsVisible)
            {
                _panel.Hide();
            }
            else
            {
                if (!SceneRoles.IsMainMenu(CurrentScene))
                {
                    LoggerInstance.Msg("[CairnMP] Multiplayer panel is only available from the main menu.");
                    return;
                }

                _mainMenu.SuspendNativeMenu();
                _panel.Show();
            }
        }

        if (keyboard != null && keyboard[_disconnectKey].wasPressedThisFrame)
        {
            if (Network.IsConnected)
            {
                OnDisconnectRequested();
            }
        }

        // While the multiplayer panel is open, reassert the blocking of the menu's action maps
        // to prevent any background navigation (Delete, arrows, back).
        if (_panel.IsVisible)
            InputInterop.BlockMainMenuActionMaps(allowOverlayNavigation: true);
    }

    private static Key ParseKey(string name, Key fallback)
    {
        return Enum.TryParse<Key>(name, true, out var result) ? result : fallback;
    }

    private void TickNameToggleInput()
    {
        var keyboardPressed = Keyboard.current?[Key.N].wasPressedThisFrame == true;
        if (!keyboardPressed && !ModControllerInput.WasPressed(ControllerShortcut.ToggleNames)) return;

        var shown = RemotePlayerManager.ToggleNames();
        LoggerInstance.Msg($"[CairnMP] Player names {(shown ? "shown" : "hidden")}");
    }

    private void SetLocalState(PlayerState state)
    {
        if (state == LocalState)
            return;

        LoggerInstance.Msg($"[State] local: {LocalState} -> {state} ({Player.LastComputedStateReason})");
        _runtimeState.LocalPlayerState = state;

        // Leaving gameplay means Cairn is about to tear the scene down. Ghosts survive the
        // transition (DontDestroyOnLoad), so their colliders must leave the physics scene
        // now — a moving collider registered while it unloads is a PhysX hazard.
        if (state != PlayerState.InGame)
            RemotePlayerManager.SuspendPhysics();
    }

    public override void OnGUI()
    {
        if (CrashHandler.IsFatal)
        {
            CrashScreen.Draw(CrashHandler.State);
            return;
        }

        RunGuarded("Mod.OnGUI", DrawModUi);
    }

    private void DrawModUi()
    {
        using var performance = Measure(PerformanceArea.Ui);
        _panel?.OnGUI();
        Features?.DrawHud();
        if (_multiplayerModeActive)
            _hud?.Draw();
    }

    private void TickConnectingStatus()
    {
        if (!_connectingStart.HasValue) return;
        if (Lobby.IsInLobby) { _connectingStart = null; return; }

        var elapsed = (DateTime.UtcNow - _connectingStart.Value).TotalSeconds;
        var msg = elapsed switch
        {
            < 3 => $"{_connectingVerb}...",
            < 10 => $"{_connectingVerb} — waiting for Steam...",
            _ => $"{_connectingVerb} — taking longer than usual..."
        };
        _panel.SetConnecting(msg);
    }

    public override void OnDeinitializeMelon()
    {
        try
        {
            StopRuntime();
            LoggerInstance.Msg("Cairn Multiplayer Mod unloaded.");
        }
        catch (Exception exception)
        {
            CrashHandler.RecordRecoverableExceptionOnce(exception, "Mod.OnDeinitializeMelon");
        }
    }

    private void TickFatalShutdown()
    {
        StopRuntime();
        if (!CrashHandler.ShouldClose || _quitIssued) return;

        _quitIssued = true;
        LoggerInstance.Msg("[CairnMP] Closing Cairn after the fatal-error report was saved.");
        Application.Quit();
    }

    private void StopRuntime()
    {
        if (_runtimeStopped) return;
        _runtimeStopped = true;

        SafeStop("performance diagnostics", StopPerformance);
        SafeStop("input", InputInterop.ForceClearBlock);
        SafeStop("panel", () =>
        {
            _panel?.Hide();
            _panel?.DestroyResources();
        });
        SafeStop("feature session", () => Features?.NotifySessionEnded());
        SafeStop("features", () => Features?.Dispose());
        SafeStop("voice", () => _voice?.Dispose());
        SafeStop("main-menu registration", () => _mainMenuButton?.Dispose());
        SafeStop("main-menu adapter", () => _mainMenu?.Dispose());
        SafeStop("remote players", RemotePlayerManager.ClearAll);
        SafeStop("network disconnect", () => Network?.Disconnect());
        SafeStop("Steam lobby", () => Lobby?.Dispose());
        SafeStop("network transport", () => Network?.Dispose());
        SafeStop("IL2CPP exception capture", Il2CppExceptionCapture.Uninstall);
        SafeStop("game patches", GamePatchRegistry.UninstallAll);
        SafeStop("crash handlers", CrashHandler.Shutdown);
        SafeStop("feature logging", () => FeatureLog.SetSink(null, null, null));
        SafeStop("internal logging", ModLog.Shutdown);
    }

    private void SafeStop(string component, Action stop)
    {
        try { stop(); }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"[CairnMP] Could not stop {component}: {exception.Message}");
        }
    }

    private static void RunGuarded(string context, Action action)
    {
        if (CrashHandler.IsFatal) return;
        try { action(); }
        catch (Exception exception) { CrashHandler.Fatal(exception, context); }
    }
}
