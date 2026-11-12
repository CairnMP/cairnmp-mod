namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// After a tie, a climb bot that is still out is called home before the rope is pinned
/// to the harness. The pin is what lets a piton attach, and it is also what stops the
/// whistle. A bot left busy after a staff unclip is told to stop securing instead.
/// One ignored call may be asked again. A bot already on the way is left to finish.
/// A clip that went on while the bot was ready stays up when the belay later looks busy.
/// </summary>
internal static class ClimbotRecall
{
    /// <summary>How long an ignored call waits before the one retry.</summary>
    internal const float RetryAfterSeconds = 1f;

    internal readonly struct Step
    {
        internal Step(bool call, bool stop, bool holdRope, int asks)
        {
            Call = call;
            Stop = stop;
            HoldRope = holdRope;
            Asks = asks;
        }

        /// <summary>Whistle the bot home on this frame. This is not the stop order.</summary>
        internal bool Call { get; }

        /// <summary>Tell the bot to stop securing on this frame. This is not the whistle.</summary>
        internal bool Stop { get; }

        /// <summary>Do not pin the rope, and do not switch it off.</summary>
        internal bool HoldRope { get; }

        /// <summary>Asks already spent on this outing. Two is the last.</summary>
        internal int Asks { get; }
    }

    /// <summary>
    /// A busy bot is stuck when this lead has not yet clipped while it was ready,
    /// a whistle is not already bringing it home, and this climber is not the
    /// follower held on a catch. Voicemail and a broken bot are not this flag.
    /// </summary>
    internal static bool IsStuckBelay(
        bool reportedBusy, bool clippedWhileReady, bool whistleInFlight, bool keepsFollowerCatch)
        => reportedBusy && !clippedWhileReady && !whistleInFlight && !keepsFollowerCatch;

    /// <summary>
    /// A clip made while the bot was ready stays up on the wall. Back on the ground,
    /// a busy bot is stuck again, and the next climb must not clip onto it.
    /// </summary>
    internal static bool GroundEndsReadyClip(bool clippedWhileReady, bool onGround, bool reportedBusy)
        => clippedWhileReady && onGround && reportedBusy;

    /// <summary>
    /// <paramref name="outThere"/> is a bot waiting to be called, or sitting out to fetch a piton.
    /// <paramref name="arrivedHome"/> is a bot back with the climber. A bot on the way is neither.
    /// <paramref name="stuckBusy"/> is a belay left open after the rope came off. It holds the
    /// pin and asks the bot to stop. It does not whistle.
    /// </summary>
    internal static Step Decide(
        bool outThere, bool arrivedHome, int asks, float secondsSinceAsk, bool stuckBusy = false)
    {
        if (arrivedHome)
            return new Step(call: false, stop: false, holdRope: false, asks: 0);

        if (outThere)
        {
            var call = asks == 0 || (asks == 1 && secondsSinceAsk >= RetryAfterSeconds);
            return new Step(call, stop: false, holdRope: true, asks: asks + (call ? 1 : 0));
        }

        if (stuckBusy)
        {
            var stop = asks == 0 || (asks == 1 && secondsSinceAsk >= RetryAfterSeconds);
            return new Step(call: false, stop, holdRope: true, asks: asks + (stop ? 1 : 0));
        }

        if (asks > 0)
            return new Step(call: false, stop: false, holdRope: true, asks);

        return new Step(call: false, stop: false, holdRope: false, asks: 0);
    }
}
