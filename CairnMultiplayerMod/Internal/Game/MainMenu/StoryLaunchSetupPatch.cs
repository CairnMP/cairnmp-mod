using System;
using System.Reflection;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.UI;

namespace CairnMultiplayerMod.Internal.Game.MainMenu;

/// <summary>
/// A customized story <see cref="GameSetup"/> is built by the constructor that leaves
/// summit mode on. <see cref="MainMenuLaunchGameElement"/> then skips the reset the
/// preset constructor gets, so the new game asks for Summit gear and tracks energy.
/// This patch applies that missing reset, and only while a multiplayer story handoff
/// is inside <c>LaunchStoryMode</c>. Summit launches and solo story launches do not
/// set the flag, and loading a save does not go through this constructor.
/// </summary>
internal static class StoryLaunchSetupPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.StoryLaunchSetup");
    private static bool _installed;
    private static bool _failed;
    private static int _launchDepth;

    /// <summary>Set for the life of a multiplayer story handoff, from the save selection
    /// until the menu scene is left or the launch is cancelled.</summary>
    internal static bool MultiplayerStoryPending { get; set; }

    public static void Install()
    {
        if (_installed || _failed) return;

        try
        {
            var launch = AccessTools.Method(typeof(MainMenuLaunchGameElement), "LaunchStoryMode");
            var constructor = FindCustomSetupConstructor();
            var prefix = PatchMethod(nameof(LaunchStoryPrefix));
            var finalizer = PatchMethod(nameof(LaunchStoryFinalizer));
            var postfix = PatchMethod(nameof(AfterCustomSetup));

            if (launch == null || constructor == null || prefix == null || finalizer == null || postfix == null)
            {
                _failed = true;
                ModLog.Warning("[GameOptions] Story launch setup patch methods were not found");
                return;
            }

            Harmony.Patch(launch, prefix: new HarmonyMethod(prefix), finalizer: new HarmonyMethod(finalizer));
            Harmony.Patch(constructor, postfix: new HarmonyMethod(postfix));
            _installed = true;
            ModLog.Info("[GameOptions] Story launch setup patch installed");
        }
        catch (Exception exception)
        {
            try { Harmony.UnpatchSelf(); }
            catch (Exception unpatch)
            {
                ModLog.SuppressedException("story-launch.unpatch", unpatch);
            }

            _failed = true;
            _launchDepth = 0;
            ModLog.Warning($"[GameOptions] Story launch setup patch failed: {exception.Message}");
        }
    }

    public static void Uninstall()
    {
        try { Harmony.UnpatchSelf(); }
        finally
        {
            _installed = false;
            _failed = false;
            _launchDepth = 0;
            MultiplayerStoryPending = false;
        }
    }

    private static void LaunchStoryPrefix() => _launchDepth++;

    private static Exception LaunchStoryFinalizer(Exception __exception)
    {
        try
        {
            if (MultiplayerStoryPending && _launchDepth > 0)
                ClearSummitMode(GameSetup.GetCurrentGameSetup());
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("story-launch.normalize-setup", exception);
        }
        finally
        {
            if (_launchDepth > 0) _launchDepth--;
        }

        return __exception;
    }

    private static void AfterCustomSetup(GameSetup __instance)
    {
        if (!MultiplayerStoryPending || _launchDepth <= 0) return;
        ClearSummitMode(__instance);
    }

    private static void ClearSummitMode(GameSetup setup)
    {
        if (setup != null && setup.summitMode)
            setup.summitMode = false;
    }

    private static ConstructorInfo FindCustomSetupConstructor()
    {
        foreach (var constructor in AccessTools.GetDeclaredConstructors(typeof(GameSetup)))
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length != 8) continue;
            for (var index = 0; index < parameters.Length; index++)
            {
                if (parameters[index].ParameterType.Name == "CustomizedDifficulty")
                    return constructor;
            }
        }

        return null;
    }

    private static MethodInfo PatchMethod(string name)
        => typeof(StoryLaunchSetupPatch).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
}
