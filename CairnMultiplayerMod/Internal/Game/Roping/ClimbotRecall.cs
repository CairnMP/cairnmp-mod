namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// After a tie, a climb bot that is still out is called home before the rope is pinned
/// to the harness. The pin is what lets a piton attach, and it is also what stops the
/// whistle. A bot left busy after a staff unclip is told to stop securing instead.
/// One ignored call may be asked again. A bot already on the way is left to finish.
/// A clip that went on while the bot was ready stays up while the leader is still
/// on the wall. Back on the ground, a busy belay is stuck and is told to stop.
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

    /// <summary>What to do with the harness this frame. Each value is one outcome.</summary>
    internal enum Harness
    {
        /// <summary>The leader clips the securing rope on.</summary>
        LeaderPin,
        /// <summary>The follower's catch stays clipped.</summary>
        FollowerPin,
        /// <summary>The leader's stale clip comes off until the bot is ready.</summary>
        LeaderRelease,
        /// <summary>The leader has left at a staff. The harness comes off.</summary>
        LeaderLeftRelease,
        /// <summary>A busy belay that is not this lead. The stale clip comes off.</summary>
        StaleRelease,
        /// <summary>Do not touch the harness.</summary>
        Leave,
        /// <summary>The rope stays off this harness.</summary>
        Hide,
    }

    /// <summary>
    /// One harness answer. Who is told to stop securing is decided by
    /// <see cref="Decide"/>, not here. The flags are inputs.
    /// </summary>
    internal static Harness ChooseHarness(
        bool whistleHold, bool busyHold, bool leads, bool leaderLeft, bool followerClip)
    {
        if (!whistleHold && leads)
            return busyHold ? Harness.LeaderRelease : Harness.LeaderPin;
        if (!whistleHold && leaderLeft)
            return Harness.LeaderLeftRelease;
        if (followerClip)
            return Harness.FollowerPin;
        if (!whistleHold && busyHold)
            return Harness.StaleRelease;
        if (whistleHold)
            return Harness.Leave;
        return Harness.Hide;
    }

    /// <summary>
    /// A busy bot is stuck when a Ready clip does not protect this belay, a whistle
    /// is not already bringing it home, and this climber is not the follower held
    /// on a catch. A Ready clip only protects while still on the wall; on the ground
    /// Busy is stuck even after that clip. Voicemail and a broken bot are not this flag.
    /// </summary>
    internal static bool IsStuckBelay(
        bool reportedBusy,
        bool clippedWhileReady,
        bool whistleInFlight,
        bool keepsFollowerCatch,
        bool onGround = false)
        => reportedBusy
           && (!clippedWhileReady || onGround)
           && !whistleInFlight
           && !keepsFollowerCatch;

    /// <summary>
    /// Whether <see cref="Decide"/> should treat the bot as out. On the ground a stuck
    /// belay wins: whistle must not outrank <c>OrderToStopSecuring</c>.
    /// </summary>
    internal static bool PickOuting(bool outThere, bool stuckBusy, bool onGround)
        => outThere && !(stuckBusy && onGround);

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
