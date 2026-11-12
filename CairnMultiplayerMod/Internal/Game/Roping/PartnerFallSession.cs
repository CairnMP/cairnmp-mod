namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// The partner's last reported pose, and the untie that still owes someone a fall.
/// The pose arrives on the rope.fall-state stream. A missed packet is replaced by the next.
/// </summary>
internal static class PartnerFallSession
{
    internal static PartnerPose Partner { get; private set; }

    internal static bool ForceRelease { get; set; }

    internal static bool DropPending { get; private set; }

    internal static void Remember(PartnerPose pose) => Partner = pose;

    internal static void ForgetPartner()
    {
        Partner = PartnerPose.OnGround;
    }

    internal static bool TakeForceRelease()
    {
        var force = ForceRelease;
        ForceRelease = false;
        return force;
    }

    internal static void ArmDrop() => DropPending = true;

    internal static void CompleteDrop() => DropPending = false;

    internal static void Reset()
    {
        Partner = PartnerPose.OnGround;
        ForceRelease = false;
        DropPending = false;
    }
}
