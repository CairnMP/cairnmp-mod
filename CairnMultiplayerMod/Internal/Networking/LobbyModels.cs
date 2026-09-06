namespace CairnMultiplayerMod.Internal.Networking;

/// <summary>Visibility of a created lobby. Public: visible in the browser
/// and reliable for join-by-code; FriendsOnly/Private: favor the Steam invite.</summary>
internal enum LobbyVisibility
{
    Public,
    FriendsOnly,
    Private,
}

/// <summary>Configuration submitted by the user when creating a lobby.</summary>
internal sealed class HostConfig
{
    public string PlayerName { get; init; } = "";
    public string LobbyName { get; init; } = "";
    public int MaxPlayers { get; init; } = 8;
    public LobbyVisibility Visibility { get; init; } = LobbyVisibility.Public;
}

/// <summary>Entry shown in the public lobby browser.</summary>
internal sealed class LobbyEntry
{
    public ulong LobbyId { get; init; }   // Steam lobby SteamID64; 0 if not Steam
    public string Name { get; init; } = "";
    public string HostName { get; init; } = "";
    public int PlayerCount { get; init; }
    public int MaxPlayers { get; init; }
    public string Region { get; init; } = "";
}
