using System;
using System.Reflection;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2CppCairn.UI;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// "Multiplayer-friendly" pause: in solo, opening the pause menu (ESC) freezes everything
/// (Time.timeScale = 0). In multiplayer, that local freeze would disconnect the player from the
/// shared world — the day/night clock (host-authoritative) freezes for everyone, remote
/// avatars stop updating, etc. So we neutralize ONLY the pause menu's pause requests
/// during a multiplayer session: the world keeps running for everyone,
/// while the local climber stays frozen in place (the menu's input context
/// already cuts gameplay -> no action reaches it).
///
/// We target precisely <see cref="PauseMenu"/> (ESC), distinct from the other legitimate
/// pauses (cutscene, dialogue, loading) which must keep freezing.
/// </summary>
internal static unsafe class MultiplayerPausePatch
{
    private static readonly HarmonyLib.Harmony MpPauseHarmony = new("CairnMultiplayerMod.MultiplayerPausePatch");
    private static bool _mpPausePatchInstalled;
    private static bool _mpPausePatchFailed;

    private static readonly PauseRequestSuppressionState Suppression = new();
    private static Func<bool> _isMultiplayerConnected = () => false;

    internal static void Configure(Func<bool> isMultiplayerConnected)
        => _isMultiplayerConnected = isMultiplayerConnected ?? (() => false);

    public static void Install()
    {
        if (_mpPausePatchInstalled || _mpPausePatchFailed) return;

        try
        {
            var onOpening = AccessTools.Method(typeof(PauseMenu), nameof(PauseMenu.OnOpening));
            var onClosing = AccessTools.Method(typeof(PauseMenu), nameof(PauseMenu.OnClosing));
            var requestPause = AccessTools.Method(typeof(Il2Cpp.TimeManager), nameof(Il2Cpp.TimeManager.RequestPause));
            var requestUnpause = AccessTools.Method(typeof(Il2Cpp.TimeManager), nameof(Il2Cpp.TimeManager.RequestUnpause));
            var requestGameTimePause = AccessTools.Method(
                typeof(Il2Cpp.TimeManager), nameof(Il2Cpp.TimeManager.RequestGameTimePause));
            var requestGameTimeUnpause = AccessTools.Method(
                typeof(Il2Cpp.TimeManager), nameof(Il2Cpp.TimeManager.RequestGameTimeUnpause));

            var openingPrefix = PatchMethod(nameof(MultiplayerPausePatches.PauseMenuOnOpeningPrefix));
            var openingFinalizer = PatchMethod(nameof(MultiplayerPausePatches.PauseMenuOnOpeningFinalizer));
            var closingPrefix = PatchMethod(nameof(MultiplayerPausePatches.PauseMenuOnClosingPrefix));
            var closingFinalizer = PatchMethod(nameof(MultiplayerPausePatches.PauseMenuOnClosingFinalizer));
            var requestPausePrefix = PatchMethod(nameof(MultiplayerPausePatches.RequestPausePrefix));
            var requestUnpausePrefix = PatchMethod(nameof(MultiplayerPausePatches.RequestUnpausePrefix));
            var requestGameTimePausePrefix = PatchMethod(nameof(MultiplayerPausePatches.RequestGameTimePausePrefix));
            var requestGameTimeUnpausePrefix = PatchMethod(nameof(MultiplayerPausePatches.RequestGameTimeUnpausePrefix));

            if (onOpening == null || onClosing == null || requestPause == null || requestUnpause == null ||
                requestGameTimePause == null || requestGameTimeUnpause == null || openingPrefix == null ||
                openingFinalizer == null || closingPrefix == null || closingFinalizer == null ||
                requestPausePrefix == null || requestUnpausePrefix == null || requestGameTimePausePrefix == null ||
                requestGameTimeUnpausePrefix == null)
            {
                _mpPausePatchFailed = true;
                ModLog.Warning("[Pause] Multiplayer pause patch methods were not found");
                return;
            }

            MpPauseHarmony.Patch(onOpening, prefix: new HarmonyMethod(openingPrefix),
                finalizer: new HarmonyMethod(openingFinalizer));
            MpPauseHarmony.Patch(onClosing, prefix: new HarmonyMethod(closingPrefix),
                finalizer: new HarmonyMethod(closingFinalizer));
            MpPauseHarmony.Patch(requestPause, prefix: new HarmonyMethod(requestPausePrefix));
            MpPauseHarmony.Patch(requestUnpause, prefix: new HarmonyMethod(requestUnpausePrefix));
            MpPauseHarmony.Patch(requestGameTimePause, prefix: new HarmonyMethod(requestGameTimePausePrefix));
            MpPauseHarmony.Patch(requestGameTimeUnpause, prefix: new HarmonyMethod(requestGameTimeUnpausePrefix));

            _mpPausePatchInstalled = true;
            ModLog.Info("[Pause] Multiplayer pause patch installed (pause menu keeps world and audio running)");
        }
        catch (Exception ex)
        {
            _mpPausePatchFailed = true;
            ModLog.Warning($"[Pause] Failed to install multiplayer pause patch: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        try { MpPauseHarmony.UnpatchSelf(); }
        finally
        {
            _mpPausePatchInstalled = false;
            _mpPausePatchFailed = false;
            Suppression.Reset();
            _isMultiplayerConnected = () => false;
        }
    }

    private static MethodInfo PatchMethod(string name)
        => typeof(MultiplayerPausePatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

    private static class MultiplayerPausePatches
    {
        // Cairn.UI.Menu.OnOpening issues both native pause requests synchronously from
        // PauseMenu.OnOpening. Suppress only calls made inside that precise transition;
        // dialogue, loading, cutscene and solo pause requests remain untouched.
        internal static void PauseMenuOnOpeningPrefix()
        {
            Suppression.BeginOpening(_isMultiplayerConnected());
        }

        internal static Exception PauseMenuOnOpeningFinalizer(Exception __exception)
        {
            Suppression.EndOpening();
            return __exception;
        }

        internal static void PauseMenuOnClosingPrefix()
        {
            Suppression.BeginClosing();
        }

        internal static Exception PauseMenuOnClosingFinalizer(Exception __exception)
        {
            Suppression.EndClosing();
            return __exception;
        }

        internal static bool RequestPausePrefix()
        {
            if (!Suppression.SuppressPauseRequest()) return true;
            ModLog.Info("[Pause] Pause menu opened — keeping shared world and audio running (MP session)");
            return false;
        }

        internal static bool RequestGameTimePausePrefix() => !Suppression.SuppressGameTimePauseRequest();
        internal static bool RequestUnpausePrefix() => !Suppression.SuppressUnpauseRequest();
        internal static bool RequestGameTimeUnpausePrefix() => !Suppression.SuppressGameTimeUnpauseRequest();
    }
}
