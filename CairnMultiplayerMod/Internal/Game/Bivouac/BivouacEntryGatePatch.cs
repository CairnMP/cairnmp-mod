using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game;
using CairnMultiplayerMod.Internal.Game.Roping;
using CairnMultiplayerMod.Internal.Networking;
using HarmonyLib;
using Il2CppTheGameBakers.Cairn;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Bivouac;

/// <summary>
/// Rope Team does not start a bivouac from the rest interaction until the other
/// climber is at that camp. The save on the same object still runs. Pass-out and
/// story entry call <c>BivouacManager.EnterBivouac</c> directly and are not patched.
/// </summary>
internal static class BivouacEntryGatePatch
{
    internal const string MessageId = "bivouac.wait-partner";
    internal const string WaitText = "Wait for your partner.";
    private const float NoticeSeconds = 4f;

    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.BivouacEntry");
    private static bool _installed;
    private static HudAdapter _hud;
    private static Func<NetworkManager> _network;
    private static Func<string> _localScene;

    internal static void Bind(HudAdapter hud, Func<NetworkManager> network, Func<string> localScene)
    {
        _hud = hud;
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _localScene = localScene ?? throw new ArgumentNullException(nameof(localScene));
    }

    public static void Install()
    {
        if (_installed) return;
        try
        {
            PatchEnter(typeof(BivouacInteractionProvider));
            PatchEnter(typeof(BivouacGroundInteractionProvider));
            PatchEnter(typeof(BivouacWallInteractionProvider));
            _installed = true;
            ModLog.Info("[Bivouac] Rope Team waits for both climbers before resting.");
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            _installed = false;
            ModLog.Warning("[Bivouac] Could not hold the bivouac for both climbers: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
        _hud?.HideMessage(MessageId);
    }

    private static void PatchEnter(Type type)
    {
        var method = AccessTools.Method(type, nameof(BivouacInteractionProvider.Interact_Bivouac));
        if (method == null || method.DeclaringType != type)
            throw new MissingMethodException(type.Name, nameof(BivouacInteractionProvider.Interact_Bivouac));
        Harmony.Patch(method, prefix: new HarmonyMethod(typeof(BivouacEntryGatePatch), nameof(BlockUntilPartnerArrives)));
    }

    private static bool BlockUntilPartnerArrives(BivouacInteractionProvider __instance)
    {
        if (__instance == null || !BivouacSaveSession.RopeTeamSession || PartnersAreHere(__instance))
        {
            _hud?.HideMessage(MessageId);
            RememberCamp(__instance);
            return true;
        }

        _hud?.ShowMessage(MessageId, WaitText, NoticeSeconds);
        return false;
    }

    private static bool PartnersAreHere(BivouacInteractionProvider camp)
    {
        var network = _network?.Invoke();
        if (network == null) return true;

        var stillOut = 0;
        var atCamp = 0;
        var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        foreach (var pair in network.RemotePlayers)
        {
            var player = pair.Value;
            if (player == null || player.Id == network.LocalPlayerId || PracticePartner.Is(player.Id))
                continue;
            if (!BivouacEntryPolicy.IsStillOut(player.State)) continue;

            stillOut++;
            var fresh = BivouacEntryPolicy.PoseIsFresh(player.LastUpdateTime, now);
            var sameScene = SameScene(player.SceneName);
            var inReach = fresh && sameScene && InReach(camp, player.X, player.Y, player.Z);
            if (BivouacEntryPolicy.IsAtThisCamp(player.State, fresh, sameScene, inReach))
                atCamp++;
        }

        return BivouacEntryPolicy.MayEnter(ropeTeamSession: true, stillOut, atCamp);
    }

    private static bool SameScene(string partnerScene)
    {
        var local = _localScene?.Invoke();
        return !string.IsNullOrEmpty(local)
               && !string.IsNullOrEmpty(partnerScene)
               && string.Equals(local, partnerScene, StringComparison.Ordinal);
    }

    /// <summary>
    /// The camp's own colliders are the reach volume. A position query that runs
    /// while the local climber is already in reach cannot be told from a check of
    /// that climber, so it is only the fallback when the volume is missing.
    /// </summary>
    private static bool InReach(BivouacInteractionProvider camp, float x, float y, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
        try
        {
            return BivouacVolume.Contains(camp, new Vector3(x, y, z));
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("bivouac.entry-reach", exception);
            return false;
        }
    }

    /// <summary>The mountain marker of the camp being entered. A failed read must not block the rest.</summary>
    private static void RememberCamp(BivouacInteractionProvider camp)
    {
        if (camp == null) return;
        try { BivouacVolume.Remember(camp.transform.position); }
        catch (Exception exception) { ModLog.SuppressedException("bivouac.remember-camp", exception); }
    }
}
