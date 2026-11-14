using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Bivouac;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// After a tie has been made, the rope Cairn draws from a climber to Climbot stays hidden
/// until camp. The rope object is left in place: a partner rope still clones its segment prefab from it.
/// The logical tether is clipped to the harness only while this climber is the tied leader
/// and has not left at a staff. The clip waits while this climber's own bot is still out,
/// and while a belay left open by a staff unclip is still busy. That clip is what lets a
/// piton attach, and it is also Cairn's paid-out stop. A clip made once the bot is ready
/// stays on if the belay later looks busy. After the leader unclips at a staff, the coil
/// stays with the follower, this harness comes off, and the bot is told to stop securing.
/// Anyone else keeps it down, including a climber who untied on the wall, so Climbot does
/// not belay a climber who is not leading. Before the first tie, and after camp, the rope
/// is Cairn's.
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
    private static bool _loggedFollowerRope;
    private static bool _loggedRecall;
    private static bool _loggedStop;
    private static bool _loggedBusyRelease;
    private static bool _whistleInFlight;
    private static bool _clippedWhileReady;
    private static int _recallAsks;
    private static float _recallAskedAt = -1f;

    private readonly struct ClimbotGate
    {
        internal ClimbotGate(bool whistleHold, bool busyHold, bool ready)
        {
            WhistleHold = whistleHold;
            BusyHold = busyHold;
            Ready = ready;
        }

        /// <summary>A whistle is already bringing the bot home. Leave the harness alone.</summary>
        internal bool WhistleHold { get; }

        /// <summary>The belay is still open. Take a stale clip off and do not pin a new one.</summary>
        internal bool BusyHold { get; }

        /// <summary>The bot is home. A leader clip made now may stay up through a later belay.</summary>
        internal bool Ready { get; }
    }

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
        _loggedFollowerRope = false;
        ForgetRecall();
    }

    /// <summary>
    /// The follower on the live chain stays clipped while a piton protects them.
    /// So does the climber a staff untie left on the old chain. A new follower
    /// does not inherit that catch.
    /// </summary>
    private static bool KeepsFollowerCatch()
        => TeamAnchorSession.KeepsLocalCatch;

    /// <summary>
    /// This climber's own bot is not home yet. The partner's bot is not this object.
    /// An inactive practice bot does not run here. A busy belay left over from a staff
    /// unclip is told to stop even between ties, so the next lead does not clip onto it.
    /// </summary>
    private static ClimbotGate LeaveRopeUntilClimbotIsHome(RobotPawnController robot)
    {
        var leads = TeamAnchorChain.KeepsClimbotCoil(TeamAnchorSession.IsLocalLeader, TeamAnchorSession.LeaderLeft);
        if (!leads)
            _clippedWhileReady = false;

        var reported = robot.GetClimbotStatus();
        var outThere = robot.WaitingForRecall || ClimbotPitonRecoveryPatch.OutToFetch;
        var ready = !outThere && reported == ClimbotStatus.Ready;
        var onTie = leads || TeamAnchorSession.IsLocalFollower;
        var keepsCatch = KeepsFollowerCatch();
        var stuckBusy = ClimbotRecall.IsStuckBelay(
            reported == ClimbotStatus.Busy, _clippedWhileReady, _whistleInFlight, keepsCatch);
        // A stop asked before this catch existed must not keep the rope off.
        // A whistle already in flight still finishes. It does not unpin the catch.
        if (keepsCatch && !_whistleInFlight)
        {
            _recallAsks = 0;
            _recallAskedAt = -1f;
            _loggedStop = false;
        }

        if (!onTie && !stuckBusy && !keepsCatch)
        {
            ForgetRecall();
            return new ClimbotGate(whistleHold: false, busyHold: false, ready: false);
        }

        var since = _recallAskedAt < 0f
            ? ClimbotRecall.RetryAfterSeconds
            : Time.time - _recallAskedAt;
        var step = ClimbotRecall.Decide(outThere, ready, _recallAsks, since, stuckBusy);
        _recallAsks = step.Asks;
        if (step.Asks == 0)
        {
            _recallAskedAt = -1f;
            _loggedRecall = false;
            _loggedStop = false;
            _whistleInFlight = false;
        }

        if (step.Call)
        {
            _recallAskedAt = Time.time;
            _whistleInFlight = true;
            robot.RecallClimbot();
            if (!_loggedRecall)
            {
                _loggedRecall = true;
                ModLog.Info("[Rope] Climbot is coming home.");
            }
        }
        else if (step.Stop)
        {
            _recallAskedAt = Time.time;
            _whistleInFlight = false;
            robot.OrderToStopSecuring();
            if (!_loggedStop)
            {
                _loggedStop = true;
                ModLog.Info("[Rope] Climbot was told to stop securing.");
            }
        }

        return new ClimbotGate(
            whistleHold: step.HoldRope && !stuckBusy && !keepsCatch,
            busyHold: step.HoldRope && stuckBusy,
            ready: ready);
    }

    private static void ForgetRecall()
    {
        _recallAsks = 0;
        _recallAskedAt = -1f;
        _loggedRecall = false;
        _loggedStop = false;
        _loggedBusyRelease = false;
        _whistleInFlight = false;
        _clippedWhileReady = false;
    }

    private static void AfterLocalRobot(RobotPawnController __instance)
    {
        // A walking save restores Climbot off the belay inside ReadFromSavegame.
        // Taking the rope down on the frames before that removes the coroutine the
        // restore starts, and the loading screen stays on the dots it has shown.
        // At camp the rope is Cairn's. Hiding it here would store a climber who walked off.
        if (__instance == null || SaveStillLoading() || BivouacSyncGate.AtCamp)
        {
            if (BivouacSyncGate.AtCamp) ForgetRecall();
            return;
        }
        if (!TeamAnchorSession.RulesArmed)
        {
            ForgetRecall();
            return;
        }
        try
        {
            // The mesh stays hidden below. A piton needs the securing rope clipped to the
            // harness. Turning the rope object on is not that clip. The tied leader who is
            // still on the rope gets it, and so does the follower while a piton protects
            // them. That follower clip is the catch on their anchor. After the leader
            // unclips at a staff, the coil comes off the leader and the bot is told to stop.
            // A bot that is still out is called home first. Pinning while it is out is what
            // stops the whistle, and it leaves the leader unable to place a piton. A busy
            // belay holds the same way, except a stale clip comes off until the bot is ready.
            // A whistle already bringing the bot home leaves the harness as it is,
            // except the follower's catch, which stays clipped.
            var gate = LeaveRopeUntilClimbotIsHome(__instance);
            var leads = TeamAnchorChain.KeepsClimbotCoil(
                TeamAnchorSession.IsLocalLeader, TeamAnchorSession.LeaderLeft);
            var leaderLeft = TeamAnchorSession.IsLocalLeader && TeamAnchorSession.LeaderLeft;
            var followerClip = TeamAnchorChain.FollowerKeepsSecuringClip(
                KeepsFollowerCatch(), KeepsFollowerCatch());
            switch (ClimbotRecall.ChooseHarness(
                gate.WhistleHold, gate.BusyHold, leads, leaderLeft, followerClip))
            {
                case ClimbotRecall.Harness.LeaderPin:
                    _loggedLeaderRelease = false;
                    _loggedFollowerRope = false;
                    if (gate.Ready)
                        _clippedWhileReady = true;
                    if (!LeaderLifelineIsAttached(__instance))
                        __instance.SetAttachedToHarness(true);
                    else if (!_loggedLeaderRope)
                    {
                        _loggedLeaderRope = true;
                        ModLog.Info("[Rope] The leader's lifeline is clipped on.");
                    }

                    break;
                case ClimbotRecall.Harness.FollowerPin:
                    // Pitons above the anchor leave this rope. The anchor stays, and the harness
                    // stays clipped, so a fall ends there. A whistle does not take this catch off.
                    _loggedLeaderRelease = false;
                    _loggedLeaderRope = false;
                    if (!LeaderLifelineIsAttached(__instance))
                        __instance.SetAttachedToHarness(true);
                    else if (!_loggedFollowerRope)
                    {
                        _loggedFollowerRope = true;
                        ModLog.Info("[Rope] The follower's lifeline stays clipped on.");
                    }

                    break;
                case ClimbotRecall.Harness.LeaderRelease:
                    _loggedLeaderRelease = false;
                    _loggedFollowerRope = false;
                    if (LeaderLifelineIsAttached(__instance))
                        ReleaseUntilReady(__instance);
                    break;
                case ClimbotRecall.Harness.LeaderLeftRelease:
                    // The rope object stays. The follower's pitons are still on it. Only the
                    // harness comes off, which is the clip Cairn's paid-out stop follows.
                    _loggedFollowerRope = false;
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

                    break;
                case ClimbotRecall.Harness.StaleRelease:
                    // A stale clip comes off until the bot is ready. The rope object stays.
                    _loggedFollowerRope = false;
                    if (LeaderLifelineIsAttached(__instance))
                        ReleaseUntilReady(__instance);
                    break;
                case ClimbotRecall.Harness.Hide:
                    if (__instance.RopeDisplayed || __instance.HasSecuringRope || LeaderLifelineIsAttached(__instance))
                    {
                        _loggedFollowerRope = false;
                        if (LeaderLifelineIsAttached(__instance))
                            __instance.SetAttachedToHarness(false);
                        __instance.SetRopeActive(false, false);
                    }

                    break;
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

    /// <summary>
    /// Gives the rope object back once, after this patch has been standing it down.
    /// Does not clip or unclip the harness. Cairn's own off-belay does that.
    /// </summary>
    internal static void HandBackToCairn()
    {
        if (SaveStillLoading()) return;
        try
        {
            RobotPawnController robot = null;
            var all = UnityEngine.Object.FindObjectsOfType<RobotPawnController>();
            if (all != null)
            {
                foreach (var candidate in all)
                {
                    if (candidate == null) continue;
                    robot = candidate;
                    break;
                }
            }

            if (robot == null) return;
            var rope = robot.GetRope();
            if (rope != null) rope.SetVisible(true, false);
            var coil = robot.ropeBetweenArmAndStock;
            if (coil != null) coil.SetVisible(true, false);
            robot.SetRopeActive(true, false);
        }
        catch (Exception ex)
        {
            ModLog.SuppressedException("rope.climbot-hand-back", ex);
        }
    }

    /// <summary>The clip went on while the bot was still busy. It comes off until the bot is ready.</summary>
    private static void ReleaseUntilReady(RobotPawnController robot)
    {
        robot.SetAttachedToHarness(false);
        if (_loggedBusyRelease) return;
        _loggedBusyRelease = true;
        _loggedLeaderRope = false;
        ModLog.Info("[Rope] The lifeline came off until Climbot is ready.");
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
        if (climbot == null || climbot.Pointer == IntPtr.Zero || SaveStillLoading()) return;
        try
        {
            var rope = climbot.Rope;
            if (rope == null || rope.Pointer == IntPtr.Zero || !rope.IsInitialized) return;
            Suppress(rope);
        }
        catch (Exception ex) { ModLog.SuppressedException("rope.remote-climbot-hide", ex); }
    }

    /// <summary>
    /// Puts a partner climbot rope back once the latch is clear. Does not destroy it.
    /// A partner rope still clones its segment prefab from this object.
    /// </summary>
    internal static void ShowRemote(NetplayRemoteClimbot climbot)
    {
        if (climbot == null || climbot.Pointer == IntPtr.Zero || SaveStillLoading()) return;
        try
        {
            var rope = climbot.Rope;
            if (rope == null || rope.Pointer == IntPtr.Zero || !rope.IsInitialized) return;
            Reveal(rope);
        }
        catch (Exception ex) { ModLog.SuppressedException("rope.remote-climbot-show", ex); }
    }

    private static void AfterRopeLine(LogicalRopeRenderer __instance)
    {
        if (__instance == null || SaveStillLoading() || BivouacSyncGate.AtCamp) return;
        if (!TeamAnchorSession.RulesArmed) return;
        if (!RendererPointers.Contains(__instance.Pointer)) return;
        try { HideLine(__instance); }
        catch (Exception ex) { ModLog.SuppressedException("rope.climbot-line", ex); }
    }

    private static void Reveal(LogicalRope rope)
    {
        if (rope == null || rope.Pointer == IntPtr.Zero || !rope.IsInitialized) return;
        rope.SetVisible(true, false);
        rope.shouldUseRopeLineRendererLateUpdate = true;
        var renderer = rope.logicalRopeRenderer;
        if (renderer != null)
        {
            RendererPointers.Remove(renderer.Pointer);
            renderer.shouldUseRopeLineRendererLateUpdate = true;
            var line = renderer.lineRenderer;
            if (line != null) line.enabled = true;
        }

        var parts = rope.ropeParts;
        if (parts == null) return;
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (part == null) continue;
            part.Visible = true;
            var meshes = part.GetComponentsInChildren<Renderer>(true);
            if (meshes == null) continue;
            for (var mesh = 0; mesh < meshes.Length; mesh++)
                if (meshes[mesh] != null) meshes[mesh].enabled = true;
        }
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
