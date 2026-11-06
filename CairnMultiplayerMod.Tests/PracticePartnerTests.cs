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
    public void FlyMovesAlongTheLookAndShortensALongFrame()
    {
        Assert.True(PracticePartnerMotion.TryFly(
            0f, 0f, 0f, forwardX: 0f, forwardY: 0f, forwardZ: 1f, rightX: 1f, rightY: 0f, rightZ: 0f,
            strafe: 0f, ahead: 1f, vertical: 0f,
            speed: PracticePartnerMotion.FlySpeedMetersPerSecond, deltaSeconds: 0.5f,
            maxStep: PracticePartnerMotion.MaxStepMeters,
            out var x, out var y, out var z));
        Assert.Equal(0f, x);
        Assert.Equal(0f, y);
        Assert.Equal(4f, z);

        Assert.True(PracticePartnerMotion.TryFly(
            0f, 0f, 0f, 0f, 0f, 1f, 1f, 0f, 0f,
            strafe: 0f, ahead: 1f, vertical: 0f,
            speed: PracticePartnerMotion.FlySpeedMetersPerSecond, deltaSeconds: 1f,
            maxStep: PracticePartnerMotion.MaxStepMeters,
            out x, out y, out z));
        Assert.Equal(4f, z);

        Assert.True(PracticePartnerMotion.TryFly(
            1f, 2f, 3f, 0f, 0f, 1f, 1f, 0f, 0f,
            strafe: 0f, ahead: 0f, vertical: 0f,
            speed: PracticePartnerMotion.FlySpeedMetersPerSecond, deltaSeconds: 1f,
            maxStep: PracticePartnerMotion.MaxStepMeters,
            out x, out y, out z));
        Assert.Equal(1f, x);
        Assert.Equal(2f, y);
        Assert.Equal(3f, z);
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
    public void UnprotectedLineShowsOnlyWhileTiedWithNoLeaderAnchor()
    {
        Assert.Equal(LeaderProtectionMarker.UnprotectedText, LeaderProtectionMarker.StatusText(linked: true, hasLeaderAnchor: false));
        Assert.Null(LeaderProtectionMarker.StatusText(linked: true, hasLeaderAnchor: true));
        Assert.Null(LeaderProtectionMarker.StatusText(linked: false, hasLeaderAnchor: false));
    }

    [Fact]
    public void PracticeKeysStayOffTheRopeAndTakeKeys()
    {
        Assert.Equal("F9", PracticePartnerController.SpawnKeyName);
        Assert.Equal("H", PracticePartnerController.MoveKeyName);
        Assert.NotEqual(RopeCoupleController.ToggleKeyName, PracticePartnerController.SpawnKeyName);
        Assert.NotEqual(PitonRecoverySession.TakeKeyName, PracticePartnerController.MoveKeyName);
        Assert.Equal("Tap D-pad up — Partner takes piton", PitonRecoverySession.PartnerTakeText);
    }
}
