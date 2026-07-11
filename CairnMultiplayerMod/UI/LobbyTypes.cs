namespace CairnMultiplayerMod.UI;

/// <summary>Visibilite d'un lobby cree. Public : visible dans le browser
/// et fiable pour le join-by-code ; FriendsOnly/Private : privilegient l'invite Steam.</summary>
public enum LobbyVisibility
{
    Public,
    FriendsOnly,
    Private,
}

/// <summary>Configuration soumise par l'utilisateur lors de la creation d'un lobby.</summary>
public sealed class HostConfig
{
    public string PlayerName { get; init; } = "";
    public string LobbyName  { get; init; } = "";
    public int    MaxPlayers { get; init; } = 8;
    public LobbyVisibility Visibility { get; init; } = LobbyVisibility.Public;
}

/// <summary>Entree affichee dans le browser de lobbies publics.</summary>
public sealed class LobbyEntry
{
    public ulong  LobbyId     { get; init; }   // Steam lobby SteamID64 ; 0 si non Steam
    public string Name        { get; init; } = "";
    public string HostName    { get; init; } = "";
    public int    PlayerCount { get; init; }
    public int    MaxPlayers  { get; init; }
    public string Region      { get; init; } = "";
}
