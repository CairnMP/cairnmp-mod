using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Stable high-level state of the local Cairn player.</summary>
internal interface IGameStateApi
{
    PlayerState LocalPlayerState { get; }
    bool IsLocalPlayerInGame { get; }
    bool IsLocalPlayerInCutscene => false;

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

    /// <summary>
    /// True while the local climber is standing in a bivouac's volume on the mountain.
    /// The interior of the rest is <see cref="IsLocalPlayerInBivouac"/>, not this.
    /// </summary>
    bool IsInCampVolume { get; }

    /// <summary>
    /// The camp on the mountain a partner can be brought back to. Inside the rest this is
    /// the camp they entered, not the interior pose. False when that camp is not known.
    /// </summary>
    bool TryGetRecallCamp(out WorldPosition position);

    /// <summary>True when <paramref name="pose"/> is in the same camp volume as <paramref name="camp"/>.</summary>
    bool SameCampVolume(WorldPosition camp, WorldPosition pose);
}
