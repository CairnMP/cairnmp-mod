using System.IO;
using System.Text;
using CairnMultiplayer.Shared;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public class ServerTeleportTests
{
    [Fact]
    public void ServerTeleport_RoundTrip()
    {
        var sent = new ServerTeleport { X = -656.6f, Y = -757.5f, Z = -452.0f, Yaw = 137.5f };

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            sent.Serialize(w);
        }
        ms.Position = 0;
        using var r = new BinaryReader(ms);
        var got = new ServerTeleport();
        got.Deserialize(r);

        Assert.Equal(sent.X, got.X);
        Assert.Equal(sent.Y, got.Y);
        Assert.Equal(sent.Z, got.Z);
        Assert.Equal(sent.Yaw, got.Yaw);
    }

    [Fact]
    public void ServerTeleport_PacketId_IsStable()
    {
        Assert.Equal(81, (int)PacketId.ServerTeleport);
    }
}
