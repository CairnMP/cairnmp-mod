using System.Linq;
using CairnMultiplayer.Shared;
using Xunit;

namespace CairnMultiplayer.Tests;

public class ClimbWorldTests
{
    [Fact]
    public void WorldIdsAreCairnsOwn()
    {
        // The id goes straight back to the game's StreamingTweakables.World: renumbering one
        // would send a lobby to another world, not just rename it.
        Assert.Equal(0, (int)ClimbWorld.Kami);
        Assert.Equal(6, (int)ClimbWorld.Lamu);
        Assert.Equal(7, (int)ClimbWorld.Haken);
        Assert.Equal(8, (int)ClimbWorld.Ruka);
    }

    [Fact]
    public void KamiIsTheDefaultDestinationAndTheOnlyStoryOne()
    {
        // Both panels open on the first destination.
        Assert.Equal(ClimbWorld.Kami, ClimbWorlds.Available[0]);
        Assert.Equal(new[] { ClimbWorld.Kami }, ClimbWorlds.Available.Where(ClimbWorlds.IsStory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("haken")]
    public void ALobbyWithoutADestinationClimbsKami(string raw)
        => Assert.Equal(ClimbWorld.Kami, ClimbWorlds.Parse(raw));

    [Fact]
    public void KnownDestinationsReadBackFromTheWire()
    {
        foreach (var world in ClimbWorlds.Available)
            Assert.Equal(world, ClimbWorlds.Parse(((int)world).ToString()));
    }

    [Fact]
    public void AnUnknownDestinationIsKeptSoTheLaunchCanRefuseIt()
    {
        var world = ClimbWorlds.Parse("9");

        Assert.Equal(9, (int)world);
        Assert.False(ClimbWorlds.IsKnown(world));
        Assert.False(ClimbWorlds.IsStory(world));
        Assert.Equal("Unknown destination", ClimbWorlds.Name(world));
    }

    [Fact]
    public void EveryDestinationHasItsOwnName()
    {
        var names = ClimbWorlds.Available.Select(ClimbWorlds.Name).ToArray();

        Assert.DoesNotContain("Unknown destination", names);
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public void TheBadgeNamesTheDifficultyOnKamiAndTheWorldElsewhere()
    {
        var rules = MultiplayerModes.RopeTeam;

        Assert.Equal(MultiplayerModeText.DifficultyName(rules.Difficulty),
            MultiplayerModeText.Badge(rules, ClimbWorld.Kami));
        // Summit worlds run at the game's own difficulty, so the mode's would be a false promise.
        Assert.Equal("Haken", MultiplayerModeText.Badge(rules, ClimbWorld.Haken));
    }
}
