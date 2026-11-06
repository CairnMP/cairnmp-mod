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
/// The logical tether is clipped to the harness only while this climber is the tied leader,
/// which is what lets a piton attach. Anyone else keeps it down, so Climbot does not belay
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
    }

    private static void AfterLocalRobot(RobotPawnController __instance)
    {
        if (__instance == null) return;
        try
        {
            // The mesh stays hidden below. A piton needs the securing rope clipped to the
            // harness. Turning the rope object on is not that clip. Only the tied leader gets it.
            // Everyone else stays unclipped, so Climbot does not belay them.
            if (TeamAnchorSession.IsLocalLeader)
            {
                if (!LeaderLifelineIsAttached(__instance))
                    __instance.SetAttachedToHarness(true);
                else if (!_loggedLeaderRope)
                {
                    _loggedLeaderRope = true;
                    ModLog.Info("[Rope] The leader's lifeline is clipped on.");
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
