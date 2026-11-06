using CairnMultiplayerMod.Internal.Game;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class RadialMenuLabelsTests
{
    private static RadialContext Context(bool rope = false, bool names = true) => new(rope, names);

    [Theory]
    [InlineData(false, "Attach rope")]
    [InlineData(true, "Detach rope")]
    public void TheRopeEntryNamesTheNextAction(bool attached, string expected)
        => Assert.Equal(expected, RadialMenuLabels.For(ControllerShortcut.ToggleRope, Context(rope: attached)));

    [Theory]
    [InlineData(true, "Hide names")]
    [InlineData(false, "Show names")]
    public void TheNamesEntryNamesTheNextAction(bool shown, string expected)
        => Assert.Equal(expected, RadialMenuLabels.For(ControllerShortcut.ToggleNames, Context(names: shown)));

    [Fact]
    public void GiveAndDropAreCombosOnly()
    {
        Assert.False(ControllerShortcutBindings.IsRadialItem(ControllerShortcut.GiveSelectedItem));
        Assert.False(ControllerShortcutBindings.IsRadialItem(ControllerShortcut.DropSelectedItem));
    }

    [Fact]
    public void EveryRadialItemHasATitle()
    {
        foreach (var item in ControllerShortcutBindings.RadialItems)
            Assert.False(string.IsNullOrWhiteSpace(RadialMenuLabels.For(item, Context())));
    }
}
