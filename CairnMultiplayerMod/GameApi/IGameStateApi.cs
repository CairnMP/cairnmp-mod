using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Stable high-level state of the local Cairn player.</summary>
internal interface IGameStateApi
{
    PlayerState LocalPlayerState { get; }
    bool IsLocalPlayerInGame { get; }
}
