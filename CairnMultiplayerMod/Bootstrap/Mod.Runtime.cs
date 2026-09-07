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
        _loadedScenes.Add(sceneName);
        if (SceneRoles.IsGameplayRoot(sceneName))
            _runtimeState.LastGameplayScene = sceneName;

        _runtimeState.CurrentScene = sceneName;
        _runtimeState.TimeSinceLastSceneLoad = 0f;
        LogDebug($"Scene loaded: [{buildIndex}] {sceneName}");

        var isMainMenu = SceneRoles.IsMainMenu(sceneName);

        // FreeRoam unlock: active ONLY at the MainMenu (forcing the flag during boot
        // or in game sends the game onto an unready FreeRoam init path -> black screen).
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
        _loadedScenes.Remove(sceneName);

        if (string.Equals(CurrentScene, sceneName, StringComparison.Ordinal))
            _runtimeState.CurrentScene = ResolveCurrentSceneAfterUnload(sceneName);

        if (!SceneRoles.IsBivouac(sceneName))
            return;

        _runtimeState.TimeSinceLastSceneLoad = 0f;
        LogDebug($"Scene unloaded: [{buildIndex}] {sceneName}");
        LogDebug($"[State] Scene {sceneName} overlay unloaded for multiplayer");
        ResetSceneBoundSyncState();
    }

    /// <summary>Forgets everything bound to the scene we're leaving: IL2CPP caches held by
    /// the game services, then the sync timers.</summary>
    private void ResetSceneBoundSyncState()
    {
        SceneCache.Reset();
        _hud?.Clear();
        ResetSyncTimers();
        Features?.NotifySceneReset();
    }

    /// <summary>Restarts the broadcast cadence from scratch, without touching the caches.
    /// Features rearm their own cadence through OnSceneReset.</summary>
    private void ResetSyncTimers()
    {
        Player.ResetSyncState();
        Player.ResetTimers();
    }

    private bool IsGameplaySyncSuspended() => Bivouac.BlocksGameplaySync();

    /// <summary>After unloading a scene, the current scene becomes the gameplay root
    /// that remains loaded, or the last known root when a different scene was unloaded.</summary>
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
        _hud?.Tick();
        _inventory?.Tick();

        // PANIC failsafe (F10): force-unblock the game's inputs whatever the state. Read
        // from the raw keyboard device (never affected by the block) and placed BEFORE any
        // early return below, so it stays reachable even mid-bivouac. Whoever captured the
        // keyboard releases it on its own side — the chat feature also listens for F10.
        var panicKeyboard = Keyboard.current;
        if (panicKeyboard != null && panicKeyboard.f10Key.wasPressedThisFrame)
        {
            InputInterop.ForceClearBlock();
            LoggerInstance.Msg("[CairnMP] Panic: input force-cleared (F10)");
        }

        // While a mod UI captures the keyboard for chat, suppress the mod's shortcuts so
        // typing cannot trigger actions. InputManager independently blocks the game input.
        var chatTyping = _game.Input.IsKeyboardCaptured;

        // Update the button injection in the main menu
        if (SceneRoles.IsMainMenu(CurrentScene))
        {
            _mainMenu.Tick();
            // Unlock FreeRoam: force the tweakable field as soon as it's loaded (no-op
            // once it succeeds). Complements the Harmony postfix on the public property.
            FreeRoamUnlockPatch.TryForceTweakableField();
            // Unhide the FreeRoam mode in the difficulty list (isHidden=false).
            FreeRoamUnlockPatch.TryUnhideDifficulty();
        }

        // Pump the managed Steam callback queue (also handles deferred init).
        Lobby.Pump(Time.unscaledDeltaTime);

        // Lock the gameplay layer before processing network packets.
        Bivouac.Update();

        // Process network events
        Network.Update();

        // Features that must keep running whatever the state (input, HUD upkeep) — placed
        // before the bivouac early-return, like the other always-on ticks below.
        Features.Tick(FeaturePhase.Always);

        // Name toggle (N) — placed BEFORE the bivouac/photo suspension return so it stays
        // reachable in photo mode (where gameplay is suspended).
        if (!chatTyping) TickNameToggleInput();

        // Injection of the "N" row into the native photo-mode legend. The clone is
        // instantiated under an inactive parent, stripped of its non-visual components to
        // prevent duplicated input handlers from blocking the game, and injected only while
        // photo mode is open.
        try { PhotoModeNamesRow.Tick(); }
        catch (Exception ex) { LoggerInstance.Error($"[PhotoNames] tick failed: {ex.Message}"); }

        // During a bivouac, Cairn itself drives the pawn, the camera and the taping
        // hands. The mod only keeps a minimal network presence.
        if (Bivouac.IsSuspended)
        {
            Bivouac.TickSuspendedLog();
            Player.TickSuspendedNetworkPresence();
            TickConnectingStatus();
            return;
        }

        // Handles the cursor blink + lobby refresh for the Canvas connection panel.
        _panel.Tick(Time.unscaledDeltaTime);

        // Recompute the local lifecycle state from the scene + handshake + MC.
        var newState = Player.ComputeLocalState();
        SetLocalState(newState);

        // Diagnostic: watch for the sync recovering after a bivouac.
        Bivouac.TickRecoveryLog();

        // Game launch flow
        StartGame.Tick();

        // Periodic broadcast of the local player state + sync of remote ghosts.
        try
        {
            Player.Tick();
        }
        catch (Exception ex)
        {
            LoggerInstance.Error(ex.ToString());
            CrashHandler.RecordRecoverableExceptionOnce(ex, "Mod.TickPlayerSync");
        }

        // Features that touch the world — only once gameplay sync is active.
        Features.Tick(FeaturePhase.Gameplay);

        // Progressive waiting status during lobby creation/join (provisioning).
        TickConnectingStatus();

        // Long-distance post-teleport settle (prevents falling into the void + fixes the
        // zone-load repositioning). No-op if no teleport is pending.
        TeleportInterop.TickSettle(LocalState == PlayerState.InGame);

        // Inter-player roping: clip detection (E) + maintenance of the NATIVE rope team.
        // No more cosmetic rope: the lifeline's native rope (clipped to a mobile piton
        // placed on the partner through the native rope-link system) provides both the
        // visual rope and the belay.
        try
        {
            Rope.Tick(); // Input is gated inside; safety and anchor maintenance always run.
        }
        catch (Exception ex) { LoggerInstance.Error($"[RopeCouple] tick failed: {ex.Message}"); }

        // Keyboard shortcuts via the new Input System
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        // Chat open -> no mod shortcut passes through (total block).
        if (chatTyping) return;

        if (keyboard[_connectKey].wasPressedThisFrame)
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

        if (keyboard[_disconnectKey].wasPressedThisFrame)
        {
            if (Network.IsConnected)
            {
                OnDisconnectRequested();
            }
        }

        // While the multiplayer panel is open, reassert the blocking of the menu's action maps
        // to prevent any background navigation (Delete, arrows, back).
        if (_panel.IsVisible)
            InputInterop.BlockMainMenuActionMaps();
    }

    private static Key ParseKey(string name, Key fallback)
    {
        return Enum.TryParse<Key>(name, true, out var result) ? result : fallback;
    }

    /// <summary>
    /// N key: toggles the display of remote players' name plates. Works both in game
    /// and in photo mode (the hint then appears at the bottom left via PhotoModeHud).
    /// Read from the raw keyboard device.
    /// </summary>
    private void TickNameToggleInput()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (!kb[Key.N].wasPressedThisFrame) return;

        var shown = RemotePlayerManager.ToggleNames();
        LoggerInstance.Msg($"[CairnMP] Player names {(shown ? "shown" : "hidden")} (N)");
    }

    private void SetLocalState(PlayerState state)
    {
        if (state == LocalState)
            return;

        LoggerInstance.Msg($"[State] local: {LocalState} -> {state}");
        _runtimeState.LocalPlayerState = state;
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
        _panel?.OnGUI();
        Features?.DrawHud();
        _hud?.Draw();
    }

    /// <summary>Adapts the displayed status while we wait for the Steam round-trip for
    /// lobby creation / join. No server provisioning here — the callback is typically
    /// &lt; 1 s — so short messages only.</summary>
    private void TickConnectingStatus()
    {
        if (!_connectingStart.HasValue) return;
        // The OnLobbyEntered event will clear _connectingStart and write "Connected".
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
