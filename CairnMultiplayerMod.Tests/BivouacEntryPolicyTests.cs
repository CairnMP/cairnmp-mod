using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Game.Bivouac;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BivouacEntryPolicyTests
{
    [Fact]
    public void OtherModesEnterWhileAPartnerIsAway()
    {
        Assert.True(BivouacEntryPolicy.MayEnter(
            ropeTeamSession: false, othersStillOut: 1, othersAtThisCamp: 0));
    }

    [Fact]
    public void ARopeTeamOfOneMayRest()
    {
        Assert.True(BivouacEntryPolicy.MayEnter(
            ropeTeamSession: true, othersStillOut: 0, othersAtThisCamp: 0));
    }

    [Fact]
    public void APartnerOnTheWallKeepsTheCampClosed()
    {
        Assert.True(BivouacEntryPolicy.IsStillOut(PlayerState.InGame));
        Assert.False(BivouacEntryPolicy.IsAtThisCamp(
            PlayerState.InGame, poseFresh: true, sameScene: true, inReach: false));
        Assert.False(BivouacEntryPolicy.MayEnter(
            ropeTeamSession: true, othersStillOut: 1, othersAtThisCamp: 0));
    }

    [Fact]
    public void APartnerAtThisCampOpensIt()
    {
        Assert.True(BivouacEntryPolicy.IsAtThisCamp(
            PlayerState.InGame, poseFresh: true, sameScene: true, inReach: true));
        Assert.True(BivouacEntryPolicy.MayEnter(
            ropeTeamSession: true, othersStillOut: 1, othersAtThisCamp: 1));
    }

    [Fact]
    public void APartnerAlreadyInsideDoesNotHoldTheDoor()
    {
        Assert.False(BivouacEntryPolicy.IsStillOut(PlayerState.Bivouac));
        Assert.True(BivouacEntryPolicy.MayEnter(
            ropeTeamSession: true, othersStillOut: 0, othersAtThisCamp: 0));
    }

    [Theory]
    [InlineData(PlayerState.Loading)]
    [InlineData(PlayerState.Connecting)]
    [InlineData(PlayerState.InMenu)]
    [InlineData(PlayerState.Unknown)]
    public void APartnerWhoIsNotInTheWorldYetKeepsTheCampClosed(PlayerState state)
    {
        Assert.True(BivouacEntryPolicy.IsStillOut(state));
        Assert.False(BivouacEntryPolicy.IsAtThisCamp(
            state, poseFresh: true, sameScene: true, inReach: true));
        Assert.False(BivouacEntryPolicy.MayEnter(
            ropeTeamSession: true, othersStillOut: 1, othersAtThisCamp: 0));
    }

    [Fact]
    public void OnePartnerStillOutsideKeepsTheCampClosed()
    {
        Assert.False(BivouacEntryPolicy.MayEnter(
            ropeTeamSession: true, othersStillOut: 2, othersAtThisCamp: 1));
    }

    [Fact]
    public void AStaleOrForeignPoseIsNotAtCamp()
    {
        Assert.False(BivouacEntryPolicy.PoseIsFresh(10, 12.01));
        Assert.True(BivouacEntryPolicy.PoseIsFresh(10, 12));
        Assert.False(BivouacEntryPolicy.PoseIsFresh(0, 12));
        Assert.False(BivouacEntryPolicy.PoseIsFresh(double.NaN, 12));
        Assert.False(BivouacEntryPolicy.PoseIsFresh(13, 12));
        Assert.False(BivouacEntryPolicy.IsAtThisCamp(
            PlayerState.InGame, poseFresh: false, sameScene: true, inReach: true));
        Assert.False(BivouacEntryPolicy.IsAtThisCamp(
            PlayerState.InGame, poseFresh: true, sameScene: false, inReach: true));
    }
}
