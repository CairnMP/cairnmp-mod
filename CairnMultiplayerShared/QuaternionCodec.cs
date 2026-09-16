using System;
using System.IO;

namespace CairnMultiplayer.Shared;

/// <summary>
/// "Smallest three" compression of a unit quaternion into 4 bytes.
///
/// Principle: a unit quaternion (x,y,z,w) satisfies x²+y²+z²+w²=1, so the
/// component with the largest magnitude can be reconstructed from the other
/// three. We store the index of the largest (2 bits) + the 3 other components
/// (10 bits each, range [-1/√2, 1/√2]) => 32 bits.
///
/// q and -q represent the same rotation: we negotiate the sign so that the
/// largest component is always positive, which lets us reconstruct it without
/// storing its sign.
///
/// Lives in Shared (no Unity dependency) to guarantee strictly identical
/// encoding/decoding on the capture side and the apply side, and to stay testable.
/// </summary>
public static class QuaternionCodec
{
    public const int PackedSize = 4;

    private const float Sqrt2Inv = 0.70710678f;
    private const int Bits = 10;
    private const int Mask = (1 << Bits) - 1;

    public static void Pack(BinaryWriter w, float x, float y, float z, float wq)
        => w.Write(Encode(x, y, z, wq));

    public static void Unpack(BinaryReader r, out float x, out float y, out float z, out float wq)
        => Decode(r.ReadUInt32(), out x, out y, out z, out wq);

    public static uint Encode(float x, float y, float z, float wq)
    {
        // Renormalize as a safeguard (local rotations may drift slightly).
        var len = (float)Math.Sqrt(x * x + y * y + z * z + wq * wq);
        if (len < 1e-8f)
        {
            // Degenerate quaternion -> identity.
            x = 0f; y = 0f; z = 0f; wq = 1f;
        }
        else
        {
            var inv = 1f / len;
            x *= inv; y *= inv; z *= inv; wq *= inv;
        }

        Span<float> c = stackalloc float[4] { x, y, z, wq };
        int maxIndex = 0;
        float maxAbs = Math.Abs(c[0]);
        for (int i = 1; i < 4; i++)
        {
            var a = Math.Abs(c[i]);
            if (a > maxAbs) { maxAbs = a; maxIndex = i; }
        }

        // Negotiate the sign so that the max component is positive.
        if (c[maxIndex] < 0f)
            for (int i = 0; i < 4; i++) c[i] = -c[i];

        uint result = (uint)maxIndex << (3 * Bits);
        int shift = 2 * Bits;
        for (int i = 0; i < 4; i++)
        {
            if (i == maxIndex) continue;
            result |= (uint)Quantize(c[i]) << shift;
            shift -= Bits;
        }
        return result;
    }

    public static void Decode(uint packed, out float x, out float y, out float z, out float wq)
    {
        int maxIndex = (int)(packed >> (3 * Bits)) & 0x3;

        Span<float> c = stackalloc float[4];
        float sumSq = 0f;
        int shift = 2 * Bits;
        for (int i = 0; i < 4; i++)
        {
            if (i == maxIndex) continue;
            var value = Dequantize((int)((packed >> shift) & Mask));
            c[i] = value;
            sumSq += value * value;
            shift -= Bits;
        }

        c[maxIndex] = (float)Math.Sqrt(Math.Max(0f, 1f - sumSq));

        x = c[0]; y = c[1]; z = c[2]; wq = c[3];
    }

    private static int Quantize(float value)
    {
        var normalized = value / Sqrt2Inv * 0.5f + 0.5f;
        var q = (int)Math.Round(normalized * Mask);
        return q < 0 ? 0 : (q > Mask ? Mask : q);
    }

    private static float Dequantize(int quantized)
    {
        var normalized = quantized / (float)Mask;
        return (normalized * 2f - 1f) * Sqrt2Inv;
    }
}
