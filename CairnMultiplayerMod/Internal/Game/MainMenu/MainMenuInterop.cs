using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using NativeMainMenu = Il2CppTheGameBakers.Cairn.UI.MainMenu;

namespace CairnMultiplayerMod.Internal.Game.MainMenu;

internal static class MainMenuInterop
{
    /// <summary>
    /// Numeric values must match Cairn's native enum because the launch path casts across IL2CPP.
    /// </summary>
    internal enum MainMenuStep
    {
        Initialization = 0,
        PressStart = 1,
        ModeSelect = 2,
        StoryModeManageSave = 3,
        LaunchNewStoryGame = 4,
        Loading = 12,
        LaunchSavedGame = 16,
        Settings = 13,
        DifficultySelect = 20,
        DifficultyCustomization = 21,
        ActivateTutorials = 22,
    }

    /// <summary>Cairn consumes and clears this nullable value from its next Update.</summary>
    public static bool ForceMainMenuStep(MainMenuStep step)
    {
        try
        {
            NativeMainMenu.ForceStepTransition = new Il2CppSystem.Nullable<NativeMainMenu.Step>(
                (NativeMainMenu.Step)(int)step);
            ModLog.Debug($"[MainMenu] Forced MainMenu step -> {step}");
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error($"[MainMenu] ForceMainMenuStep({step}) failed: {ex}");
            return false;
        }
    }
}
