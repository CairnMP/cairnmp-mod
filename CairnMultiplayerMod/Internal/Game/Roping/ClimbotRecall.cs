namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// After a tie, a climb bot that is still out is called home before the rope is pinned
/// to the harness. The pin is what lets a piton attach, and it is also what stops the
/// whistle. One ignored call may be asked again. A bot already on the way is left to finish.
/// </summary>
internal static class ClimbotRecall
{
    /// <summary>How long an ignored call waits before the one retry.</summary>
    internal const float RetryAfterSeconds = 1f;

    internal readonly struct Step
    {
        internal Step(bool call, bool holdRope, int asks)
        {
            Call = call;
            HoldRope = holdRope;
            Asks = asks;
        }

        /// <summary>Ask the bot to come home on this frame.</summary>
        internal bool Call { get; }

        /// <summary>Do not pin the rope, and do not switch it off.</summary>
        internal bool HoldRope { get; }

        /// <summary>Asks already spent on this outing. Two is the last.</summary>
        internal int Asks { get; }
    }

    /// <summary>
    /// <paramref name="outThere"/> is a bot waiting to be called, or sitting out to fetch a piton.
    /// <paramref name="arrivedHome"/> is a bot back with the climber. A bot on the way is neither.
    /// </summary>
    internal static Step Decide(bool outThere, bool arrivedHome, int asks, float secondsSinceAsk)
    {
        if (outThere)
        {
            var call = asks == 0 || (asks == 1 && secondsSinceAsk >= RetryAfterSeconds);
            return new Step(call, holdRope: true, asks: asks + (call ? 1 : 0));
        }

        if (asks > 0 && !arrivedHome)
            return new Step(call: false, holdRope: true, asks);

        return new Step(call: false, holdRope: false, asks: 0);
    }
}
