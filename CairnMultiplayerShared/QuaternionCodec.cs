using System;
using System.IO;

namespace CairnMultiplayer.Shared;

/// <summary>
/// Compression "smallest three" d'un quaternion unitaire en 4 octets.
///
/// Principe : un quaternion unitaire (x,y,z,w) verifie x²+y²+z²+w²=1, donc le
/// plus grand composant en magnitude peut etre reconstruit a partir des trois
/// autres. On stocke l'index du plus grand (2 bits) + les 3 autres composants
/// (10 bits chacun, plage [-1/√2, 1/√2]) => 32 bits.
///
/// q et -q representent la meme rotation : on negocie le signe pour que le plus
/// grand composant soit toujours positif, ce qui permet de le reconstruire sans
/// stocker son signe.
///
/// Vit dans Shared (sans dependance Unity) pour garantir un encodage/decodage
/// strictement identique cote capture et cote application, et rester testable.
/// </summary>
public static class QuaternionCodec
{
    /// <summary>Taille compressee d'un quaternion, en octets.</summary>
    public const int PackedSize = 4;

    private const float Sqrt2Inv = 0.70710678f; // 1/√2 — borne des composants non-max
    private const int Bits = 10;
    private const int Mask = (1 << Bits) - 1; // 1023

    /// <summary>Ecrit un quaternion compresse (4 octets little-endian).</summary>
    public static void Pack(BinaryWriter w, float x, float y, float z, float wq)
        => w.Write(Encode(x, y, z, wq));

    /// <summary>Lit un quaternion compresse (4 octets little-endian).</summary>
    public static void Unpack(BinaryReader r, out float x, out float y, out float z, out float wq)
        => Decode(r.ReadUInt32(), out x, out y, out z, out wq);

    /// <summary>Encode un quaternion en uint32 "smallest three".</summary>
    public static uint Encode(float x, float y, float z, float wq)
    {
        // Renormalise par securite (les rotations locales peuvent deriver legerement).
        var len = (float)Math.Sqrt(x * x + y * y + z * z + wq * wq);
        if (len < 1e-8f)
        {
            // Quaternion degenere -> identite.
            x = 0f; y = 0f; z = 0f; wq = 1f;
        }
        else
        {
            var inv = 1f / len;
            x *= inv; y *= inv; z *= inv; wq *= inv;
        }

        // Trouve l'index du composant de plus grande magnitude.
        Span<float> c = stackalloc float[4] { x, y, z, wq };
        int maxIndex = 0;
        float maxAbs = Math.Abs(c[0]);
        for (int i = 1; i < 4; i++)
        {
            var a = Math.Abs(c[i]);
            if (a > maxAbs) { maxAbs = a; maxIndex = i; }
        }

        // Negocie le signe pour que le composant max soit positif.
        if (c[maxIndex] < 0f)
            for (int i = 0; i < 4; i++) c[i] = -c[i];

        // Encode les trois composants restants.
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

    /// <summary>Decode un uint32 "smallest three" en quaternion unitaire.</summary>
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

        // Le composant max (positif) reconstruit la norme unitaire.
        c[maxIndex] = (float)Math.Sqrt(Math.Max(0f, 1f - sumSq));

        x = c[0]; y = c[1]; z = c[2]; wq = c[3];
    }

    /// <summary>Quantifie un composant de [-1/√2, 1/√2] vers [0, 1023].</summary>
    private static int Quantize(float value)
    {
        var normalized = value / Sqrt2Inv * 0.5f + 0.5f; // [0,1]
        var q = (int)Math.Round(normalized * Mask);
        return q < 0 ? 0 : (q > Mask ? Mask : q);
    }

    /// <summary>Dequantifie un entier [0, 1023] vers [-1/√2, 1/√2].</summary>
    private static float Dequantize(int quantized)
    {
        var normalized = quantized / (float)Mask; // [0,1]
        return (normalized * 2f - 1f) * Sqrt2Inv;
    }
}
