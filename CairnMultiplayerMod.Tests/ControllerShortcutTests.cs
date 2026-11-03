using System;
using System.Linq;
using CairnMultiplayerMod.Internal.Game;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class ControllerShortcutTests
{
    [Fact]
    public void EveryShortcutHasADedicatedModifiedButton()
    {
        var shortcuts = Enum.GetValues<ControllerShortcut>();
        var buttons = shortcuts.Select(ControllerShortcutBindings.ButtonFor).ToArray();

        Assert.Equal(shortcuts.Length, buttons.Distinct().Count());
        Assert.Equal("View/Share", ControllerShortcutBindings.ModifierDisplayName);
    }

    [Fact]
    public void EveryShortcutHasAnOnScreenLabel()
    {
        foreach (var shortcut in Enum.GetValues<ControllerShortcut>())
        {
            var label = ControllerShortcutBindings.LabelFor(shortcut);
            Assert.StartsWith("View/Share + ", label);
            Assert.False(string.IsNullOrWhiteSpace(ControllerShortcutBindings.DescriptionFor(shortcut)));
        }

        Assert.Equal("View/Share + Y/Triangle: Attach or detach rope",
            ControllerShortcutBindings.LabelFor(ControllerShortcut.ToggleRope));
    }

    [Theory]
    [InlineData((int)ControllerShortcut.ToggleRope, (int)ControllerButton.North)]
    [InlineData((int)ControllerShortcut.PickupSharedItem, (int)ControllerButton.South)]
    [InlineData((int)ControllerShortcut.GiveSelectedItem, (int)ControllerButton.West)]
    [InlineData((int)ControllerShortcut.DropSelectedItem, (int)ControllerButton.East)]
    [InlineData((int)ControllerShortcut.PlacePing, (int)ControllerButton.RightShoulder)]
    [InlineData((int)ControllerShortcut.PushToTalk, (int)ControllerButton.LeftShoulder)]
    [InlineData((int)ControllerShortcut.Panic, (int)ControllerButton.RightStick)]
    public void GameplayShortcutsKeepTheDocumentedLayout(int shortcut, int expected)
        => Assert.Equal((ControllerButton)expected,
            ControllerShortcutBindings.ButtonFor((ControllerShortcut)shortcut));

    [Fact]
    public void FeedbackNamesTheButtonOnlyWhenTheRadialMenuIsOff()
    {
        Assert.Equal("Attach or detach rope",
            ControllerShortcutBindings.FeedbackFor(ControllerShortcut.ToggleRope, radialMenu: true));
        Assert.Equal("View/Share + Y/Triangle: Attach or detach rope",
            ControllerShortcutBindings.FeedbackFor(ControllerShortcut.ToggleRope, radialMenu: false));
    }

    [Fact]
    public void TheGuideShowsBothControllerStylesAndTheKeyboardAtOnce()
    {
        var columns = ShortcutGuide.Columns(true, "F5", "F6", "V");
        Assert.Equal(3, columns.Length);

        var radial = columns[0].Lines;
        var combos = columns[1].Lines;
        foreach (var item in ControllerShortcutBindings.RadialItems)
        {
            var description = ControllerShortcutBindings.DescriptionFor(item);
            Assert.Contains(description, radial);
            Assert.Contains(combos, line => line.EndsWith(description));
        }

        Assert.Contains("F5: Multiplayer panel", columns[2].Lines);
        Assert.Contains("V: Push to talk", columns[2].Lines);
    }

    [Theory]
    [InlineData(true, "Controller: radial menu (on)", "Controller: button combos")]
    [InlineData(false, "Controller: radial menu", "Controller: button combos (on)")]
    public void TheGuideMarksTheActiveStyle(bool radial, string first, string second)
    {
        var columns = ShortcutGuide.Columns(radial, "F5", "F6", "V");
        Assert.Equal(first, columns[0].Title);
        Assert.Equal(second, columns[1].Title);
    }
}
