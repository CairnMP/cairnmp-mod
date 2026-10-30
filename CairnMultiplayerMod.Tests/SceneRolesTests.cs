using CairnMultiplayerMod.Internal.Game;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class SceneRolesTests
{
    [Fact]
    public void AdditiveSceneResolvesToLastGameplayRoot()
        => Assert.Equal("2_Kami", SceneRoles.ResolveNetworkScene(
            "Kami_AudioWorldScene", "2_Kami"));

    [Fact]
    public void GameplayRootRemainsCurrentNetworkScene()
        => Assert.Equal("3_Summit", SceneRoles.ResolveNetworkScene(
            "3_Summit", "2_Kami"));

    [Theory]
    [InlineData("MainMenu")]
    [InlineData("LoadingScreen")]
    public void TransitionSceneDoesNotReusePreviousGameplayRoot(string scene)
        => Assert.Equal(scene, SceneRoles.ResolveNetworkScene(scene, "2_Kami"));

    [Theory]
    [InlineData("2_Kami", true)]
    [InlineData("Kami_AudioWorldScene", true)]
    [InlineData("2_Kami_LOD", true)]
    [InlineData("LoadingScreen", false)]
    [InlineData("CommonBaseScene", false)]
    [InlineData("MainMenu", false)]
    [InlineData("BivouacIndoor", false)]
    public void GameplayContextIncludesAdditiveLayersButNotRealBoundaries(string scene, bool expected)
        => Assert.Equal(expected, SceneRoles.HasGameplayContext(scene, "2_Kami"));

    [Theory]
    [InlineData("Crux_01_TowerGym", true)]
    [InlineData("Crux_02_DeepWaterPiton", true)]
    [InlineData("Crux_03_Mallorca", true)]
    [InlineData("Crux_01_TowerGym_Holds2", false)]
    [InlineData("Crux_01_TowerGym_Art", false)]
    [InlineData("Crux_01_TowerGym_Background", false)]
    [InlineData("Crux_Common", false)]
    [InlineData("Crux_A1_TowerGym", false)]
    [InlineData("Crux__TowerGym", false)]
    public void ADlcWorldIsAGameplayRootButItsLayersAreNot(string scene, bool expected)
        => Assert.Equal(expected, SceneRoles.IsGameplayRoot(scene));

    [Fact]
    public void DlcLayersResolveToTheirWorld()
        => Assert.Equal("Crux_01_TowerGym", SceneRoles.ResolveNetworkScene(
            "Crux_01_TowerGym_Holds2", "Crux_01_TowerGym"));

    [Fact]
    public void EnteringADlcWorldResetsTheSync()
        => Assert.True(SceneRoles.IsSyncResetPoint("Crux_01_TowerGym"));

    [Theory]
    [InlineData("Crux_01_TowerGym_Art", true)]
    [InlineData("MainMenu", false)]
    [InlineData("BivouacIndoor", false)]
    public void ADlcWorldKeepsItsGameplayContext(string scene, bool expected)
        => Assert.Equal(expected, SceneRoles.HasGameplayContext(scene, "Crux_01_TowerGym"));
}
