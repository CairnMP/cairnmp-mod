using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// The rope Cairn draws from a climber to Climbot stays hidden. The rope object is left in place:
/// a partner rope still clones its segment prefab from it.
/// The logical tether is clipped to the harness only while this climber is the tied leader
/// and has not left at a staff. That clip is what lets a piton attach, and it is also
/// Cairn's paid-out stop. After the leader unclips there, the coil stays with the follower
/// and this harness comes off. Anyone else keeps it down, so Climbot does not belay
/// a climber who is not leading.
/// Do not force <see cref="RobotPawnController.ShouldDisplayRope"/> off, and do not patch the
/// remote climbot's own update. Either one calls into il2cpp while that climbot has no frame
/// and kills the runtime. The practice climber's climbot stays inactive. A real partner's rope
/// is hidden from our tick, after a climbot frame exists.
/// </summary>
internal static class ClimbotRopePatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.ClimbotRope");
    private static readonly HashSet<IntPtr> RendererPointers = new();
    private static bool _installed;
    private static bool _logged;
    private static bool _loggedLeaderRope;
    private static bool _loggedLeaderRelease;

    public static void Install()
    {
        if (_installed) return;
        try
        {
            var local = AccessTools.Method(typeof(RobotPawnController), nameof(RobotPawnController.LateUpdate))
                        ?? throw new MissingMethodException(nameof(RobotPawnController), nameof(RobotPawnController.LateUpdate));
            Harmony.Patch(local, postfix: new HarmonyMethod(typeof(ClimbotRopePatch), nameof(AfterLocalRobot)));
            var line = AccessTools.Method(typeof(LogicalRopeRenderer), nameof(LogicalRopeRenderer.LateUpdate))
                       ?? throw new MissingMethodException(nameof(LogicalRopeRenderer), nameof(LogicalRopeRenderer.LateUpdate));
            Harmony.Patch(line, postfix: new HarmonyMethod(typeof(ClimbotRopePatch), nameof(AfterRopeLine)));
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[Rope] Climbot rope hook unavailable: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        RendererPointers.Clear();
        _installed = false;
        _logged = false;
        _loggedLeaderRope = false;
        _loggedLeaderRelease = false;
    }

    private static void AfterLocalRobot(RobotPawnController __instance)
    {
        // A walking save restores Climbot off the belay inside ReadFromSavegame.
        // Taking the rope down on the frames before that removes the coroutine the
        // restore starts, and the loading screen stays on the dots it has shown.
        if (__instance == null || SaveStillLoading()) return;
        try
        {
            // The mesh stays hidden below. A piton needs the securing rope clipped to the
            // harness. Turning the rope object on is not that clip. Only the tied leader who
            // is still on the rope gets it. After they unclip at a staff, IsLocalLeader stays
            // true for the follower's chain, and this clip is what keeps Cairn's paid-out stop.
            if (TeamAnchorChain.KeepsClimbotCoil(TeamAnchorSession.IsLocalLeader, TeamAnchorSession.LeaderLeft))
            {
                _loggedLeaderRelease = false;
                if (!LeaderLifelineIsAttached(__instance))
                    __instance.SetAttachedToHarness(true);
                else if (!_loggedLeaderRope)
                {
                    _loggedLeaderRope = true;
                    ModLog.Info("[Rope] The leader's lifeline is clipped on.");
                }
            }
            else if (TeamAnchorSession.IsLocalLeader && TeamAnchorSession.LeaderLeft)
            {
                // The rope object stays. The follower's pitons are still on it. Only the
                // harness comes off, which is the clip Cairn's paid-out stop follows.
                if (LeaderLifelineIsAttached(__instance))
                {
                    __instance.SetAttachedToHarness(false);
                    if (!_loggedLeaderRelease)
                    {
                        _loggedLeaderRelease = true;
                        _loggedLeaderRope = false;
                        ModLog.Info("[Rope] The leader left the rope. The lifeline is off their harness.");
                    }
                }
            }
            else if (__instance.RopeDisplayed || __instance.HasSecuringRope || LeaderLifelineIsAttached(__instance))
            {
                if (LeaderLifelineIsAttached(__instance))
                    __instance.SetAttachedToHarness(false);
                __instance.SetRopeActive(false, false);
            }

            Suppress(__instance.GetRope());
            Suppress(__instance.ropeBetweenArmAndStock);
        }
        catch (Exception ex)
        {
            ModLog.SuppressedException("rope.climbot-hide", ex);
        }
    }

    private static bool SaveStillLoading()
    {
        try
        {
            var manager = GlobalGameManager.Instance;
            if (manager == null) return false;
            var loading = manager.loadingState;
            return loading == GlobalGameManager.LoadingState.Loading
                || loading == GlobalGameManager.LoadingState.PreLoaded
                || loading == GlobalGameManager.LoadingState.Loaded
                || loading == GlobalGameManager.LoadingState.GearingUp
                || loading == GlobalGameManager.LoadingState.Initialized
                || loading == GlobalGameManager.LoadingState.LoadingSave;
        }
        catch (Exception ex)
        {
            ModLog.SuppressedException("rope.climbot-save-load", ex);
            return false;
        }
    }

    private static bool LeaderLifelineIsAttached(RobotPawnController robot)
    {
        var harness = robot.AssociatedPawn != null ? robot.AssociatedPawn.harness : null;
        var rope = harness != null && harness.PersonalLifeline != null
            ? harness.PersonalLifeline.securingRope
            : null;
        if (rope == null || harness == null) return false;
        return rope.IsAttached(harness.Cast<IRopeHolder>());
    }

    /// <summary>
    /// Hides a real partner's climbot rope. Caller must already know this climbot has a frame.
    /// </summary>
    internal static void HideRemote(NetplayRemoteClimbot climbot)
    {
        if (climbot == null || climbot.Pointer == IntPtr.Zero) return;
        try
        {
            var rope = climbot.Rope;
            if (rope == null || rope.Pointer == IntPtr.Zero || !rope.IsInitialized) return;
            Suppress(rope);
        }
        catch (Exception ex) { ModLog.SuppressedException("rope.remote-climbot-hide", ex); }
    }

    private static void AfterRopeLine(LogicalRopeRenderer __instance)
    {
        if (__instance == null || !RendererPointers.Contains(__instance.Pointer)) return;
        try { HideLine(__instance); }
        catch (Exception ex) { ModLog.SuppressedException("rope.climbot-line", ex); }
    }

    private static void Suppress(LogicalRope rope)
    {
        if (rope == null || rope.Pointer == IntPtr.Zero || !rope.IsInitialized) return;
        // deactivateRopeOnHide stays false. Deactivating this object would take the segment prefab with it.
        if (rope.Visible)
            rope.SetVisible(false, false);
        rope.shouldUseRopeLineRendererLateUpdate = false;
        var renderer = rope.logicalRopeRenderer;
        if (renderer != null)
        {
            RendererPointers.Add(renderer.Pointer);
            HideLine(renderer);
        }

        var parts = rope.ropeParts;
        if (parts != null)
        {
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null) continue;
                part.Visible = false;
                var meshes = part.GetComponentsInChildren<Renderer>(true);
                if (meshes == null) continue;
                for (var mesh = 0; mesh < meshes.Length; mesh++)
                    if (meshes[mesh] != null) meshes[mesh].enabled = false;
            }
        }

        if (_logged) return;
        _logged = true;
        ModLog.Info("[Rope] Climbot rope stays off.");
    }

    private static void HideLine(LogicalRopeRenderer renderer)
    {
        renderer.shouldUseRopeLineRendererLateUpdate = false;
        var line = renderer.lineRenderer;
        if (line != null) line.enabled = false;
    }
}
