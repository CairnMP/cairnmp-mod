using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Life;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Networking;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal static partial class RopeInterop
{
    internal const string BraceMessageId = "rope.brace";
    private const string BraceText = "Bracing";
    private const float BraceRefreshSeconds = 0.35f;
    private const float LockSeconds = 0.25f;
    private const float StatusRepeatSeconds = 1f;
    private const float DrainStepCapSeconds = 0.25f;

    private static ushort _fallChannel;
    private static HudAdapter _fallHud;
    private static bool _bracing;
    private static bool _walkBlocked;
    private static bool _statusSent;
    private static PartnerPose _lastStatus;
    private static float _nextStatusAt;
    private static int _watchedPartner = int.MinValue;
    private static int _dropAfterFrame;
    private static int _refilledFrame = -1;

    internal static void BindFallChannel(ushort channel) => _fallChannel = channel;

    internal static void BindFallHud(HudAdapter hud) => _fallHud = hud;

    internal static void AcceptFallState(int fromPlayerId, byte[] payload)
    {
        if (payload == null || payload.Length < 1) return;
        var value = payload[0];
        if (value > (byte)PartnerPose.Down) return;
        if (!TeamAnchorSession.IsLinked || TeamAnchorSession.LeaderLeft) return;
        if (fromPlayerId != PartnerId()) return;
        PartnerFallSession.Remember((PartnerPose)value);
    }

    /// <summary>
    /// Called on the untie, before the chain decides whether a staff keeps it.
    /// Each client also runs this when the untie packet arrives, and drops its own pawn.
    /// </summary>
    internal static PartnerUntie PrepareUntie(int from, int target, bool localIsLeader, bool localUntied)
    {
        if (!TeamAnchorSession.IsLinked || !UntieOfThisRope(from, target))
            return new PartnerUntie(false, false, true);

        var local = ReadLocal();
        var result = PartnerFallPenalty.Untie(
            localIsLeader, localUntied, local.Hanging, PartnerFallSession.Partner == PartnerPose.Hanging);
        PartnerFallSession.ForceRelease = result.ForceRelease;
        if (result.RefillGrip) RefillGrip();
        if (result.DropLocal) ArmDrop();
        return result;
    }

    internal static void TickPartnerFall(NetworkManager network)
    {
        TryDrop();

        var linked = TeamAnchorSession.IsLinked && !TeamAnchorSession.LeaderLeft;
        if (!linked)
        {
            if (_watchedPartner != int.MinValue)
            {
                _watchedPartner = int.MinValue;
                _statusSent = false;
                PartnerFallSession.ForgetPartner();
                ApplyBrace(false);
            }

            return;
        }

        var partnerId = PartnerId();
        if (partnerId != _watchedPartner)
        {
            _watchedPartner = partnerId;
            _statusSent = false;
            PartnerFallSession.ForgetPartner();
        }

        var local = ReadLocal();
        Publish(network, local.Status);
        var hold = PartnerFallPenalty.Tick(
            linked: true,
            leaderLeft: false,
            TeamAnchorSession.IsLocalLeader,
            local.Falling,
            local.OffBelay,
            local.Down,
            PartnerFallSession.Partner,
            PartnerFallSession.BraceLatched);
        PartnerFallSession.BraceLatched = hold.Latched;
        ApplyBrace(hold.Brace);
        if (hold.Drain) DrainGrip(Time.unscaledDeltaTime);
    }

    internal static void ResetPartnerFall()
    {
        _watchedPartner = int.MinValue;
        _statusSent = false;
        _dropAfterFrame = 0;
        PartnerFallSession.Reset();
        ApplyBrace(false);
    }

    private static int PartnerId()
        => TeamAnchorSession.IsLocalLeader ? TeamAnchorSession.FollowerId : TeamAnchorSession.LeaderId;

    private static bool UntieOfThisRope(int from, int target)
        => (from == TeamAnchorSession.LeaderId && target == TeamAnchorSession.FollowerId)
           || (from == TeamAnchorSession.FollowerId && target == TeamAnchorSession.LeaderId);

    private static void Publish(NetworkManager network, PartnerPose pose)
    {
        if (network == null || _fallChannel == 0) return;
        var now = Time.unscaledTime;
        if (_statusSent && pose == _lastStatus && now < _nextStatusAt) return;
        _statusSent = true;
        _lastStatus = pose;
        _nextStatusAt = now + StatusRepeatSeconds;
        network.SendFeatureStream(_fallChannel, new[] { (byte)pose }, reliable: true);
    }

    private static void ArmDrop()
    {
        if (PartnerFallSession.DropPending) return;
        PartnerFallSession.ArmDrop();
        _dropAfterFrame = Time.frameCount;
    }

    private static void TryDrop()
    {
        if (!PartnerFallSession.DropPending || Time.frameCount <= _dropAfterFrame) return;
        if (TeamAnchorSession.HasPendingRelease) return;
        PartnerFallSession.CompleteDrop();
        DropHanging();
    }

    private static void DropHanging()
    {
        var pawn = ResolveLocalClimbController();
        if (pawn == null) return;
        try
        {
            if (pawn.IsFalling && !pawn.IsSecureFalling && !pawn.IsOffBelay) return;
            if (pawn.IsOffBelay && pawn.FallingModule != null)
                pawn.CurrentModule = pawn.FallingModule;
            pawn.ToFallingModuleUnsecure(FallReason.Unknown, default, null);
            ModLog.Info("[RopeTeam] The untie dropped a hanging climber.");
        }
        catch (Exception exception)
        {
            ModLog.Warning("[RopeTeam] Cairn refused the untie fall: " + exception.Message);
        }
    }

    private static void DrainGrip(float deltaSeconds)
    {
        if (deltaSeconds <= 0f) return;
        var portion = Math.Min(deltaSeconds, DrainStepCapSeconds) / PartnerFallPenalty.FullGripSeconds;
        var pawn = ResolveLocalClimbController();
        var limbs = pawn?.limbs;
        if (limbs == null) return;
        var ignore = IgnoresStamina(pawn);
        try
        {
            for (var index = 0; index < limbs.Length; index++)
            {
                var limb = limbs[index];
                if (limb == null) continue;
                var before = limb.StaminaRatio;
                limb.ConsumeBaseStaminaRatio(portion, 0f);
                if (ignore) limb.SetStaminaRatio(Math.Max(0f, before - portion));
            }
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.fall-drain", exception);
        }
    }

    private static void RefillGrip()
    {
        if (_refilledFrame == Time.frameCount) return;
        _refilledFrame = Time.frameCount;
        var limbs = ResolveLocalClimbController()?.limbs;
        if (limbs == null) return;
        try
        {
            for (var index = 0; index < limbs.Length; index++)
            {
                var limb = limbs[index];
                if (limb == null) continue;
                limb.SetStaminaRatio(1f);
            }

            ModLog.Info("[RopeTeam] The leader cut the rope and took a full grip back.");
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.fall-refill", exception);
        }
    }

    private static bool IgnoresStamina(ClimbingV2PawnController pawn)
    {
        try
        {
            return pawn?.CurrentModule != null && pawn.CurrentModule.IgnoreStamina;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.fall-ignore-stamina", exception);
            return false;
        }
    }

    private static void ApplyBrace(bool brace)
    {
        if (brace)
        {
            try
            {
                var pawns = PawnManager.Instance;
                if (pawns != null) pawns.lockClimberTimer = LockSeconds;
                if (PawnCaptureInterop.IsLocalPlayerWalking())
                {
                    var walk = pawns?.WalkingPawnController;
                    if (walk != null)
                    {
                        walk.IsLocomotionBlocked = true;
                        _walkBlocked = true;
                    }
                }
                else if (_walkBlocked)
                {
                    ReleaseWalk();
                }
            }
            catch (Exception exception)
            {
                ModLog.SuppressedException("rope.fall-brace", exception);
            }

            _fallHud?.ShowMessage(BraceMessageId, BraceText, BraceRefreshSeconds);
            _bracing = true;
            return;
        }

        if (!_bracing && !_walkBlocked) return;
        ReleaseWalk();
        _fallHud?.HideMessage(BraceMessageId);
        _bracing = false;
    }

    private static void ReleaseWalk()
    {
        if (!_walkBlocked) return;
        _walkBlocked = false;
        try
        {
            var walk = PawnManager.Instance?.WalkingPawnController;
            if (walk != null) walk.IsLocomotionBlocked = false;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.fall-walk", exception);
        }
    }

    private readonly struct LocalPose
    {
        internal LocalPose(PartnerPose status, bool falling, bool offBelay, bool down)
        {
            Status = status;
            Falling = falling;
            OffBelay = offBelay;
            Down = down;
        }

        internal PartnerPose Status { get; }
        internal bool Falling { get; }
        internal bool OffBelay { get; }
        internal bool Hanging => Status == PartnerPose.Hanging;
        internal bool Down { get; }
    }

    private static LocalPose ReadLocal()
    {
        var dead = PawnCaptureInterop.GetLocalPawnState() == NetFrame.PawnStateType.Dead
                   || LifeInterop.IsLocalPlayerDead();
        var pawn = ResolveLocalClimbController();
        if (pawn == null)
        {
            var state = PawnCaptureInterop.GetLocalPawnState();
            var falling = state == NetFrame.PawnStateType.Falling;
            var status = PartnerFallPenalty.Classify(dead, falling, offBelay: false, moduleName: null);
            if (state == NetFrame.PawnStateType.Climbing && status == PartnerPose.OnGround)
                status = PartnerPose.Climbing;
            return new LocalPose(status, falling, false, dead || status == PartnerPose.Down);
        }

        try
        {
            var module = ReadClimbModule();
            var moduleName = ModuleName(pawn);
            var offBelay = pawn.IsOffBelay
                           || (module == TopAnchorModule.Other
                               && moduleName.Contains("OffBelay", StringComparison.Ordinal));
            var falling = pawn.IsFalling || pawn.IsSecureFalling
                          || module is TopAnchorModule.Falling or TopAnchorModule.SecuredFalling;
            var status = PartnerFallPenalty.Classify(dead, falling, offBelay, moduleName);
            return new LocalPose(status, falling, offBelay, dead || status == PartnerPose.Down);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.fall-pose", exception);
            return new LocalPose(PartnerPose.OnGround, false, false, dead);
        }
    }

    private static string ModuleName(ClimbingV2PawnController pawn)
    {
        var module = pawn.CurrentModule;
        if (module == null) return "";
        return module.GetIl2CppType()?.Name ?? module.type.ToString() ?? "";
    }
}
