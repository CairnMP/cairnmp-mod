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
}
