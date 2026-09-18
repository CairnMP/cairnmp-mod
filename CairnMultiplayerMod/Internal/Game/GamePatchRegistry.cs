using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Bivouac;
using CairnMultiplayerMod.Internal.Game.Life;
using CairnMultiplayerMod.Internal.Game.MainMenu;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Game.Roping;

namespace CairnMultiplayerMod.Internal.Game;

internal static class GamePatchRegistry
{
    private sealed class PatchModule
    {
        public PatchModule(string name, Action install, Action uninstall)
        {
            Name = name;
            Install = install;
            Uninstall = uninstall;
        }

        public string Name { get; }
        public Action Install { get; }
        public Action Uninstall { get; }
    }

    private static readonly PatchModule[] Modules =
    {
        new("netplay frame", NetplaySetFramePatch.Install, NetplaySetFramePatch.Uninstall),
        new("bivouac safety", BivouacDiagnostics.Install, BivouacDiagnostics.Uninstall),
        new("direct rope team", RopeTeamFallPatch.Install, RopeTeamFallPatch.Uninstall),
        new("multiplayer pause", MultiplayerPausePatch.Install, MultiplayerPausePatch.Uninstall),
        new("main-menu buttons", MainMenuButtonIntegration.Install, MainMenuButtonIntegration.Uninstall),
        new("free-roam unlock", FreeRoamUnlockPatch.Install, FreeRoamUnlockPatch.Uninstall),
        new("savegame piton guard", SavegamePitonGuardPatch.Install, SavegamePitonGuardPatch.Uninstall),
        new("native performance probe", NativePerformanceProbe.Install, NativePerformanceProbe.Uninstall),
        new("downed climbers", DeathScreenPatch.Install, DeathScreenPatch.Uninstall),
        new("revive prompt", RevivePromptPatch.Install, RevivePromptPatch.Uninstall),
    };

    private static readonly List<PatchModule> Installed = new();

    public static void InstallAll()
    {
        if (Installed.Count != 0) return;

        foreach (var module in Modules)
        {
            try
            {
                module.Install();
                Installed.Add(module);
            }
            catch
            {
                UninstallAll();
                throw;
            }
        }
    }

    public static void UninstallAll()
    {
        for (var index = Installed.Count - 1; index >= 0; index--)
        {
            var module = Installed[index];
            try { module.Uninstall(); }
            catch (Exception exception)
            {
                ModLog.Warning($"[CairnMP] Could not uninstall {module.Name} patch: {exception.Message}");
            }
        }
        Installed.Clear();
    }
}
