using System.IO;
using System.Text;
using CairnMultiplayer.Shared;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public class PacketCodecTests
{
    [Theory]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("héllo wörld 🌍")]
    [InlineData("a string with\nnewlines\tand tabs")]
    public void WriteString_ReadString_RoundTrip(string value)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            PacketCodec.WriteString(w, value);
        }
        ms.Position = 0;
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
        var got = PacketCodec.ReadString(r);
        Assert.Equal(value, got);
    }

    [Fact]
    public void WriteString_NullIsTreatedAsEmpty()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            PacketCodec.WriteString(w, null);
        }
        ms.Position = 0;
        using var r = new BinaryReader(ms);
        Assert.Equal("", PacketCodec.ReadString(r));
    }

    [Fact]
    public void WriteString_UsesLittleEndianUInt16Prefix()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            PacketCodec.WriteString(w, "ab"); // 2 bytes UTF-8
        }
        var bytes = ms.ToArray();
        // 2 little-endian length bytes + payload
        Assert.Equal(4, bytes.Length);
        Assert.Equal(0x02, bytes[0]);
        Assert.Equal(0x00, bytes[1]);
        Assert.Equal((byte)'a', bytes[2]);
        Assert.Equal((byte)'b', bytes[3]);
    }

    [Fact]
    public void WriteString_RejectsOversizedPayload()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        Assert.Throws<InvalidDataException>(() => PacketCodec.WriteString(w, new string('x', ushort.MaxValue + 1)));
    }

    [Fact]
    public void Frame_RejectsOversizedPayload()
    {
        var packet = new ClientChat { Message = new string('x', ushort.MaxValue) };
        Assert.Throws<InvalidDataException>(() => PacketCodec.Frame(PacketId.ClientChat, packet));
    }
}

public class ProtocolVersionTests
{
    // Safeguard: if someone bumps the version by mistake without changing the format,
    // this test is a reminder that the change must also be documented in the mod and the launcher.
    [Fact]
    public void Version_IsKnownConstant()
    {
        // When you bump Protocol.Version, update this value AND the release notes
        // to signal to clients that they need to update.
        Assert.Equal(7, Protocol.Version);
    }

    [Fact]
    public void DefaultPort_InEphemeralRange()
    {
        Assert.InRange(Protocol.DefaultPort, 1024, 65535);
    }

    [Theory]
    [InlineData(GameDifficulty.Alpinist, 1769420573)]
    [InlineData(GameDifficulty.Explorer, 766328718)]
    [InlineData(GameDifficulty.FreeSolo, -1944667143)]
    [InlineData(GameDifficulty.FreeRoam, 418187680)]
    public void GameDifficulty_ValuesAreStable(GameDifficulty d, int expected)
    {
        // These values come from the Cairn game — if they change, existing packets
        // become unreadable. Safeguard against an accidental refactor.
        Assert.Equal(expected, (int)d);
    }
}

public class PingPacketTests
{
    [Fact]
    public void ClientPingPlaced_RoundTrips()
    {
        var pkt = new ClientPingPlaced { PosX = 12.5f, PosY = -3.25f, PosZ = 1024.75f };

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            pkt.Serialize(w);

        ms.Position = 0;
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
        var got = new ClientPingPlaced();
        got.Deserialize(r);

        Assert.Equal(pkt.PosX, got.PosX);
        Assert.Equal(pkt.PosY, got.PosY);
        Assert.Equal(pkt.PosZ, got.PosZ);
    }

    [Fact]
    public void ServerPingPlaced_RoundTrips()
    {
        var pkt = new ServerPingPlaced
        {
            FromPlayerId = 7,
            PosX = -100.5f,
            PosY = 64f,
            PosZ = 0.125f,
        };

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            pkt.Serialize(w);

        ms.Position = 0;
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
        var got = new ServerPingPlaced();
        got.Deserialize(r);

        Assert.Equal(pkt.FromPlayerId, got.FromPlayerId);
        Assert.Equal(pkt.PosX, got.PosX);
        Assert.Equal(pkt.PosY, got.PosY);
        Assert.Equal(pkt.PosZ, got.PosZ);
    }

    [Fact]
    public void ClientPingPlaced_FramesThroughCodec()
    {
        // Verifies the full encoding (length + PacketId + fields) via PacketCodec.Frame.
        var pkt = new ClientPingPlaced { PosX = 1f, PosY = 2f, PosZ = 3f };
        var frame = PacketCodec.Frame(PacketId.ClientPingPlaced, pkt);

        // [uint16 len][byte id][3 floats] => 2 + 1 + 12 = 15 bytes, payload = 13.
        Assert.Equal(15, frame.Length);
        Assert.Equal((byte)PacketId.ClientPingPlaced, frame[2]);
    }
}

public class HandPosePacketTests
{
    private static byte[] SamplePacked()
    {
        var bytes = new byte[Protocol.HandPosePackedSize];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 7 + 3);
        return bytes;
    }

    [Fact]
    public void ClientHandPose_RoundTrips()
    {
        var pkt = new ClientHandPose { Packed = SamplePacked() };

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            pkt.Serialize(w);

        ms.Position = 0;
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
        var got = new ClientHandPose();
        got.Deserialize(r);

        Assert.Equal(pkt.Packed, got.Packed);
    }

    [Fact]
    public void ServerHandPose_RoundTrips()
    {
        var pkt = new ServerHandPose { PlayerId = 42, Packed = SamplePacked() };

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            pkt.Serialize(w);

        ms.Position = 0;
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
        var got = new ServerHandPose();
        got.Deserialize(r);

        Assert.Equal(pkt.PlayerId, got.PlayerId);
        Assert.Equal(pkt.Packed, got.Packed);
    }

    [Fact]
    public void HandPosePackedSize_MatchesFingerBoneCount()
    {
        // 38 finger bones (Aava skeleton, resolved by name) x 4 bytes smallest-three = 152.
        Assert.Equal(38, Protocol.FingerBoneCount);
        Assert.Equal(Protocol.FingerBoneCount * QuaternionCodec.PackedSize, Protocol.HandPosePackedSize);
        Assert.Equal(152, Protocol.HandPosePackedSize);
    }

    [Fact]
    public void ClientHandPose_NullPacked_SerializesAsEmpty()
    {
        var pkt = new ClientHandPose { Packed = null };

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            pkt.Serialize(w);

        ms.Position = 0;
        using var r = new BinaryReader(ms);
        var got = new ClientHandPose();
        got.Deserialize(r);

        Assert.Empty(got.Packed);
    }
}

public class TimeSyncPacketTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClientSleepState_RoundTrips(bool asleep)
    {
        var pkt = new ClientSleepState { Asleep = asleep };

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            pkt.Serialize(w);

        ms.Position = 0;
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
        var got = new ClientSleepState();
        got.Deserialize(r);

        Assert.Equal(asleep, got.Asleep);
    }

    [Fact]
    public void ServerTimeState_RoundTrips()
    {
        var pkt = new ServerTimeState { DayTime01 = 0.4275f, AllAsleep = true };

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            pkt.Serialize(w);

        ms.Position = 0;
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
        var got = new ServerTimeState();
        got.Deserialize(r);

        Assert.Equal(pkt.DayTime01, got.DayTime01);
        Assert.Equal(pkt.AllAsleep, got.AllAsleep);
    }
}

public class WeatherSyncDataTests
{
    [Fact]
    public void WeatherSyncData_RoundTrips()
    {
        var state = new WeatherSyncData
        {
            IsValid = true,
            WeatherType = 3,
            RainType = 2,
            ThunderType = 1,
            FogType = 1,
            CloudsType = 2,
            WindType = 2,
            WindOverride = 3,
            SnowRainForceMode = 1,
            RemainingDuration = 42.5f,
            UseSnowInsteadOfRain01 = 0.75f,
            WindForce = 12.25f,
            WindForce01 = 0.5f,
            WindDirX = 1f,
            WindDirY = 0f,
            WindDirZ = -1f,
            WindAngle = 180f,
        };

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            state.Serialize(w);

        ms.Position = 0;
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
        var got = new WeatherSyncData();
        got.Deserialize(r);

        Assert.True(got.IsValid);
        Assert.Equal(state.WeatherType, got.WeatherType);
        Assert.Equal(state.RainType, got.RainType);
        Assert.Equal(state.ThunderType, got.ThunderType);
        Assert.Equal(state.FogType, got.FogType);
        Assert.Equal(state.CloudsType, got.CloudsType);
        Assert.Equal(state.WindType, got.WindType);
        Assert.Equal(state.WindOverride, got.WindOverride);
        Assert.Equal(state.SnowRainForceMode, got.SnowRainForceMode);
        Assert.Equal(state.RemainingDuration, got.RemainingDuration);
        Assert.Equal(state.UseSnowInsteadOfRain01, got.UseSnowInsteadOfRain01);
        Assert.Equal(state.WindForce, got.WindForce);
        Assert.Equal(state.WindForce01, got.WindForce01);
        Assert.Equal(state.WindDirX, got.WindDirX);
        Assert.Equal(state.WindDirY, got.WindDirY);
        Assert.Equal(state.WindDirZ, got.WindDirZ);
        Assert.Equal(state.WindAngle, got.WindAngle);
    }
}

public class NetFrameDataTests
{
    [Fact]
    public void NetFrameData_RoundTrips()
    {
        var frame = new NetFrameData
        {
            IsValid = true,
            Flags = 7,
            Positions = new[] { 1f, 2f, 3f, 4f, 5f, 6f },
            Eulers = new[] { 10f, 20f, 30f },
        };

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            frame.Serialize(w);

        ms.Position = 0;
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
        var got = new NetFrameData();
        got.Deserialize(r);

        Assert.True(got.IsValid);
        Assert.Equal(7, got.Flags);
        Assert.Equal(frame.Positions, got.Positions);
        Assert.Equal(frame.Eulers, got.Eulers);
    }

    [Fact]
    public void NetFrameData_RejectsMalformedVectorArray()
    {
        var frame = new NetFrameData
        {
            IsValid = true,
            Flags = 1,
            Positions = new[] { 1f, 2f },
            Eulers = new[] { 0f, 0f, 0f },
        };

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        Assert.Throws<InvalidDataException>(() => frame.Serialize(w));
    }
}
