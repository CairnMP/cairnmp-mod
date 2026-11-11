using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Game.MainMenu;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class StoryLaunchPresetTests
{
    [Fact]
    public void APresetWithNoExtraConstraintsStaysNative()
    {
        Assert.True(GameOptionsInterop.UsesNativePreset(0, ClimbConstraints.None));
    }

    [Fact]
    public void AnExtraFlagAlreadyPresentOnThePresetStaysNative()
    {
        var preset = (int)(ClimbConstraints.PermaDeath | ClimbConstraints.NoAssistMode);
        Assert.True(GameOptionsInterop.UsesNativePreset(preset, ClimbConstraints.PermaDeath));
    }

    [Fact]
    public void AnExtraFlagThePresetDoesNotHaveIsCustomized()
    {
        Assert.False(GameOptionsInterop.UsesNativePreset(0, ClimbConstraints.PermaDeath));
    }

    [Fact]
    public void RopeTeamAndRaceAddNothingSoTheyStayOnThePreset()
    {
        Assert.True(GameOptionsInterop.UsesNativePreset(0, MultiplayerModes.RopeTeam.ExtraConstraints));
        Assert.True(GameOptionsInterop.UsesNativePreset(0, MultiplayerModes.Race.ExtraConstraints));
    }

    [Fact]
    public void FreeSoloStaysNativeOnlyWhenThePresetAlreadyHasPermadeath()
    {
        Assert.False(GameOptionsInterop.UsesNativePreset(0, MultiplayerModes.FreeSolo.ExtraConstraints));
        Assert.True(GameOptionsInterop.UsesNativePreset(
            (int)ClimbConstraints.PermaDeath, MultiplayerModes.FreeSolo.ExtraConstraints));
    }
}
