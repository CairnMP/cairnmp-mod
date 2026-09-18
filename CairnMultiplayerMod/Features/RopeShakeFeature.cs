using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;

namespace CairnMultiplayerMod.Features;

/// <summary>
/// The rope pulls back.
///
/// Until now being tied together was something you saw. Cairn's own shared-rope protocol says
/// what it should feel like: when a roped partner comes off the wall, the climbers tied to
/// them lose every hold they were merely holding -- the game even has a drop cause named
/// SharedRope for it. A firm hold rides it out, which is what makes the rope a decision
/// rather than a decoration.
/// </summary>
internal sealed class RopeShakeFeature : MultiplayerFeature
{
    private Broadcast<PartnerFell> _fell;
    private bool _wasFalling;

    public override string Id => "rope-shake";

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _fell = feature.Broadcast<PartnerFell>("fell", OnPartnerFell);

        // Gameplay: a fall only means anything while the pawn is ours to read.
        feature.EveryFrame(Tick);
        feature.OnSessionEnded(() => _wasFalling = false);
    }

    private void Tick()
    {
        var falling = Game.Players.IsLocalPlayerFalling;
        if (falling == _wasFalling) return;

        _wasFalling = falling;
        // Only the moment of coming off the wall travels; a long fall is one shake, not one
        // per frame.
        if (falling) _fell.Send(new PartnerFell());
    }

    private void OnPartnerFell(int fromPlayerId, PartnerFell message)
    {
        if (!Game.Players.IsRopedToLocalPlayer(fromPlayerId)) return;
        if (Game.Players.ShakeLocalClimberGrip())
            LogInfo($"Shaken off the wall by player {fromPlayerId}'s fall");
    }
}

internal sealed class PartnerFell : IPacket
{
    public void Serialize(BinaryWriter writer) => writer.Write((byte)1);
    public void Deserialize(BinaryReader reader) => reader.ReadByte();
}
