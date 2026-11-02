using CairnMultiplayerMod.Internal.Game;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class RadialMenuStateTests
{
    private const float Frame = 0.05f;

    private static RadialMenuState Menu() => new(7);

    /// <summary>Holds the button with the stick centred until the menu is up and armed.</summary>
    private static RadialMenuState OpenMenu()
    {
        var menu = Menu();
        for (var i = 0; i < 4; i++) menu.Update(true, 0f, 0f, Frame, false);
        Assert.True(menu.IsVisible);
        return menu;
    }

    [Theory]
    [InlineData(0f, 1f, 0)]
    [InlineData(1f, 0f, 2)]
    [InlineData(-0.1f, -1f, 4)]
    [InlineData(-1f, 0f, 5)]
    public void TheStickPicksTheItemInThatDirection(float x, float y, int expected)
        => Assert.Equal(expected, Menu().IndexFor(x, y));

    [Fact]
    public void TheMenuOnlyAppearsAfterAShortDelay()
    {
        var menu = Menu();
        menu.Update(true, 0f, 0f, 0.05f, false);
        Assert.False(menu.IsVisible);

        menu.Update(true, 0f, 0f, 0.2f, false);
        Assert.True(menu.IsVisible);
    }

    [Fact]
    public void ReleasingOnAnAimedItemChoosesIt()
    {
        var menu = OpenMenu();
        menu.Update(true, 1f, 0f, Frame, false);
        Assert.Equal(2, menu.SelectedIndex);

        Assert.Equal(2, menu.Update(false, 1f, 0f, Frame, false));
        Assert.False(menu.IsVisible);
    }

    [Fact]
    public void ReleasingWithTheStickCentredChoosesNothing()
    {
        var menu = OpenMenu();
        menu.Update(true, 1f, 0f, Frame, false);
        menu.Update(true, 0f, 0f, Frame, false);

        Assert.Equal(-1, menu.Update(false, 0f, 0f, Frame, false));
    }

    [Fact]
    public void AStickAlreadyPushedWhenTheButtonGoesDownChoosesNothing()
    {
        var menu = Menu();
        for (var i = 0; i < 6; i++) menu.Update(true, 1f, 0f, Frame, false);

        Assert.Equal(-1, menu.SelectedIndex);
        Assert.Equal(-1, menu.Update(false, 1f, 0f, Frame, false));
    }

    [Fact]
    public void ReleasingBeforeTheMenuAppearsChoosesNothing()
    {
        var menu = Menu();
        menu.Update(true, 0f, 0f, 0.02f, false);
        menu.Update(true, 1f, 0f, 0.02f, false);

        Assert.Equal(-1, menu.Update(false, 1f, 0f, 0.02f, false));
    }

    [Fact]
    public void AComboButtonCancelsTheMenuUntilTheButtonIsReleased()
    {
        var menu = OpenMenu();
        menu.Update(true, 1f, 0f, Frame, true);
        Assert.False(menu.IsVisible);

        menu.Update(true, 1f, 0f, Frame, false);
        Assert.False(menu.IsVisible);
        Assert.Equal(-1, menu.Update(false, 1f, 0f, Frame, false));

        for (var i = 0; i < 4; i++) menu.Update(true, 0f, 0f, Frame, false);
        Assert.True(menu.IsVisible);
    }

    [Fact]
    public void EveryRadialItemIsAShortcutAndTheHeldOnesAreNot()
    {
        foreach (var item in ControllerShortcutBindings.RadialItems)
            Assert.True(ControllerShortcutBindings.IsRadialItem(item));

        Assert.False(ControllerShortcutBindings.IsRadialItem(ControllerShortcut.PushToTalk));
        Assert.False(ControllerShortcutBindings.IsRadialItem(ControllerShortcut.Panic));
        Assert.False(ControllerShortcutBindings.IsRadialItem(ControllerShortcut.PreviousSharedItem));
        Assert.False(ControllerShortcutBindings.IsRadialItem(ControllerShortcut.NextSharedItem));
    }
}
