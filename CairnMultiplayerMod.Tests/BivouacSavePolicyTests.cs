using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BivouacSavePolicyTests
{
    public BivouacSavePolicyTests() => BivouacSaveSnapshot.Reset();

    [Fact]
    public void AContinuedSaveSetsThePitchAsideAndPutsItBack()
    {
        var plan = BivouacSavePolicy.ForWrite(
            ropeTeam: true, pitchLive: true, alreadyClean: false, leaving: false);
        Assert.True(plan.SetAside);
        Assert.True(plan.Restore);
        Assert.False(plan.TellsPartner);

        Assert.True(BivouacSaveSnapshot.Enter(
            ropeTeam: true, pitchLive: true, alreadyClean: false, leaving: false));
        Assert.False(BivouacSaveSnapshot.Enter(
            ropeTeam: true, pitchLive: true, alreadyClean: false, leaving: false));
        Assert.False(BivouacSaveSnapshot.Exit(failed: false));
        Assert.True(BivouacSaveSnapshot.Exit(failed: false));
    }

    [Fact]
    public void LeavingDoesNotPutThePitchBackUnlessTheWriteFails()
    {
        var plan = BivouacSavePolicy.ForWrite(
            ropeTeam: true, pitchLive: true, alreadyClean: false, leaving: true);
        Assert.True(plan.SetAside);
        Assert.False(plan.Restore);
        Assert.False(plan.TellsPartner);
        Assert.True(BivouacSavePolicy.Restores(plan, failed: true));

        Assert.True(BivouacSaveSnapshot.Enter(
            ropeTeam: true, pitchLive: true, alreadyClean: false, leaving: true));
        Assert.False(BivouacSaveSnapshot.Exit(failed: false));

        Assert.True(BivouacSaveSnapshot.Enter(
            ropeTeam: true, pitchLive: true, alreadyClean: false, leaving: true));
        Assert.True(BivouacSaveSnapshot.Exit(failed: true));
    }

    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public void ACleanWorldOrASoloClimbIsNotSetAside(
        bool ropeTeam, bool pitchLive, bool alreadyClean, bool leaving)
    {
        var plan = BivouacSavePolicy.ForWrite(ropeTeam, pitchLive, alreadyClean, leaving);
        Assert.False(plan.SetAside);
        Assert.False(plan.Restore);
        Assert.False(plan.TellsPartner);
        Assert.False(BivouacSavePolicy.Restores(plan, failed: true));

        Assert.False(BivouacSaveSnapshot.Enter(ropeTeam, pitchLive, alreadyClean, leaving));
        Assert.False(BivouacSaveSnapshot.Exit(failed: true));
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
