using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Il2CppCairn.UI;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// A lobby skips tutorials, and Cairn then keeps the gameplay-prompt canvas closed.
/// While the clip-in row is up, that one hide is ignored. Pause, cutscenes, and photo mode
/// still close it.
/// </summary>
internal static class RopeClipPromptPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.RopeClipPrompt");
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        try
        {
            var tutorials = AccessTools.PropertyGetter(typeof(GameplayPromptHud), "MustHideBecauseOfTutorials")
                            ?? throw new MissingMethodException(nameof(GameplayPromptHud), "MustHideBecauseOfTutorials");
            var mustHide = AccessTools.PropertyGetter(typeof(GameplayPromptHud), "MustHide")
                           ?? throw new MissingMethodException(nameof(GameplayPromptHud), "MustHide");
            var opened = AccessTools.Method(typeof(GameplayPromptHud), "UpdateOpenedOrClosed")
                         ?? throw new MissingMethodException(nameof(GameplayPromptHud), "UpdateOpenedOrClosed");
            var rowLate = AccessTools.Method(typeof(GameplayPromptUI), "LateUpdate")
                          ?? throw new MissingMethodException(nameof(GameplayPromptUI), "LateUpdate");
            var uiLate = AccessTools.Method(typeof(UIManager), "LateUpdate")
                         ?? throw new MissingMethodException(nameof(UIManager), "LateUpdate");

            Harmony.Patch(tutorials, prefix: new HarmonyMethod(typeof(RopeClipPromptPatch), nameof(SkipTutorialHide)));
            Harmony.Patch(mustHide, postfix: new HarmonyMethod(typeof(RopeClipPromptPatch), nameof(KeepClipTipVisible)));
            Harmony.Patch(opened, postfix: new HarmonyMethod(typeof(RopeClipPromptPatch), nameof(AfterCanvasDecision)));
            Harmony.Patch(rowLate, postfix: new HarmonyMethod(typeof(RopeClipPromptPatch), nameof(AfterPromptRow)));
            Harmony.Patch(uiLate, postfix: new HarmonyMethod(typeof(RopeClipPromptPatch), nameof(AfterInterfaceLateUpdate)));
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[Rope] Clip-in prompt hook unavailable: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
        RopeClipPrompt.Hide();
    }

    private static bool SkipTutorialHide(GameplayPromptHud __instance, ref bool __result)
    {
        if (!RopeClipPrompt.AllowDespiteSkippedTutorials(__instance)) return true;
        __result = false;
        return false;
    }

    private static void KeepClipTipVisible(GameplayPromptHud __instance, ref bool __result)
    {
        if (__result && RopeClipPrompt.AllowDespiteSkippedTutorials(__instance))
            __result = false;
    }

    private static void AfterCanvasDecision(GameplayPromptHud __instance)
    {
        if (RopeClipPrompt.AllowDespiteSkippedTutorials(__instance))
            RopeClipPrompt.RevealCanvas(__instance);
    }

    private static void AfterPromptRow(GameplayPromptUI __instance)
    {
        RopeClipPrompt.AfterRowLateUpdate(__instance);
    }

    private static void AfterInterfaceLateUpdate()
    {
        RopeClipPrompt.RevealIfAllowed();
    }
}
