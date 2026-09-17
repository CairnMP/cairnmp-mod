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
}
