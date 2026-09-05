using System;
using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Networking.Authoritative;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PitonAuthorityTests
{
    [Fact]
    public void RepeatedPlacementHasNoAdditionalSpawnOrBroadcast()
    {
        var sink = new RecordingSink();
        var spawns = 0;
        var authority = new PitonAuthority(sink, _ => spawns++, _ => { });
        for (var i = 0; i < 1000; i++) PlaceFromClient(authority, 10, 7);
        Assert.Equal(1, spawns);
        Assert.Single(sink.Broadcasts);
        authority.SendSnapshotTo(20);
        Assert.Single(sink.Sends);
    }

    [Fact]
    public void PlacementQuotaSurvivesSnapshotAndReleasesOnDeparture()
    {
        var sink = new RecordingSink();
        double time = 0;
        var removed = 0;
        var authority = new PitonAuthority(sink, _ => { }, _ => removed++, () => time);
        for (uint id = 1; id <= 150; id++)
        {
            time++;
            PlaceFromClient(authority, 10, id);
        }
        Assert.Equal(PitonAuthority.MaxPitonsPerPlayer, sink.Broadcasts.Count);
        authority.SendSnapshotTo(20);
        Assert.Equal(PitonAuthority.MaxPitonsPerPlayer, sink.Sends.Count);
        authority.RemovePlayer(10);
        Assert.Equal(PitonAuthority.MaxPitonsPerPlayer, removed);
        sink.Sends.Clear();
        authority.SendSnapshotTo(20);
        Assert.Empty(sink.Sends);
        PlaceFromClient(authority, 10, 1);
        authority.SendSnapshotTo(20);
        Assert.Single(sink.Sends);
    }

    [Fact]
    public void UniqueIdFloodIsRateLimitedBeforeNativeSpawn()
    {
        var sink = new RecordingSink();
        var spawns = 0;
        var authority = new PitonAuthority(sink, _ => spawns++, _ => { }, () => 0);
        for (uint id = 1; id <= 1000; id++) PlaceFromClient(authority, 10, id);
        Assert.Equal(16, spawns);
        PlaceFromClient(authority, 20, 1);
        Assert.Equal(17, spawns);
    }

    [Fact]
    public void SameClientIdFromDifferentPlayersGetsDistinctAuthoritativeIds()
    {
        var sink = new RecordingSink();
        var authority = new PitonAuthority(sink, _ => { }, _ => { });

        PlaceFromClient(authority, playerId: 10, clientPitonId: 1);
        PlaceFromClient(authority, playerId: 20, clientPitonId: 1);

        Assert.Equal(2, sink.Broadcasts.Count);
        var first = Assert.IsType<ServerPitonPlaced>(sink.Broadcasts[0].Packet);
        var second = Assert.IsType<ServerPitonPlaced>(sink.Broadcasts[1].Packet);
        Assert.NotEqual(first.PitonId, second.PitonId);
        Assert.Equal(10, first.FromPlayerId);
        Assert.Equal(20, second.FromPlayerId);
    }

    [Fact]
    public void SnapshotReplaysCommittedPitonsToOnePlayer()
    {
        var sink = new RecordingSink();
        var authority = new PitonAuthority(sink, _ => { }, _ => { });
        PlaceFromClient(authority, playerId: 10, clientPitonId: 7);

        authority.SendSnapshotTo(30);

        var sent = Assert.Single(sink.Sends);
        Assert.Equal(30, sent.TargetPlayerId);
        Assert.Equal(PacketId.ServerPitonPlaced, sent.Id);
        Assert.IsType<ServerPitonPlaced>(sent.Packet);
    }

    private static void PlaceFromClient(PitonAuthority authority, int playerId, uint clientPitonId)
    {
        var packet = new ClientPitonPlaced
        {
            PitonId = clientPitonId,
            PosX = 1f,
            PosY = 2f,
            PosZ = 3f,
            RotW = 1f,
            Quality = 1,
            PitonHp = 100,
            ItemId = 5,
        };

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            packet.Serialize(writer);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        authority.OnClientPacket(playerId, PacketId.ClientPitonPlaced, reader);
    }

    private sealed class RecordingSink : IAuthoritativeSink
    {
        internal readonly List<(int TargetPlayerId, PacketId Id, IPacket Packet)> Sends = new();
        internal readonly List<(PacketId Id, IPacket Packet, int ExceptPlayerId)> Broadcasts = new();

        public IReadOnlyCollection<int> ConnectedPlayerIds => Array.Empty<int>();

        public void SendTo(int playerId, PacketId id, IPacket packet, NetReliability reliability)
            => Sends.Add((playerId, id, packet));

        public void Broadcast(PacketId id, IPacket packet, int exceptPlayerId, NetReliability reliability)
            => Broadcasts.Add((id, packet, exceptPlayerId));
    }
}
