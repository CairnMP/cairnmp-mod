namespace CairnMultiplayerMod.Internal.Diagnostics;

internal enum BelayPersonalRopeAction { Grab, Ascend }

/// <summary>Personal rope control must not compete with the owned protection-rope budget.</summary>
internal static class BelayRopeInteractionGuard
{
    internal static bool ShouldBlock(BelayPersonalRopeAction action, bool ownsLocalRope, bool ending,
        bool loaded, bool placingProtection, bool personallyAnchoring)
    {
        if (!ownsLocalRope || ending) return false;
        if (action == BelayPersonalRopeAction.Ascend) return true;
        return loaded || !(placingProtection || personallyAnchoring);
    }
}
