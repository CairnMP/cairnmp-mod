using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class SavedPitonLoadGuardTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OnlyPitonsWithoutAPoseAreSkipped(bool hasValidPose, bool skipped)
        => Assert.Equal(skipped, SavedPitonLoadGuard.ShouldSkip(hasValidPose));
}
