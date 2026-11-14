using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// In Rope Team, once a tie has been made, only the leader who is still on the rope plants
/// pitons, with Cairn's own tool, so they land on the leader's securing rope in chain order.
/// Before that tie, and after camp clears it, Cairn decides. A wall untie does not.
/// Cairn also refuses anyone who is not <see cref="SharedRopeGamemode.IsLocalClientRopeHead"/>.
/// That flag stays false for our tie, so the leader was refused with the same reason as a follower.
/// </summary>
internal static class TeamPitonPlacePatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.TeamPitonPlace");
    private static bool _installed;
    private static bool _loggedOtherRefusal;
    private static bool _loggedChainClip;

    public static void Install()
    {
        if (_installed) return;
        try
        {
            var method = AccessTools.Method(
                typeof(ClimbingV2PawnControllerModulePitonExecution),
                nameof(ClimbingV2PawnControllerModulePitonExecution.CanAddPiton));
            if (method == null)
                throw new MissingMethodException(
                    nameof(ClimbingV2PawnControllerModulePitonExecution), nameof(ClimbingV2PawnControllerModulePitonExecution.CanAddPiton));
            Harmony.Patch(method, postfix: new HarmonyMethod(typeof(TeamPitonPlacePatch), nameof(ApplyLeaderRule)));

            var head = AccessTools.Method(typeof(SharedRopeGamemode), nameof(SharedRopeGamemode.IsLocalClientRopeHead))
                       ?? throw new MissingMethodException(nameof(SharedRopeGamemode), nameof(SharedRopeGamemode.IsLocalClientRopeHead));
            Harmony.Patch(head, prefix: new HarmonyMethod(typeof(TeamPitonPlacePatch), nameof(LeaderIsRopeHead)));

            PatchClip(typeof(PitonQuickdrawInteractionProvider), nameof(RefuseChainClip));
            PatchClip(typeof(PitonOffBelayInteractionProvider), nameof(RefuseFreshClip));
            var canOffBelay = AccessTools.Method(typeof(ClimbingV2PawnController), nameof(ClimbingV2PawnController.CanOffBelay))
                              ?? throw new MissingMethodException(nameof(ClimbingV2PawnController), nameof(ClimbingV2PawnController.CanOffBelay));
            Harmony.Patch(canOffBelay, postfix: new HarmonyMethod(typeof(TeamPitonPlacePatch), nameof(ClearFreshOffBelay)));
            var canOffBelayOn = AccessTools.Method(typeof(ClimbingV2PawnController), nameof(ClimbingV2PawnController.CanOffBelayOn))
                                ?? throw new MissingMethodException(nameof(ClimbingV2PawnController), nameof(ClimbingV2PawnController.CanOffBelayOn));
            Harmony.Patch(canOffBelayOn, postfix: new HarmonyMethod(typeof(TeamPitonPlacePatch), nameof(ClearFreshOffBelayOn)));
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[RopeTeam] Piton placement hook unavailable: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
        _loggedOtherRefusal = false;
        _loggedChainClip = false;
    }

    /// <summary>Cairn asks this before it will arm a piton. Our tied leader is that climber.</summary>
    private static bool LeaderIsRopeHead(ref bool __result)
    {
        if (!TeamAnchorSession.IsLocalLeader || TeamAnchorSession.LeaderLeft) return true;
        __result = true;
        return false;
    }

    private static void ApplyLeaderRule(
        ref bool __result,
        ref ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason reason)
    {
        if (!TeamAnchorSession.AllowsPlacement)
        {
            __result = false;
            reason = ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason.NetplayNotRopeHead;
            return;
        }

        if (__result) return;
        if (reason == ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason.NetplayNotRopeHead)
        {
            __result = true;
            reason = ClimbingV2PawnControllerModulePitonExecution.PitonUnusabilityReason.Usable;
            return;
        }

        if (_loggedOtherRefusal) return;
        _loggedOtherRefusal = true;
        ModLog.Info("[Piton] The leader was refused a piton: " + reason);
    }

    private static void PatchClip(Type provider, string prefix)
    {
        var available = AccessTools.Method(provider, "IsAvailable")
                        ?? throw new MissingMethodException(provider.Name, "IsAvailable");
        var interact = AccessTools.Method(provider, "Interact")
                       ?? throw new MissingMethodException(provider.Name, "Interact");
        Harmony.Patch(available, prefix: new HarmonyMethod(typeof(TeamPitonPlacePatch), prefix));
        Harmony.Patch(interact, prefix: new HarmonyMethod(typeof(TeamPitonPlacePatch), prefix));
    }

    /// <summary>The rope is already through this chain piton. It is not a new clip.</summary>
    private static bool RefuseChainClip(PitonQuickdrawInteractionProvider __instance, ref bool __result)
    {
        var piton = __instance != null ? __instance.piton : null;
        if (piton == null || !OnChain(piton) || !TeamAnchorChain.BlocksChainClip(KeepsCatch(), onProtectionChain: true))
            return true;
        NoteChainClip();
        __result = false;
        return false;
    }

    /// <summary>
    /// Rest on a piton the rope already runs through stays. A clip onto chain gear
    /// that the harness is not already on does not.
    /// </summary>
    private static bool RefuseFreshClip(PitonOffBelayInteractionProvider __instance, ref bool __result)
    {
        var piton = __instance != null ? __instance.piton : null;
        if (!FreshClipBlocked(piton)) return true;
        NoteChainClip();
        __result = false;
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
        if (piton == null || !OnChain(piton)) return false;
        if (!TryRopeRunsThrough(piton, out var through)) return false;
        return TeamAnchorChain.BlocksFreshClip(
            KeepsCatch(), onProtectionChain: true, through, RopeInterop.SecuringRopeIsOnHarness());
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
