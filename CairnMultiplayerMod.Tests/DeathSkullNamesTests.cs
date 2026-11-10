using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class DeathSkullNamesTests
{
    [Theory]
    [InlineData("UI_HP_Picto_Skull")]
    [InlineData("ui_hp_picto_skull")]
    public void TheLifeBarSkullMatches(string name)
    {
        Assert.True(DeathSkullNames.IsSkull(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UI_HP_Picto_Heart")]
    public void OtherPictogramsDoNot(string name)
    {
        Assert.False(DeathSkullNames.IsSkull(name));
    }
}
