using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Game.MainMenu;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class StoryDifficultySelectionTests
{
    [Theory]
    [InlineData(MultiplayerMode.BelayTeam)]
    [InlineData(MultiplayerMode.RopeTeam)]
    [InlineData(MultiplayerMode.Race)]
    public void PresetModesKeepTheNativeStoryPathThatClearsSummitState(MultiplayerMode mode)
        => Assert.False(GameOptionsInterop.NeedsCustomStoryDifficulty(ClimbConstraints.None,
            MultiplayerModes.Parse((byte)mode).ExtraConstraints));

    [Fact]
    public void FreeSoloStillRequiresItsAdditionalNativeConstraints()
        => Assert.True(GameOptionsInterop.NeedsCustomStoryDifficulty(ClimbConstraints.None, MultiplayerModes.FreeSolo.ExtraConstraints));

    [Fact]
    public void ConstraintsAlreadyProvidedByTheNativePresetDoNotForceACustomRun()
        => Assert.False(GameOptionsInterop.NeedsCustomStoryDifficulty(ClimbConstraints.NoStory, ClimbConstraints.NoStory));
}
