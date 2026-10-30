using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.PersistentData;
using Il2CppTheGameBakers.Cairn.UI;
using StartData = Il2CppTheGameBakers.Cairn.UI.MainMenuSummitSelectElement.PackElement.MapElement.Layout.StartData;

namespace CairnMultiplayerMod.Internal.Game.MainMenu;

/// <summary>Whether this player's game can open a DLC destination.</summary>
internal enum SummitWorldAccess
{
    Ready,

    /// <summary>The main menu's Summit screens are not loaded, so there is nothing to ask yet.</summary>
    MenuNotReady,

    /// <summary>The pack that holds the world is not installed.</summary>
    NotInstalled,

    /// <summary>Installed, but this player has not unlocked the world yet.</summary>
    Locked,

    /// <summary>This version of Cairn has no Summit world by that id.</summary>
    Missing,
}

/// <summary>
/// The DLC destinations live in Cairn's Summit mode. A lobby launch takes the same road as the
/// main menu's own button: the onboarding pages for a player who has never read them, then
/// the world selection, opened on the lobby's world. Starting a new run or continuing the one
/// saved there stays the player's call, as choosing a story save does.
/// </summary>
internal static class SummitWorldInterop
{
    /// <summary>Asks the world selection itself, so the answer is the game's: the pack must
    /// be installed and the world unlocked, exactly as when the player browses it by hand.</summary>
    internal static SummitWorldAccess Check(ClimbWorld world)
    {
        try
        {
            var select = FindWorldSelect();
            if (select?.packs == null) return SummitWorldAccess.MenuNotReady;

            var found = false;
            var installed = false;
            foreach (var pack in select.packs)
            {
                if (pack?.maps == null) continue;
                var available = pack.IsAvailable();
                foreach (var map in pack.maps)
                {
                    var layouts = map?.layout?.data;
                    if (layouts == null) continue;
                    foreach (var layout in layouts)
                    {
                        var start = layout?.TryCast<StartData>();
                        var zone = start?.World;
                        if (zone == null || (int)zone.WorldId != (int)world) continue;

                        found = true;
                        if (!available) continue;
                        installed = true;
                        if (start.IsUnlocked()) return SummitWorldAccess.Ready;
                    }
                }
            }

            return !found ? SummitWorldAccess.Missing
                : !installed ? SummitWorldAccess.NotInstalled
                : SummitWorldAccess.Locked;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("summit.check-world", exception);
            return SummitWorldAccess.MenuNotReady;
        }
    }

    /// <summary>What to tell a player who cannot go, or null when nothing stands in the way.
    /// A menu that is not loaded yet is not a refusal: the launch asks again once it is.</summary>
    internal static string WhyUnavailable(ClimbWorld world)
        => ClimbWorlds.IsStory(world) ? null : Explain(world, Check(world));

    internal static string Explain(ClimbWorld world, SummitWorldAccess access)
    {
        var name = ClimbWorlds.Name(world);
        return access switch
        {
            SummitWorldAccess.NotInstalled =>
                $"{name} needs the free DLC {ClimbWorlds.DeepWaterDlcName}. Add it to your Steam library.",
            SummitWorldAccess.Locked => $"{name} is still locked in your game. Unlock it in Summit mode first.",
            SummitWorldAccess.Missing => $"{name} is not in this version of Cairn.",
            _ => null,
        };
    }

    /// <summary>Opens the world selection on <paramref name="world"/>, through the onboarding
    /// pages when the player has never read them. The game hands those pages over to the
    /// world selection itself, and the selection keeps the world until it opens.</summary>
    internal static bool TryOpenWorldSelect(ClimbWorld world)
    {
        try
        {
            var select = FindWorldSelect();
            if (select == null) return false;

            select.PreselectWorld((StreamingTweakables.World)(int)world);
            var step = EntryStep(HasDoneOnboarding());
            ModLog.Info($"[Summit] Opening {step} on {world}.");
            return MainMenuInterop.ForceMainMenuStep(step);
        }
        catch (Exception exception)
        {
            ModLog.Error($"[Summit] Could not open the world selection on {world}: {exception}");
            return false;
        }
    }

    /// <summary>The step the main menu's Summit button would take for this player.</summary>
    internal static MainMenuInterop.MainMenuStep EntryStep(bool hasDoneOnboarding)
        => hasDoneOnboarding
            ? MainMenuInterop.MainMenuStep.SummitModeWorldSelect
            : MainMenuInterop.MainMenuStep.SummitModeOnboarding;

    /// <summary>A player whose progress cannot be read goes straight to the world selection:
    /// the onboarding pages explain the mode, they do not unlock anything.</summary>
    private static bool HasDoneOnboarding()
    {
        try
        {
            var manager = PersistentDataManager.Instance;
            return manager == null || manager.playerData.hasDoneSummitOnboarding;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("summit.read-onboarding", exception);
            return true;
        }
    }

    private static MainMenuSummitSelectElement FindWorldSelect()
    {
        var elements = GameOptionsInterop.FindMainMenuComponent()?.elements;
        if (elements == null) return null;
        foreach (var element in elements)
        {
            var select = element?.TryCast<MainMenuSummitSelectElement>();
            if (select != null) return select;
        }
        return null;
    }
}
