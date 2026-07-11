using System;
using System.IO;
using System.Text;
using CairnMultiplayer.Shared;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public class QuaternionCodecTests
{
    private static (float x, float y, float z, float w) Normalize(float x, float y, float z, float w)
    {
        var len = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
        return (x / len, y / len, z / len, w / len);
    }

    // |dot| close to 1 => same rotation (q and -q are equivalent).
    private static float AbsDot(
        (float x, float y, float z, float w) a,
        (float x, float y, float z, float w) b)
        => Math.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);

    [Theory]
    [InlineData(0f, 0f, 0f, 1f)]   // identity
    [InlineData(1f, 0f, 0f, 0f)]   // 180° around X
    [InlineData(0f, 1f, 0f, 0f)]
    [InlineData(0f, 0f, 1f, 0f)]
    [InlineData(0.5f, 0.5f, 0.5f, 0.5f)] // 4 equal components
    [InlineData(-0.5f, 0.5f, -0.5f, 0.5f)]
    [InlineData(0.7071f, 0.7071f, 0f, 0f)] // two components at 1/√2
    public void Encode_Decode_PreservesRotation(float x, float y, float z, float w)
    {
        var q = Normalize(x, y, z, w);
        var packed = QuaternionCodec.Encode(q.x, q.y, q.z, q.w);
        QuaternionCodec.Decode(packed, out var dx, out var dy, out var dz, out var dw);

        var dot = AbsDot(q, (dx, dy, dz, dw));
        Assert.True(dot > 0.9995f, $"rotation drift too large: |dot|={dot}");
    }

    [Fact]
    public void RandomQuaternions_RoundTripWithinTolerance()
    {
        var rng = new Random(12345);
        float worst = 1f;
        for (int i = 0; i < 5000; i++)
        {
            var q = Normalize(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            var packed = QuaternionCodec.Encode(q.x, q.y, q.z, q.w);
            QuaternionCodec.Decode(packed, out var dx, out var dy, out var dz, out var dw);

            var dot = AbsDot(q, (dx, dy, dz, dw));
            if (dot < worst) worst = dot;
        }

        Assert.True(worst > 0.9995f, $"worst |dot| over 5000 samples = {worst}");
    }

    [Fact]
    public void Decode_OfValidEncoding_ReturnsUnitQuaternion()
    {
        var rng = new Random(987);
        for (int i = 0; i < 1000; i++)
        {
            var q = Normalize(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1));

            var packed = QuaternionCodec.Encode(q.x, q.y, q.z, q.w);
            QuaternionCodec.Decode(packed, out var x, out var y, out var z, out var w);
            var len = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
            Assert.True(Math.Abs(len - 1f) < 1e-3f, $"decoded quaternion not unit: len={len}");
        }
    }

    [Fact]
    public void Pack_Unpack_ThroughBinaryStream()
    {
        var q = Normalize(0.1f, -0.6f, 0.3f, 0.74f);

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            QuaternionCodec.Pack(w, q.x, q.y, q.z, q.w);

        Assert.Equal(QuaternionCodec.PackedSize, ms.Length);

        ms.Position = 0;
        using var r = new BinaryReader(ms);
        QuaternionCodec.Unpack(r, out var dx, out var dy, out var dz, out var dw);

        Assert.True(AbsDot(q, (dx, dy, dz, dw)) > 0.9995f);
    }
}
