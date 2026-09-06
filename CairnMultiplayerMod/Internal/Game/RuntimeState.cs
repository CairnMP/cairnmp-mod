using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Mutable session/scene state owned by the bootstrap and explicitly shared with the
/// runtime components that need it. This replaces the former global bootstrap access.
/// </summary>
internal sealed class RuntimeState
{
    internal PlayerState LocalPlayerState { get; set; } = PlayerState.Unknown;
    internal string CurrentScene { get; set; }
    internal string LastGameplayScene { get; set; }
    internal float TimeSinceLastSceneLoad { get; set; }
}
