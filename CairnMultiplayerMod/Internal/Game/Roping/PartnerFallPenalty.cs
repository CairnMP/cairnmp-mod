using System;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// What the other climber is doing on the rope. Hanging is a fall, a dangle in the air,
/// or off belay. Hanging on a hold is still climbing.
/// </summary>
internal enum PartnerPose : byte
{
    OnGround = 0,
    Climbing = 1,
    Hanging = 2,
    Down = 3,
}

/// <summary>Whether this climber spends grip while the partner hangs.</summary>
internal readonly struct PartnerHold
{
    internal PartnerHold(bool drain) => Drain = drain;

    /// <summary>The leader is holding the follower's weight.</summary>
    internal bool Drain { get; }
}

/// <summary>What an untie does to a hanging climber, and to the leader's grip.</summary>
internal readonly struct PartnerUntie
{
    internal PartnerUntie(bool dropLocal, bool refillGrip, bool holdStaff)
    {
        DropLocal = dropLocal;
        RefillGrip = refillGrip;
        HoldStaff = holdStaff;
    }

    /// <summary>This climber was hanging, so the untie drops them.</summary>
    internal bool DropLocal { get; }

    /// <summary>The leader cut the follower loose and gets a full grip back.</summary>
    internal bool RefillGrip { get; }

    /// <summary>Nobody was hanging, so a planted staff may keep the chain.</summary>
    internal bool HoldStaff { get; }

    /// <summary>A hanging untie must not leave the chain to catch the fall.</summary>
    internal bool ForceRelease => !HoldStaff;
}

/// <summary>
/// The rope-team answer when the other climber comes off the wall, dangles on the rope,
/// hangs in the air, or sits off belay. A hang on a hold does not count. The leader's
/// grip empties while the follower hangs. The follower keeps moving. An untie drops
/// whoever is hanging.
/// Only the leader's cut fills their grip. An empty chain still uses Cairn's own
/// unsecured fall, elsewhere.
/// </summary>
internal static class PartnerFallPenalty
{
    /// <summary>A full grip is gone after this many seconds of holding the follower.</summary>
    internal const float FullGripSeconds = 3f;

    internal static PartnerPose Classify(bool dead, bool falling, bool offBelay, string moduleName, bool voidHang = false)
    {
        if (dead) return PartnerPose.Down;
        if (offBelay || IsOffBelayModule(moduleName)) return PartnerPose.Hanging;
        if (falling || IsFallModule(moduleName)) return PartnerPose.Hanging;
        if (IsSuspendedOnRope(moduleName) || voidHang) return PartnerPose.Hanging;
        var module = TopAnchorPolicy.Classify(moduleName);
        if (module is TopAnchorModule.Wall or TopAnchorModule.Mantling or TopAnchorModule.GetUpFromClimb)
            return PartnerPose.Climbing;
        return PartnerPose.OnGround;
    }

    internal static PartnerHold Tick(
        bool linked,
        bool leaderLeft,
        bool localIsLeader,
        bool selfFalling,
        bool selfDown,
        PartnerPose partner)
    {
        if (!linked || leaderLeft || selfDown)
            return new PartnerHold(false);

        var drain = localIsLeader && !selfFalling && partner == PartnerPose.Hanging;
        return new PartnerHold(drain);
    }

    internal static PartnerUntie Untie(bool localIsLeader, bool localUntied, bool localHanging, bool partnerHanging)
    {
        var hanging = localHanging || partnerHanging;
        var refill = localIsLeader && localUntied && partnerHanging;
        return new PartnerUntie(localHanging, refill, holdStaff: !hanging);
    }

    /// <summary>A rappel, an abseil, or a rope hang. A hold on the wall is not this.</summary>
    internal static bool IsSuspendedOnRope(string moduleName)
        => moduleName != null
           && (moduleName.Contains("Rappel", StringComparison.Ordinal)
               || moduleName.Contains("Abseil", StringComparison.Ordinal)
               || moduleName.Contains("RopeAscending", StringComparison.Ordinal)
               || moduleName.Contains("RopeHang", StringComparison.Ordinal));

    /// <summary>Cairn's own unsecured fall. A secured catch and a rope hang still need a drop.</summary>
    internal static bool IsUnsecuredFallModule(string moduleName)
        => moduleName != null
           && moduleName.Contains("Falling", StringComparison.Ordinal)
           && !moduleName.Contains("Secured", StringComparison.Ordinal);

    private static bool IsOffBelayModule(string moduleName)
        => moduleName != null && moduleName.Contains("OffBelay", StringComparison.Ordinal);

    private static bool IsFallModule(string moduleName)
        => moduleName != null
           && (moduleName.Contains("SecuredFalling", StringComparison.Ordinal)
               || moduleName.Contains("Falling", StringComparison.Ordinal));
}
