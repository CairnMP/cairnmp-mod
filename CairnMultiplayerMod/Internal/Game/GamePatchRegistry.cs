using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Bivouac;
using CairnMultiplayerMod.Internal.Game.Players.Avatar;
using CairnMultiplayerMod.Internal.Game.Roping;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>Owns the complete, symmetric lifecycle of CairnMP's Harmony patches.</summary>
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
        new("bivouac safety", BivouacSafetyPatch.Install, BivouacSafetyPatch.Uninstall),
        new("rope-team template", RopeTeamFallPatch.Install, RopeTeamFallPatch.Uninstall),
        new("multiplayer pause", MultiplayerPausePatch.Install, MultiplayerPausePatch.Uninstall),
        new("free-roam unlock", FreeRoamUnlockPatch.Install, FreeRoamUnlockPatch.Uninstall),
        new("savegame piton guard", SavegamePitonGuardPatch.Install, SavegamePitonGuardPatch.Uninstall),
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
