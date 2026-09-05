using CairnMultiplayerMod.Internal.Diagnostics;
using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.MainMenu;

/// <summary>Drives Cairn's native MainMenu step machine (used during a host launch to
/// steer the menu toward the save screen).</summary>
internal static unsafe class MainMenuInterop
{
    /// <summary>
    /// The values match `TheGameBakers.Cairn.UI.MainMenu.Step` (seen via Cpp2IL).
    /// Only the ones we actually use are listed; the rest are here for reference.
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

    private static IntPtr _mainMenuKlass;

    private static IntPtr _mainMenuKlassPtr
    {
        get
        {
            if (_mainMenuKlass == IntPtr.Zero)
                _mainMenuKlass = IL2CPP.GetIl2CppClass(
                    "TheGameBakers.Cairn.Global.dll",
                    "TheGameBakers.Cairn.UI", "MainMenu");
            return _mainMenuKlass;
        }
    }

    /// <summary>
    /// Writes `MainMenu.ForceStepTransition = step` via the compiler-generated
    /// backing field. The game's Update loop reads this value every frame and triggers
    /// `TransitionToStep` when it isn't null, then resets it to null.
    /// </summary>
    public static bool ForceMainMenuStep(MainMenuStep step)
    {
        try
        {
            var klass = _mainMenuKlassPtr;
            if (klass == IntPtr.Zero)
            {
                ModLog.Error("[MainMenu] MainMenu class not found");
                return false;
            }

            var field = IL2CPP.GetIl2CppField(klass, "<ForceStepTransition>k__BackingField");
            if (field == IntPtr.Zero)
            {
                ModLog.Error("[MainMenu] ForceStepTransition backing field not found");
                return false;
            }

            // Nullable<Step> — the managed layout matches the .NET runtime:
            //   struct Nullable<T> { bool hasValue; T value; }
            // With T = an int-sized enum, total size of 8 bytes (1 byte bool,
            // 3 bytes padding, 4 bytes value).
            // Try BOTH Nullable layouts — the IL2CPP layout may differ from
            // managed .NET depending on the Unity version/platform.
            // Layout A: { bool hasValue(1), pad(3), T value(4) } = standard .NET
            // Layout B: { T value(4), bool hasValue(1), pad(3) } = some IL2CPP builds
            var buf = stackalloc byte[8];

            // Try layout A first (the one that worked in previous tests).
            *(byte*)buf = 1;
            *(int*)(buf + 4) = (int)step;
            IL2CPP.il2cpp_field_static_set_value(field, buf);

            ModLog.Debug($"[MainMenu] Forced MainMenu step -> {step} (field=0x{field:X})");
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error($"[MainMenu] ForceMainMenuStep({step}) failed: {ex}");
            return false;
        }
    }
}
