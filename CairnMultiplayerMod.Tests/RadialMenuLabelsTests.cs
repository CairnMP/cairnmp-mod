using CairnMultiplayerMod.Internal.Game;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class RadialMenuLabelsTests
{
    private static RadialContext Context(bool rope = false, bool names = true, string item = null, int count = 0)
        => new(rope, names, item, count);

    [Theory]
    [InlineData(false, "Attach rope")]
    [InlineData(true, "Detach rope")]
    public void TheRopeEntryNamesTheNextAction(bool attached, string expected)
        => Assert.Equal(expected,
            RadialMenuLabels.For(ControllerShortcut.ToggleRope, Context(rope: attached)).Title);

    [Theory]
    [InlineData(true, "Hide names")]
    [InlineData(false, "Show names")]
    public void TheNamesEntryNamesTheNextAction(bool shown, string expected)
        => Assert.Equal(expected,
            RadialMenuLabels.For(ControllerShortcut.ToggleNames, Context(names: shown)).Title);

    [Theory]
    [InlineData((int)ControllerShortcut.GiveSelectedItem, "Give item")]
    [InlineData((int)ControllerShortcut.DropSelectedItem, "Drop item")]
    public void GiveAndDropShowTheSelectedItem(int value, string title)
    {
        var shortcut = (ControllerShortcut)value;
        var label = RadialMenuLabels.For(shortcut, Context(item: "Water Bottle", count: 1));
        Assert.Equal(title, label.Title);
        Assert.Equal("Water Bottle", label.Detail);
        Assert.False(label.Dimmed);

        Assert.Equal("Water Bottle x3", RadialMenuLabels.For(shortcut, Context(item: "Water Bottle", count: 3)).Detail);
    }

    [Fact]
    public void GiveAndDropAreDimmedWithoutAnItem()
    {
        var label = RadialMenuLabels.For(ControllerShortcut.GiveSelectedItem, Context());
        Assert.True(label.Dimmed);
        Assert.Equal("No shareable item", label.Detail);
    }

    [Fact]
    public void EveryRadialItemHasATitle()
    {
        foreach (var item in ControllerShortcutBindings.RadialItems)
            Assert.False(string.IsNullOrWhiteSpace(RadialMenuLabels.For(item, Context()).Title));
    }
}
