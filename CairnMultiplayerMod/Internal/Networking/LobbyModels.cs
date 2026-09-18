using CairnMultiplayer.Shared;
namespace CairnMultiplayerMod.Internal.Networking;

/// <summary>Visibility of a created lobby. Public: visible in the browser
/// and reliable for join-by-code; FriendsOnly/Private: favor the Steam invite.</summary>
internal enum LobbyVisibility
{
    Public,
    FriendsOnly,
    Private,
}

internal sealed class HostConfig
{
    public string PlayerName { get; init; } = "";
    public string LobbyName { get; init; } = "";
    public int MaxPlayers { get; init; } = 8;
    public LobbyVisibility Visibility { get; init; } = LobbyVisibility.Public;

    /// <summary>The rules everyone in the lobby will play by.</summary>
    public MultiplayerMode Mode { get; init; } = MultiplayerMode.RopeTeam;
}

internal sealed class LobbyEntry
{
    public ulong LobbyId { get; init; }
    public string Name { get; init; } = "";
    public string HostName { get; init; } = "";
    public int PlayerCount { get; init; }
    public int MaxPlayers { get; init; }
    public string Region { get; init; } = "";

    /// <summary>Advertised by the host, so the mode is visible before joining.</summary>
    public MultiplayerMode Mode { get; init; } = MultiplayerMode.RopeTeam;
}
