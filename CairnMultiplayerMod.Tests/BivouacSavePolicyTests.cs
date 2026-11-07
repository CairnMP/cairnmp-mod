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

    [Fact]
    public void TheBivouacSceneIsCampAndAStuckFlagIsNot()
    {
        Assert.True(BivouacSavePolicy.AtCamp(sceneIsBivouac: true, stuckFlagOverridden: true, lifecycleSaysBivouac: false));
        Assert.False(BivouacSavePolicy.AtCamp(sceneIsBivouac: false, stuckFlagOverridden: true, lifecycleSaysBivouac: true));
        Assert.True(BivouacSavePolicy.AtCamp(sceneIsBivouac: false, stuckFlagOverridden: false, lifecycleSaysBivouac: true));
        Assert.False(BivouacSavePolicy.AtCamp(sceneIsBivouac: false, stuckFlagOverridden: false, lifecycleSaysBivouac: false));
    }
}
