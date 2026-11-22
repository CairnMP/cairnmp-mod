using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Rope Team session wall pitons stay on the climb but are not written into the save.
/// Shares the <see cref="Piton.WriteToSavegame"/> target with the save-guard finalizer.
/// </summary>
internal static class RopeTeamPitonSaveOmitPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.RopeTeamPitonSaveOmit");
    private static bool _installed;
    private static Func<bool> _inSession = () => false;

    internal static void Bind(Func<bool> inSession)
        => _inSession = inSession ?? throw new ArgumentNullException(nameof(inSession));

    public static void Install()
    {
        if (_installed) return;
        try
        {
            var write = AccessTools.Method(typeof(Piton), nameof(Piton.WriteToSavegame))
                        ?? throw new MissingMethodException(nameof(Piton), nameof(Piton.WriteToSavegame));
            Harmony.Patch(write, prefix: new HarmonyMethod(typeof(RopeTeamPitonSaveOmitPatch), nameof(OmitSessionWallPiton)));
            _installed = true;
            ModLog.Info("[RopeTeam] Session wall pitons are omitted from saves.");
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[RopeTeam] Piton save-omit hook unavailable: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
    }

    private static bool OmitSessionWallPiton(Piton __instance)
    {
        if (!_inSession()) return true;
        if (__instance == null || __instance.Pointer == IntPtr.Zero) return true;
        if (!RopeInterop.IsTrackedSessionWallPiton(__instance.Pointer)) return true;
        return !RopeTeamSavePolicy.OmitFromSave(true, true);
    }
}
