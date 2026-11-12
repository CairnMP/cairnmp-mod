using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PartnerFallPenaltyTests
{
    [Fact]
    public void AFallOrOffBelayIsHangingAndALedgeIsNot()
    {
        Assert.Equal(PartnerPose.Hanging, PartnerFallPenalty.Classify(false, true, false, "Climbing"));
        Assert.Equal(PartnerPose.Hanging, PartnerFallPenalty.Classify(false, false, false, "ClimbingV2PawnControllerModuleSecuredFalling"));
        Assert.Equal(PartnerPose.Hanging, PartnerFallPenalty.Classify(false, false, true, "ClimbingV2PawnControllerModuleOffBelay"));
        Assert.Equal(PartnerPose.Climbing, PartnerFallPenalty.Classify(false, false, false, "ClimbingV2PawnControllerModuleReaching"));
        Assert.Equal(PartnerPose.Climbing, PartnerFallPenalty.Classify(false, false, false, "Mantling"));
        Assert.Equal(PartnerPose.Climbing, PartnerFallPenalty.Classify(false, false, false, "GetUpFromClimb"));
        Assert.Equal(PartnerPose.OnGround, PartnerFallPenalty.Classify(false, false, false, "LedgeWalking"));
        Assert.Equal(PartnerPose.OnGround, PartnerFallPenalty.Classify(false, false, false, "Idle"));
        Assert.Equal(PartnerPose.Hanging, PartnerFallPenalty.Classify(
            false, false, false, "ClimbingV2PawnControllerModuleRappelAscendRopeHangIdle"));
        Assert.Equal(PartnerPose.Hanging, PartnerFallPenalty.Classify(
            false, false, false, "ClimbingV2PawnControllerModuleRopeAscending"));
        Assert.Equal(PartnerPose.Hanging, PartnerFallPenalty.Classify(
            false, false, false, "ClimbingV2PawnControllerModuleAbseiling"));
        Assert.Equal(PartnerPose.Hanging, PartnerFallPenalty.Classify(
            false, false, false, "ClimbingV2PawnControllerModuleRappel"));
        Assert.Equal(PartnerPose.Climbing, PartnerFallPenalty.Classify(
            false, false, false, "ClimbingV2PawnControllerModuleHangIdle"));
        Assert.True(PartnerFallPenalty.IsUnsecuredFallModule("ClimbingV2PawnControllerModuleFalling"));
        Assert.False(PartnerFallPenalty.IsUnsecuredFallModule("ClimbingV2PawnControllerModuleSecuredFalling"));
        Assert.False(PartnerFallPenalty.IsUnsecuredFallModule(
            "ClimbingV2PawnControllerModuleRappelAscendRopeHangIdle"));
        Assert.Equal(PartnerPose.Down, PartnerFallPenalty.Classify(true, true, true, "Falling"));
        Assert.Equal(3f, PartnerFallPenalty.FullGripSeconds);
    }

    [Fact]
    public void TheLeaderDrainsOnlyWhileTheFollowerHangs()
    {
        Assert.True(Hold(localIsLeader: true, partner: PartnerPose.Hanging).Drain);
        Assert.False(Hold(localIsLeader: true, partner: PartnerPose.OnGround).Drain);
        Assert.False(Hold(localIsLeader: true, partner: PartnerPose.Climbing).Drain);
        Assert.False(Hold(localIsLeader: true, partner: PartnerPose.Down).Drain);

        var falling = PartnerFallPenalty.Tick(
            linked: true, leaderLeft: false, localIsLeader: true,
            selfFalling: true, selfDown: false,
            PartnerPose.Hanging);
        Assert.False(falling.Drain);

        var left = PartnerFallPenalty.Tick(
            linked: true, leaderLeft: true, localIsLeader: true,
            selfFalling: false, selfDown: false,
            PartnerPose.Hanging);
        Assert.False(left.Drain);
        Assert.False(Hold(localIsLeader: false, partner: PartnerPose.Hanging).Drain);

        var down = PartnerFallPenalty.Tick(
            linked: true, leaderLeft: false, localIsLeader: true,
            selfFalling: false, selfDown: true,
            PartnerPose.Hanging);
        Assert.False(down.Drain);
    }

    [Fact]
    public void ALeaderUntieWhileTheFollowerHangsDropsThemAndRefillsTheLeader()
    {
        var cut = PartnerFallPenalty.Untie(
            localIsLeader: true, localUntied: true, localHanging: false, partnerHanging: true);
        Assert.False(cut.DropLocal);
        Assert.True(cut.RefillGrip);
        Assert.False(cut.HoldStaff);

        var follower = PartnerFallPenalty.Untie(
            localIsLeader: false, localUntied: false, localHanging: true, partnerHanging: false);
        Assert.True(follower.DropLocal);
        Assert.False(follower.RefillGrip);
        Assert.False(follower.HoldStaff);
    }

    [Fact]
    public void AFollowerUntieWhileTheLeaderHangsDropsTheLeaderAndRefillsNobody()
    {
        var leader = PartnerFallPenalty.Untie(
            localIsLeader: true, localUntied: false, localHanging: true, partnerHanging: false);
        Assert.True(leader.DropLocal);
        Assert.False(leader.RefillGrip);
        Assert.False(leader.HoldStaff);
    }

    [Fact]
    public void AnUntieDropsWhoeverIsHangingAndRefillsOnlyTheLeadersCut()
    {
        var leaderCutsBoth = PartnerFallPenalty.Untie(
            localIsLeader: true, localUntied: true, localHanging: true, partnerHanging: true);
        Assert.True(leaderCutsBoth.DropLocal);
        Assert.True(leaderCutsBoth.RefillGrip);

        var followerCutsBoth = PartnerFallPenalty.Untie(
            localIsLeader: true, localUntied: false, localHanging: true, partnerHanging: true);
        Assert.True(followerCutsBoth.DropLocal);
        Assert.False(followerCutsBoth.RefillGrip);

        var onlySelf = PartnerFallPenalty.Untie(
            localIsLeader: true, localUntied: true, localHanging: true, partnerHanging: false);
        Assert.True(onlySelf.DropLocal);
        Assert.False(onlySelf.RefillGrip);
        Assert.False(onlySelf.HoldStaff);
    }

    [Fact]
    public void AnUntieWithNobodyHangingForcesNoFallAndKeepsTheStaff()
    {
        var calm = PartnerFallPenalty.Untie(
            localIsLeader: true, localUntied: true, localHanging: false, partnerHanging: false);
        Assert.False(calm.DropLocal);
        Assert.False(calm.RefillGrip);
        Assert.True(calm.HoldStaff);

        var follower = PartnerFallPenalty.Untie(
            localIsLeader: false, localUntied: true, localHanging: false, partnerHanging: false);
        Assert.False(follower.DropLocal);
        Assert.False(follower.RefillGrip);
        Assert.True(follower.HoldStaff);
    }

    [Fact]
    public void AnInAirHangCountsAsHangingAndAHoldStaysClimbing()
    {
        Assert.Equal(PartnerPose.Hanging, PartnerFallPenalty.Classify(
            false, false, false, "ClimbingV2PawnControllerModuleHangIdle", voidHang: true));
        Assert.Equal(PartnerPose.Climbing, PartnerFallPenalty.Classify(
            false, false, false, "ClimbingV2PawnControllerModuleHangIdle"));
    }

    private static PartnerHold Hold(bool localIsLeader, PartnerPose partner)
        => PartnerFallPenalty.Tick(
            linked: true, leaderLeft: false, localIsLeader,
            selfFalling: false, selfDown: false,
            partner);
}
