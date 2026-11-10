using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BivouacSavePolicyTests
{
    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void RopeTeamWritesOnlyAfterTheRestAtCamp(
        bool ropeTeam, bool atCamp, bool restComplete, bool mayWrite)
    {
        Assert.Equal(mayWrite, BivouacSavePolicy.MayWrite(ropeTeam, atCamp, restComplete));
    }

    [Theory]
    [InlineData(false, false, 30f, true)]
    [InlineData(false, true, 30f, true)]
    [InlineData(true, false, 30f, false)]
    [InlineData(true, true, 300f, true)]
    public void RopeTeamLoadsOnlyABivouacFile(
        bool ropeTeam, bool fileIsInBivouac, float lastBivouacAge, bool mayLoad)
    {
        Assert.Equal(mayLoad, BivouacSavePolicy.MayLoad(ropeTeam, fileIsInBivouac, lastBivouacAge));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.024291992f)]
    [InlineData(0.09603882f)]
    [InlineData(0.12030029f)]
    [InlineData(0.999f)]
    public void CairnsExitWriteLoadsEvenThoughTheFlagIsClear(float lastBivouacAge)
    {
        Assert.True(BivouacSavePolicy.MayLoad(
            ropeTeamSession: true, fileIsInBivouac: false, lastBivouacAge));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(30f)]
    [InlineData(300f)]
    [InlineData(-0.1f)]
    public void ASaveMadeAfterLeavingDoesNotLoad(float lastBivouacAge)
    {
        Assert.False(BivouacSavePolicy.MayLoad(
            ropeTeamSession: true, fileIsInBivouac: false, lastBivouacAge));
        Assert.True(BivouacSavePolicy.MayLoad(
            ropeTeamSession: false, fileIsInBivouac: false, lastBivouacAge));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ARopeTeamRunRefusesQuickSavesAndDeathSegments(bool ropeTeam, bool allowed)
    {
        Assert.Equal(allowed, BivouacSavePolicy.MayWriteQuick(ropeTeam));
        Assert.Equal(allowed, BivouacSavePolicy.MayWriteDeath(ropeTeam));
    }

    [Fact]
    public void AQuickAutosaveIsRefusedAtCampAndTheLeaveCampAutosaveIsNot()
    {
        Assert.False(BivouacSavePolicy.MayWriteAutosave(
            ropeTeamSession: true, atCamp: true, restComplete: true, quick: true));
        Assert.True(BivouacSavePolicy.MayWriteAutosave(
            ropeTeamSession: true, atCamp: true, restComplete: true, quick: false));
        Assert.True(BivouacSavePolicy.MayWriteAutosave(
            ropeTeamSession: false, atCamp: false, restComplete: false, quick: true));
    }

    [Theory]
    [InlineData(false, true, 30f, false, false, false)]
    [InlineData(true, false, 30f, false, false, false)]
    [InlineData(true, true, 0.024f, false, false, false)]
    [InlineData(true, true, 0f, true, false, false)]
    [InlineData(true, true, 30f, false, false, true)]
    [InlineData(true, true, 30f, false, true, true)]
    [InlineData(true, true, 300f, true, false, false)]
    public void TheSaveMenuNamesAFileTheRunWouldRefuse(
        bool ropeTeamSaveSelect, bool hasSave, float lastBivouacAge,
        bool fileIsInBivouac, bool isQuickSave, bool shows)
    {
        Assert.Equal(shows, BivouacSavePolicy.ShowsTentOnlyNotice(
            ropeTeamSaveSelect, hasSave, lastBivouacAge, isQuickSave, fileIsInBivouac));
        Assert.Equal("Only saves in tent allowed", BivouacSavePolicy.TentOnlyNotice);
    }

    [Fact]
    public void ARopeTeamRunRefusesAQuickSaveFile()
    {
        Assert.False(BivouacSavePolicy.MayLoad(
            ropeTeamSession: true, fileIsInBivouac: true, lastBivouacAge: 0f, isQuickSave: true));
        Assert.False(BivouacSavePolicy.MayLoad(
            ropeTeamSession: true, fileIsInBivouac: false, lastBivouacAge: 0.024f, isQuickSave: true));
        Assert.True(BivouacSavePolicy.MayLoad(
            ropeTeamSession: false, fileIsInBivouac: false, lastBivouacAge: 30f, isQuickSave: true));
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, true)]
    public void TheRunStaysMarkedAfterDisconnectUntilTheMenu(
        bool marked, bool liveSession, bool backAtMenu, bool stays)
    {
        Assert.Equal(stays, BivouacSavePolicy.RunRemainsMarked(marked, liveSession, backAtMenu));
    }

    [Fact]
    public void TheBivouacSceneIsCampAndAStuckFlagIsNot()
    {
        Assert.True(BivouacSavePolicy.AtCamp(sceneIsBivouac: true, stuckFlagOverridden: true, lifecycleSaysBivouac: false));
        Assert.False(BivouacSavePolicy.AtCamp(sceneIsBivouac: false, stuckFlagOverridden: true, lifecycleSaysBivouac: true));
        Assert.True(BivouacSavePolicy.AtCamp(sceneIsBivouac: false, stuckFlagOverridden: false, lifecycleSaysBivouac: true));
        Assert.False(BivouacSavePolicy.AtCamp(sceneIsBivouac: false, stuckFlagOverridden: false, lifecycleSaysBivouac: false));
    }
}
