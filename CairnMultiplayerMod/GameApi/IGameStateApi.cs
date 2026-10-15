using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Stable high-level state of the local Cairn player.</summary>
internal interface IGameStateApi
{
    PlayerState LocalPlayerState { get; }
    bool IsLocalPlayerInGame { get; }

    /// <summary>True while the local climber is in a bivouac (or entering one). A camp is
    /// where a session can afford to be generous with the rules.</summary>
    bool IsLocalPlayerInBivouac { get; }

    /// <summary>
    /// Where the local climber is camped, while <see cref="IsLocalPlayerInBivouac"/>. Their
    /// player location stays unknown for the whole camp — gameplay sync stands down there —
    /// so this is the only way to point somebody at the fire.
    /// </summary>
    bool TryGetCampPosition(out WorldPosition position);

    /// <summary>True once the game considers the climb finished at the top.</summary>
    bool HasReachedSummit { get; }
}
