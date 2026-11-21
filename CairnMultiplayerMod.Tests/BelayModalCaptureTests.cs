using CairnMultiplayerMod.Internal.Game;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayModalCaptureTests
{
    [Fact]
    public void ClosingChatCannotReleaseAnOpenBelayModal()
    {
        Reset();
        try
        {
            InputCaptureState.IsKeyboardCaptured = true;
            InputCaptureState.IsBelayPanelCaptured = true;
            InputCaptureState.IsKeyboardCaptured = false;
            Assert.True(InputCaptureState.IsKeyboardCaptured);
            Assert.True(InputCaptureState.WantsGameplayBlocked);
            InputCaptureState.IsBelayPanelCaptured = false;
            Assert.False(InputCaptureState.IsKeyboardCaptured);
            Assert.False(InputCaptureState.WantsGameplayBlocked);
        }
        finally { Reset(); }
    }

    [Fact]
    public void ClosingBelayPreservesChatAndControllerCaptureUntilTheirOwnersReleaseThem()
    {
        Reset();
        try
        {
            InputCaptureState.IsKeyboardCaptured = true;
            InputCaptureState.IsBelayPanelCaptured = true;
            InputCaptureState.IsBelayPanelCaptured = false;
            Assert.True(InputCaptureState.IsKeyboardCaptured);
            Assert.True(InputCaptureState.WantsGameplayBlocked);
            InputCaptureState.IsControllerShortcutCaptured = true;
            InputCaptureState.IsKeyboardCaptured = false;
            Assert.False(InputCaptureState.IsKeyboardCaptured);
            Assert.True(InputCaptureState.WantsGameplayBlocked);
            InputCaptureState.IsControllerShortcutCaptured = false;
            Assert.False(InputCaptureState.WantsGameplayBlocked);
        }
        finally { Reset(); }
    }

    private static void Reset()
    {
        InputCaptureState.IsKeyboardCaptured = false;
        InputCaptureState.IsBelayPanelCaptured = false;
        InputCaptureState.IsControllerShortcutCaptured = false;
    }
}
