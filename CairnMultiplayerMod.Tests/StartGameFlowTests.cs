using CairnMultiplayerMod.Internal.Game;
using CairnMultiplayerMod.Internal.Game.MainMenu;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class StartGameFlowTests
{
    [Fact]
    public void MultiplayerLaunchOpensSaveSelectionBeforeStartingANewGame()
    {
        Assert.Equal(MainMenuInterop.MainMenuStep.StoryModeManageSave,
            StartGameFlow.MultiplayerLaunchEntryStep);
    }
}
