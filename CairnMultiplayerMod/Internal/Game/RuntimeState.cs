using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed class RuntimeState
{
    internal PlayerState LocalPlayerState { get; set; } = PlayerState.Unknown;
    internal string CurrentScene { get; set; }
    internal string LastGameplayScene { get; set; }
    internal float TimeSinceLastSceneLoad { get; set; }
}
