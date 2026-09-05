#nullable enable

using System.IO;
using System.Text;
using CairnMultiplayer.Shared;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public class RopeClipTests
{
    [Theory]
    [InlineData(1148887021, true)]
    [InlineData(1047094723, false)]
    public void ClientRopeClip_RoundTrip(int targetId, bool clip)
    {
        var sent = new ClientRopeClip { TargetPlayerId = targetId, Clip = clip };

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            sent.Serialize(w);
        ms.Position = 0;
        using var r = new BinaryReader(ms);
        var got = new ClientRopeClip();
        got.Deserialize(r);

        Assert.Equal(sent.TargetPlayerId, got.TargetPlayerId);
        Assert.Equal(sent.Clip, got.Clip);
    }

    [Theory]
    [InlineData(1148887021, 1047094723, true)]
    [InlineData(1047094723, 1070965505, false)]
    public void ServerRopeClip_RoundTrip(int fromId, int targetId, bool clip)
    {
        var sent = new ServerRopeClip { FromPlayerId = fromId, TargetPlayerId = targetId, Clip = clip };

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            sent.Serialize(w);
        ms.Position = 0;
        using var r = new BinaryReader(ms);
        var got = new ServerRopeClip();
        got.Deserialize(r);

        Assert.Equal(sent.FromPlayerId, got.FromPlayerId);
        Assert.Equal(sent.TargetPlayerId, got.TargetPlayerId);
        Assert.Equal(sent.Clip, got.Clip);
    }

    [Fact]
    public void RopeClip_PacketIds_AreStable()
    {
        Assert.Equal(15, (int)PacketId.ClientRopeClip);
        Assert.Equal(82, (int)PacketId.ServerRopeClip);
    }
}
