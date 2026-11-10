using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Game.Roping;
using CairnMultiplayerMod.Internal.Networking;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PracticePartnerTests
{
    [Fact]
    public void LinkChoicePrefersARealClimberInsideRange()
    {
        var candidates = new List<PracticePartner.LinkCandidate>
        {
            new(PracticePartner.PlayerId, 0.2f, practice: true),
            new(4, 0.9f, practice: false),
            new(9, 3f, practice: false),
        };

        Assert.Equal(4, PracticePartner.ChooseLinkTarget(candidates, rangeMeters: 1f));
    }

    [Fact]
    public void LinkChoiceUsesThePracticeClimberWhenHeIsTheOnlyOneInRange()
    {
        var candidates = new List<PracticePartner.LinkCandidate>
        {
            new(PracticePartner.PlayerId, 1f, practice: true),
            new(4, 1.01f, practice: false),
        };

        Assert.Equal(PracticePartner.PlayerId, PracticePartner.ChooseLinkTarget(candidates, rangeMeters: 1f));
        Assert.Equal(-1, PracticePartner.ChooseLinkTarget(
            new[] { new PracticePartner.LinkCandidate(PracticePartner.PlayerId, 1.01f, practice: true) },
            rangeMeters: 1f));
    }

    [Fact]
    public void PoseStepStaysFiniteAndNoLongerThanFourMeters()
    {
        Assert.True(PracticePartnerMotion.TryStep(0f, 0f, 0f, 10f, 0f, 0f, PracticePartnerMotion.MaxStepMeters,
            out var x, out var y, out var z));
        Assert.Equal(4f, x);
        Assert.Equal(0f, y);
        Assert.Equal(0f, z);

        Assert.True(PracticePartnerMotion.TryStep(0f, 1f, 0f, 2f, 1f, 0f, PracticePartnerMotion.MaxStepMeters,
            out x, out y, out z));
        Assert.Equal(2f, x);
        Assert.Equal(1f, y);

        Assert.False(PracticePartnerMotion.TryStep(0f, 0f, 0f, float.NaN, 0f, 0f, PracticePartnerMotion.MaxStepMeters,
            out x, out y, out z));
        Assert.Equal(0f, x);
    }

    [Fact]
    public void ASwitchTradesTheSpotsAndTheJobAndTheNextSwitchGivesThemBack()
    {
        var you = new PracticeSpot(1f, 2f, 3f, yaw: 10f, climbing: true);
        var them = new PracticeSpot(4f, 5f, 6f, yaw: 20f, climbing: false);

        var once = PracticeSwap.Exchange(you, them, tied: true, youLead: true);

        Assert.Equal(them.X, once.You.X);
        Assert.Equal(them.Y, once.You.Y);
        Assert.Equal(them.Z, once.You.Z);
        Assert.Equal(them.Yaw, once.You.Yaw);
        Assert.False(once.You.Climbing);
        Assert.Equal(you.X, once.Frozen.X);
        Assert.Equal(you.Y, once.Frozen.Y);
        Assert.Equal(you.Z, once.Frozen.Z);
        Assert.True(once.Frozen.Climbing);
        Assert.False(once.YouLead);
        Assert.Equal(PracticeSwap.FollowingText, PracticeSwap.StatusText(swapped: true, tied: true, once.YouLead));

        var twice = PracticeSwap.Exchange(once.You, once.Frozen, tied: true, once.YouLead);

        Assert.Equal(you.X, twice.You.X);
        Assert.Equal(you.Y, twice.You.Y);
        Assert.Equal(you.Z, twice.You.Z);
        Assert.Equal(you.Yaw, twice.You.Yaw);
        Assert.True(twice.You.Climbing);
        Assert.Equal(them.X, twice.Frozen.X);
        Assert.True(twice.YouLead);
        Assert.Null(PracticeSwap.StatusText(swapped: false, tied: true, twice.YouLead));
    }

    [Fact]
    public void AnUntiedSwitchTradesTheSpotsAndLeavesTheJob()
    {
        var you = new PracticeSpot(1f, 2f, 3f, yaw: 0f, climbing: false);
        var them = new PracticeSpot(8f, 9f, 10f, yaw: 90f, climbing: true);

        var once = PracticeSwap.Exchange(you, them, tied: false, youLead: true);

        Assert.Equal(them.X, once.You.X);
        Assert.Equal(you.X, once.Frozen.X);
        Assert.True(once.YouLead);
        Assert.Equal(PracticeSwap.UntiedText, PracticeSwap.StatusText(swapped: true, tied: false, once.YouLead));
        Assert.Equal(PracticeSwap.LeadingText, PracticeSwap.StatusText(swapped: true, tied: true, youLead: true));
    }

    [Fact]
    public void LeavingTheWallForTheGroundSeatsTheWalk()
    {
        var place = PracticeSwap.ChoosePlace(destinationClimbing: false, currentlyClimbing: true, spotNeedsHover: false);

        Assert.Equal(PracticeSwap.Arrival.Walk, place.Arrival);
        Assert.True(place.LeaveClimb);
        Assert.True(place.SeatWalkingBody);
    }

    [Fact]
    public void AStandingSwitchSeatsTheWalkWithoutTheClimbExit()
    {
        var place = PracticeSwap.ChoosePlace(destinationClimbing: false, currentlyClimbing: false, spotNeedsHover: true);

        Assert.Equal(PracticeSwap.Arrival.Walk, place.Arrival);
        Assert.False(place.LeaveClimb);
        Assert.True(place.SeatWalkingBody);
    }

    [Fact]
    public void AWallSpotWithNothingUnderItHovers()
    {
        var place = PracticeSwap.ChoosePlace(destinationClimbing: true, currentlyClimbing: true, spotNeedsHover: true);

        Assert.Equal(PracticeSwap.Arrival.Hover, place.Arrival);
        Assert.False(place.LeaveClimb);
        Assert.False(place.SeatWalkingBody);
    }

    [Fact]
    public void SwitchingBackOntoAClimbUsesTheStoredHolds()
    {
        Assert.True(PracticeSwap.UsesStoredClimb(destinationClimbing: true, hasStoredClimb: true));
        Assert.False(PracticeSwap.UsesStoredClimb(destinationClimbing: true, hasStoredClimb: false));
        Assert.False(PracticeSwap.UsesStoredClimb(destinationClimbing: false, hasStoredClimb: true));
    }

    [Fact]
    public void AWallSpotWithAGripStaysAClimb()
    {
        var place = PracticeSwap.ChoosePlace(destinationClimbing: true, currentlyClimbing: false, spotNeedsHover: false);

        Assert.Equal(PracticeSwap.Arrival.Climb, place.Arrival);
        Assert.False(place.LeaveClimb);
        Assert.False(place.SeatWalkingBody);
    }

    [Fact]
    public void ASwitchDoesNothingWhilePausedFallingOrDead()
    {
        Assert.Equal(PracticeSwap.Block.Paused, PracticeSwap.Refusal(paused: true, falling: false, dead: false, walkingOrClimbing: true));
        Assert.Equal(PracticeSwap.Block.Dead, PracticeSwap.Refusal(paused: false, falling: true, dead: true, walkingOrClimbing: true));
        Assert.Equal(PracticeSwap.Block.Falling, PracticeSwap.Refusal(paused: false, falling: true, dead: false, walkingOrClimbing: false));
        Assert.Equal(PracticeSwap.Block.Unready, PracticeSwap.Refusal(paused: false, falling: false, dead: false, walkingOrClimbing: false));
        Assert.Equal(PracticeSwap.Block.None, PracticeSwap.Refusal(paused: false, falling: false, dead: false, walkingOrClimbing: true));
    }

    [Fact]
    public void SpawnStandsHalfAMeterToTheRight()
    {
        Assert.True(PracticePartnerMotion.TryBeside(1f, 2f, 3f, yawDegrees: 0f, PracticePartnerMotion.SpawnSideMeters,
            out var x, out var y, out var z));
        Assert.Equal(1.5f, x, precision: 3);
        Assert.Equal(2f, y);
        Assert.Equal(3f, z, precision: 3);
    }

    [Fact]
    public void PracticeFollowerTakeUsesHisDistanceAndNotTheLeaders()
    {
        Assert.True(PracticePitonTake.OffersChainPiton(
            linked: true, localIsLeader: true, PracticePartner.PlayerId, onChain: true,
            distanceMeters: PitonRecoveryPolicy.ReachMeters));
        Assert.False(PracticePitonTake.OffersChainPiton(
            linked: true, localIsLeader: true, PracticePartner.PlayerId, onChain: true,
            distanceMeters: PitonRecoveryPolicy.ReachMeters + 0.01f));
        Assert.False(PracticePitonTake.OffersChainPiton(
            linked: true, localIsLeader: true, followerId: 4, onChain: true, distanceMeters: 0.1f));
        Assert.False(PracticePitonTake.OffersChainPiton(
            linked: true, localIsLeader: false, PracticePartner.PlayerId, onChain: true, distanceMeters: 0.1f));
    }

    [Fact]
    public void PublicPlayerListOmitsThePracticeClimber()
    {
        Assert.False(PracticePartner.IsPublicRemote(PracticePartner.PlayerId));
        Assert.True(PracticePartner.IsPublicRemote(4));
    }

    [Fact]
    public void PracticeKeysStayOffTheRopeAndTakeKeys()
    {
        Assert.Equal("F9", PracticePartnerController.SpawnKeyName);
        Assert.Equal("H", PracticePartnerController.MoveKeyName);
        Assert.NotEqual(RopeCoupleController.ToggleKeyName, PracticePartnerController.SpawnKeyName);
        Assert.NotEqual(PitonRecoverySession.TakeKeyName, PracticePartnerController.MoveKeyName);
        Assert.Equal("Partner takes piton", PitonRecoverySession.PartnerTakeText);
        Assert.Equal("Tap", PitonRecoverySession.TakeCue);
    }
}
