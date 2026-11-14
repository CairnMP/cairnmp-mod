using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// A piton already on the follower's chain is not a new clip. Rest stays when the
/// rope already runs through it. These hooks are their own Harmony id, so a failed
/// install takes none of them and leaves piton placement alone.
/// </summary>
internal static class TeamChainClipPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.TeamChainClip");
    private static bool _installed;
    private static bool _loggedChainClip;

    public static void Install()
    {
        if (_installed) return;
        try
        {
            PatchProvider(typeof(PitonQuickdrawInteractionProvider), nameof(HideChainClip), nameof(SkipChainClip));
            PatchProvider(typeof(PitonOffBelayInteractionProvider), nameof(HideFreshClip), nameof(SkipFreshClip));
            var canOffBelay = AccessTools.Method(typeof(ClimbingV2PawnController), nameof(ClimbingV2PawnController.CanOffBelay))
                              ?? throw new MissingMethodException(nameof(ClimbingV2PawnController), nameof(ClimbingV2PawnController.CanOffBelay));
            Harmony.Patch(canOffBelay, postfix: new HarmonyMethod(typeof(TeamChainClipPatch), nameof(ClearFreshOffBelay)));
            var canOffBelayOn = AccessTools.Method(typeof(ClimbingV2PawnController), nameof(ClimbingV2PawnController.CanOffBelayOn))
                                ?? throw new MissingMethodException(nameof(ClimbingV2PawnController), nameof(ClimbingV2PawnController.CanOffBelayOn));
            Harmony.Patch(canOffBelayOn, postfix: new HarmonyMethod(typeof(TeamChainClipPatch), nameof(ClearFreshOffBelayOn)));
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            _installed = false;
            ModLog.Warning("[Rope] Chain-clip hooks unavailable: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
        _loggedChainClip = false;
    }

    private static void PatchProvider(Type provider, string available, string interact)
    {
        var isAvailable = AccessTools.Method(provider, "IsAvailable")
                          ?? throw new MissingMethodException(provider.Name, "IsAvailable");
        var interactMethod = AccessTools.Method(provider, "Interact")
                             ?? throw new MissingMethodException(provider.Name, "Interact");
        Harmony.Patch(isAvailable, postfix: new HarmonyMethod(typeof(TeamChainClipPatch), available));
        Harmony.Patch(interactMethod, prefix: new HarmonyMethod(typeof(TeamChainClipPatch), interact));
    }

    private static void HideChainClip(PitonQuickdrawInteractionProvider __instance, ref bool __result)
    {
        var piton = __instance != null ? __instance.piton : null;
        if (!KeepsCatch() || piton == null || !OnChain(piton))
            return;
        NoteChainClip();
        __result = false;
    }

    /// <summary>Skip the method. A postfix runs after the clip is already taken.</summary>
    private static bool SkipChainClip(PitonQuickdrawInteractionProvider __instance)
    {
        var piton = __instance != null ? __instance.piton : null;
        if (!KeepsCatch() || piton == null || !OnChain(piton))
            return true;
        NoteChainClip();
        return false;
    }

    private static void HideFreshClip(PitonOffBelayInteractionProvider __instance, ref bool __result)
    {
        var piton = __instance != null ? __instance.piton : null;
        if (!FreshClipBlocked(piton)) return;
        NoteChainClip();
        __result = false;
    }

    /// <summary>Skip the method. A postfix runs after the clip is already taken.</summary>
    private static bool SkipFreshClip(PitonOffBelayInteractionProvider __instance)
    {
        var piton = __instance != null ? __instance.piton : null;
        if (!FreshClipBlocked(piton)) return true;
        NoteChainClip();
        return false;
    }

    private static void ClearFreshOffBelay(ref bool __result, ref Piton closestPiton)
    {
        if (!__result || !FreshClipBlocked(closestPiton)) return;
        NoteChainClip();
        __result = false;
        closestPiton = null;
    }

    private static void ClearFreshOffBelayOn(Piton piton, ref bool __result)
    {
        if (!__result || !FreshClipBlocked(piton)) return;
        NoteChainClip();
        __result = false;
    }

    private static bool FreshClipBlocked(Piton piton)
    {
        if (piton == null || !KeepsCatch() || !OnChain(piton)) return false;
        if (!TryRopeRunsThrough(piton, out var through)) return false;
        return TeamAnchorChain.BlocksFreshClip(
            keepsCatch: true, through, RopeInterop.SecuringRopeIsOnHarness());
    }

    private static bool OnChain(Piton piton)
    {
        if (piton.Pointer == IntPtr.Zero) return false;
        if (TopAnchorSession.OwnsPointer(piton.Pointer)) return true;
        if (!RopeInterop.TryGetChainId(piton.Pointer, out var id, out var ownLocalId)) return false;
        return TeamAnchorSession.IsChainKey(id, ownLocalId) || TeamAnchorSession.Protects(id);
    }

    private static bool KeepsCatch()
        => TeamAnchorSession.KeepsLocalCatch;

    /// <summary>False when the piton cannot be read. The clip is then left to the game.</summary>
    private static bool TryRopeRunsThrough(Piton piton, out bool through)
    {
        through = false;
        try
        {
            if (piton.RopeAttached)
            {
                through = true;
                return true;
            }

            var quickdraw = piton.Quickdraw;
            if (quickdraw != null && quickdraw.Pointer != IntPtr.Zero)
                through = quickdraw.RopeAttached || quickdraw.HasRopeAttached();
            return true;
        }
        catch (Exception ex)
        {
            ModLog.SuppressedException("rope.chain-clip", ex);
            return false;
        }
    }

    private static void NoteChainClip()
    {
        if (_loggedChainClip) return;
        _loggedChainClip = true;
        ModLog.Info("[RopeTeam] A piton already on the chain is not a new clip.");
    }
}
