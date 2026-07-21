using System;
using System.Text.Json;

namespace CairnMultiplayer.Api;

/// <summary>
/// Serialization contract for an extension payload. JSON is provided by default; integrations
/// may supply a compact or specially versioned codec without exposing network packet details.
/// </summary>
public sealed class PayloadCodec<T>
{
    public PayloadCodec(Func<T, byte[]> serialize, Func<byte[], T> deserialize)
    {
        Serialize = serialize ?? throw new ArgumentNullException(nameof(serialize));
        Deserialize = deserialize ?? throw new ArgumentNullException(nameof(deserialize));
    }

    public Func<T, byte[]> Serialize { get; }
    public Func<byte[], T> Deserialize { get; }

    public static PayloadCodec<T> Json { get; } = new(
        value => JsonSerializer.SerializeToUtf8Bytes(value),
        bytes => JsonSerializer.Deserialize<T>(bytes));
}
