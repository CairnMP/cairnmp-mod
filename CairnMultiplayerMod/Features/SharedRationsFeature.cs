using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;

namespace CairnMultiplayerMod.Features;

/// <summary>
/// A ration opened on the rope feeds the rope.
///
/// Cairn's shared-rope protocol calls this ConsumeItemForRopeMembers: one climber drinks, the
/// others get what the drink was worth. Only the climber who opened it pays for it, and only
/// the partners actually tied to them benefit -- sharing is what the rope is for, and it
/// stops where the rope stops.
/// </summary>
internal sealed class SharedRationsFeature : MultiplayerFeature
{
    private Broadcast<RationShared> _shared;

    public override string Id => "rations";

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _shared = feature.Broadcast<RationShared>("shared", OnPartnerShared);
        feature.Game.Inventory.AddItemUsedListener(OnLocalItemUsed);
    }

    private void OnLocalItemUsed(int definitionId)
    {
        if (!IsConnected || definitionId <= 0) return;
        // A race is not a rope team, whatever the rope says: nobody hands a rival their water.
        if (Rules.Mode == MultiplayerMode.Race) return;
        if (!HasRopePartner()) return;

        _shared.Send(new RationShared(definitionId));
        LogInfo($"Shared item {definitionId} with the rope team");
    }

    private bool HasRopePartner()
    {
        foreach (var playerId in Game.Players.RemotePlayersInGame)
            if (Game.Players.IsRopedToLocalPlayer(playerId)) return true;
        return false;
    }

    private void OnPartnerShared(int fromPlayerId, RationShared ration)
    {
        if (!Game.Players.IsRopedToLocalPlayer(fromPlayerId)) return;
        if (!Game.Inventory.ApplySharedConsumable(ration.DefinitionId)) return;

        Game.Hud.ShowMessage("shared",
            $"{GetPlayerName(fromPlayerId)} shared their ration with the rope.", 4f);
    }
}

internal sealed class RationShared : IPacket
{
    public RationShared() { }
    public RationShared(int definitionId) => DefinitionId = definitionId;

    public int DefinitionId;

    public void Serialize(BinaryWriter writer) => writer.Write(DefinitionId);
    public void Deserialize(BinaryReader reader) => DefinitionId = reader.ReadInt32();
}
