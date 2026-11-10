using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Unity.Cinemachine;

namespace CairnMultiplayerMod.Internal.Game.Life;

/// <summary>
/// Reclaims the view after Cairn's own camera pass.
///
/// The death hover camera is refreshed from <c>CameraManager.LateUpdate</c>, and the brain
/// applies whichever virtual camera won from its own <c>LateUpdate</c>. Both run after the
/// mod's update, so a priority set earlier in the frame is already stale. A managed
/// component cannot be added under IL2CPP without registering a type, so these two
/// postfixes are the late pass: whichever of them runs second writes the spectator pose
/// for the frame the player actually sees.
/// </summary>
internal static class SpectatorCameraDriver
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.SpectatorCamera");
    private static bool _installed;

    internal static void Install()
    {
        if (_installed) return;
        try
        {
            var brain = AccessTools.Method(typeof(CinemachineBrain), "LateUpdate")
                        ?? throw new MissingMethodException(nameof(CinemachineBrain), "LateUpdate");
            var manager = AccessTools.Method(typeof(CameraManager), "LateUpdate")
                          ?? throw new MissingMethodException(nameof(CameraManager), "LateUpdate");
            var after = new HarmonyMethod(typeof(SpectatorCameraDriver), nameof(AfterLateUpdate));
            Harmony.Patch(brain, postfix: after);
            Harmony.Patch(manager, postfix: after);
            _installed = true;
        }
        catch (Exception exception)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[Spectator] Could not keep the spectator camera live after the death shot: "
                           + exception.Message);
        }
    }

    private static void AfterLateUpdate() => SpectatorCamera.ClaimView();
}
