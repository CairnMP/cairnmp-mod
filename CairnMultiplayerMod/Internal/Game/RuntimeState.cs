using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed class RuntimeState
{
    internal PlayerState LocalPlayerState { get; set; } = PlayerState.Unknown;
    internal string CurrentScene { get; set; }
    internal string LastGameplayScene { get; set; }
    internal float TimeSinceLastSceneLoad { get; set; }

    /// <summary>
    /// The rules the session is being played under. Set when the host's launch signal is
    /// applied and read by everything that behaves differently per mode. Outside a lobby it
    /// stays on the default, so solo play and a rope-team lobby answer the same.
    /// </summary>
    internal MultiplayerModeRules ModeRules { get; set; } = MultiplayerModes.RopeTeam;
}
