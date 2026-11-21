using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class RopeTeamSavePolicyTests
{
    [Fact]
    public void SessionWallPitonsAreOmittedFromTheSave()
    {
        Assert.True(RopeTeamSavePolicy.OmitFromSave(
            ropeTeamSessionActive: true, isTrackedSessionWallPiton: true));
    }

    [Fact]
    public void UntrackedWorldPitonsStillSave()
    {
        Assert.False(RopeTeamSavePolicy.OmitFromSave(
            ropeTeamSessionActive: true, isTrackedSessionWallPiton: false));
    }

    [Fact]
    public void OutsideARopeTeamSessionNothingIsOmitted()
    {
        Assert.False(RopeTeamSavePolicy.OmitFromSave(
            ropeTeamSessionActive: false, isTrackedSessionWallPiton: true));
        Assert.False(RopeTeamSavePolicy.OmitFromSave(
            ropeTeamSessionActive: false, isTrackedSessionWallPiton: false));
    }
}
