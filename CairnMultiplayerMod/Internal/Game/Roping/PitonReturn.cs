namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// A rope-team pitch leaves the leader's bag where the placements put it.
/// The bot does not hand pitons over at the ledge, and it does not pocket one
/// unless this climber is taking it.
/// </summary>
internal static class PitonReturn
{
    private static int _takes;

    internal static bool TakeInProgress => _takes > 0;

    internal static void BeginTake() => _takes++;

    internal static void EndTake()
    {
        if (_takes > 0) _takes--;
    }

    /// <summary>The bot's hand-over waits until the pitch rules drop at camp.</summary>
    internal static bool AllowsHandover(bool pitchInForce) => !pitchInForce;

    /// <summary>Pocketing is the player's take, or Cairn's own collection outside a pitch.</summary>
    internal static bool AllowsPocket(bool pitchInForce, bool takeInProgress)
        => !pitchInForce || takeInProgress;

    /// <summary>Cairn's own refill waits until camp. A rope-up is what moves pitons.</summary>
    internal static bool AllowsNativeRefill(bool pitchInForce) => !pitchInForce;

    /// <summary>
    /// A new tie moves the follower's pitons to the leader. So does roping up again
    /// after the route, once the staff is out. Rejoining a pitch the staff still holds
    /// does not.
    /// </summary>
    internal static bool ReturnsOnRopeUp(bool samePair, bool leaderLeft, bool staffStillHolds)
    {
        if (!samePair) return true;
        return leaderLeft && !staffStillHolds;
    }

    /// <summary>How many of the ask stayed in the bag. A refusal stores none.</summary>
    internal static int RecordedGain(int requested, int stored)
    {
        if (requested < 0) requested = 0;
        if (stored < 0) stored = 0;
        return stored < requested ? stored : requested;
    }
}
