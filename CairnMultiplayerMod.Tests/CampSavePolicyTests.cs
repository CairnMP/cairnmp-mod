using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class CampSavePolicyTests
{
    [Fact]
    public void SoloAndOtherModesSaveWhereverCairnAllows()
    {
        Assert.True(CampSavePolicy.MayWrite(ropeTeamSession: false, atCamp: false));
        Assert.True(CampSavePolicy.MayWrite(ropeTeamSession: false, atCamp: true));
        Assert.True(CampSavePolicy.MayLoad(ropeTeamSession: false, fileIsInBivouac: false));
        Assert.True(CampSavePolicy.MayLoad(ropeTeamSession: false, fileIsInBivouac: true));
    }

    [Fact]
    public void RopeTeamWritesOnlyAtCamp()
    {
        Assert.False(CampSavePolicy.MayWrite(ropeTeamSession: true, atCamp: false));
        Assert.True(CampSavePolicy.MayWrite(ropeTeamSession: true, atCamp: true));
    }

    [Fact]
    public void RopeTeamLoadsOnlyACampFile()
    {
        Assert.False(CampSavePolicy.MayLoad(ropeTeamSession: true, fileIsInBivouac: false));
        Assert.True(CampSavePolicy.MayLoad(ropeTeamSession: true, fileIsInBivouac: true));
    }

    [Fact]
    public void OnlyARopeTeamCampIsRewritten()
    {
        Assert.False(CampSavePolicy.IsCampFile(ropeTeamSession: false, atCamp: true));
        Assert.False(CampSavePolicy.IsCampFile(ropeTeamSession: true, atCamp: false));
        Assert.True(CampSavePolicy.IsCampFile(ropeTeamSession: true, atCamp: true));
    }

    [Fact]
    public void ACampWriteSkipsPitonsReplicasAndTheStaff()
    {
        Assert.False(CampSavePackage.WritePiton(campWrite: true, replica: false, plantedStaff: false));
        Assert.False(CampSavePackage.WritePiton(campWrite: false, replica: true, plantedStaff: false));
        Assert.False(CampSavePackage.WritePiton(campWrite: false, replica: false, plantedStaff: true));
        Assert.True(CampSavePackage.WritePiton(campWrite: false, replica: false, plantedStaff: false));
    }

    [Fact]
    public void CampBelayIsNotOrderedOff()
    {
        var stopped = true;
        byte streak = 4;
        CampSavePackage.KeepCampBelay(ref stopped, ref streak);
        Assert.False(stopped);
        Assert.Equal(0, streak);
    }

    [Fact]
    public void AStuckBivouacFlagOnTheWallIsNotCamp()
    {
        Assert.False(CampSavePolicy.AtCamp(
            sceneIsBivouac: false, stuckFlagOverridden: true, lifecycleSaysBivouac: true));
        Assert.True(CampSavePolicy.AtCamp(
            sceneIsBivouac: true, stuckFlagOverridden: true, lifecycleSaysBivouac: false));
        Assert.True(CampSavePolicy.AtCamp(
            sceneIsBivouac: false, stuckFlagOverridden: false, lifecycleSaysBivouac: true));
        Assert.False(CampSavePolicy.AtCamp(
            sceneIsBivouac: false, stuckFlagOverridden: false, lifecycleSaysBivouac: false));
    }
}
