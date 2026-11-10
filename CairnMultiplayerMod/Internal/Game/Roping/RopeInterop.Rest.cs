using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Networking;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal static partial class RopeInterop
{
    private static readonly List<IntPtr> RestStaff = new();
    private static readonly List<IntPtr> RestReplicas = new();

    /// <summary>
    /// Ends the tie and drops gear that is not this climber's, so the world Cairn saves
    /// at the bivouac is this climber's own. The bag is left as Cairn sees it. The caller
    /// has already stood the climbot rope patch down and hands that rope back afterwards.
    /// </summary>
    internal static void RestForBivouac(NetworkManager network)
    {
        try
        {
            if (CutsceneStaffPolicy.RestWaits(CutsceneStaffSession.StoryHold, CutsceneStaffSession.TakeCampRest()))
            {
                ModLog.Info("[RopeTeam] The story rest left the staff for the follower.");
                return;
            }

            UntieForRest(network);
            ReleaseQueuedPitons();
            DestroyStaffNow(RestStaff);
            DestroyReplicas(RestStaff);
            ReleaseAllAnchors();
            HideHeldLine();
        }
        catch (Exception exception)
        {
            ModLog.Warning("[Save] Could not rest the rope team at the bivouac: " + exception.Message);
        }
    }

    private static void UntieForRest(NetworkManager network)
    {
        if (network != null)
        {
            var partner = RopeLinkState.PartnerOf(network.LocalPlayerId);
            if (partner != -1)
            {
                // Drop, not a hold at the staff. The partner's client clears the chain too.
                TopAnchorSession.RequestDrop();
                SendTopAnchorDrop(network);
                network.SendRopeClip(partner, false);
            }
        }

        RopeLinkState.Clear();
        if (TeamAnchorSession.PitchRemains)
            TeamAnchorSession.Clear();
        else
            TopAnchorSession.Reset();
    }

    private static void ReleaseQueuedPitons()
    {
        var lifeline = TryGetLifeline();
        if (lifeline == null) return;
        ReleaseUnhookedPitons(lifeline);
    }

    private static void DestroyStaffNow(List<IntPtr> destroyed)
    {
        destroyed.Clear();
        TopAnchorSession.DrainDestroy(_staffDestroy);
        foreach (var pointer in _staffDestroy)
        {
            destroyed.Add(pointer);
            TryDetachPitonViaLifeline(pointer);
            DestroyReleasedPiton(new Piton(pointer));
        }
    }

    private static void DestroyReplicas(List<IntPtr> alreadyDestroyed)
    {
        _pitons.CopyLeaving(RestReplicas);
        foreach (var pointer in RestReplicas)
        {
            if (alreadyDestroyed.Contains(pointer)) continue;
            TryDetachPitonViaLifeline(pointer);
            DestroyReleasedPiton(new Piton(pointer));
        }

        _pitons.ForgetReplicas();
    }
}
