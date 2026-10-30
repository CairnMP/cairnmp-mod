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

    [Fact]
    public void ASummitLaunchTakesTheSameRoadAsTheSummitButton()
    {
        // A player who has never read the onboarding pages reads them first; the game hands
        // them over to the world selection on its own.
        Assert.Equal(MainMenuInterop.MainMenuStep.SummitModeOnboarding, SummitWorldInterop.EntryStep(false));
        Assert.Equal(MainMenuInterop.MainMenuStep.SummitModeWorldSelect, SummitWorldInterop.EntryStep(true));
    }

    [Fact]
    public void SummitStepsMatchCairnsMenuSteps()
    {
        Assert.Equal(6, (int)MainMenuInterop.MainMenuStep.SummitModeWorldSelect);
        Assert.Equal(28, (int)MainMenuInterop.MainMenuStep.SummitModeOnboarding);
    }
}
