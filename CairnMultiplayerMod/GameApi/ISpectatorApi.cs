namespace CairnMultiplayerMod.GameApi;

/// <summary>
/// The view a player watches from once they are out of the climb. It takes over the camera
/// without touching the pawn: the body stays where it fell, and leaving hands the view back
/// to the game exactly as it was.
/// </summary>
internal interface ISpectatorApi
{
    bool IsActive { get; }

    /// <summary>True while the view is locked onto a climber rather than flying free.</summary>
    bool IsFollowing { get; }

    /// <summary>The climber being followed, or -1 while flying free.</summary>
    int FollowedPlayerId { get; }

    /// <summary>Takes over the camera from where the game was looking. Safe to call twice.</summary>
    bool Enter();

    void Leave();

    void FreeLook();

    /// <summary>Locks onto a climber. False when that player has no visible body to watch.</summary>
    bool Follow(int playerId);

    /// <summary>Moves the view for one frame. <paramref name="acceptInput"/> is false while
    /// another part of the mod is consuming the keyboard, such as the chat.</summary>
    void Tick(bool acceptInput);
}

/// <summary>Null implementation: no spectator seat, which is the vanilla behaviour.</summary>
internal sealed class UnavailableSpectatorApi : ISpectatorApi
{
    internal static readonly UnavailableSpectatorApi Instance = new();

    private UnavailableSpectatorApi() { }

    public bool IsActive => false;
    public bool IsFollowing => false;
    public int FollowedPlayerId => -1;
    public bool Enter() => false;
    public void Leave() { }
    public void FreeLook() { }
    public bool Follow(int playerId) => false;
    public void Tick(bool acceptInput) { }
}
