#nullable enable

using System.IO;
using System.Text;
using CairnMultiplayer.Shared;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public class HandshakeAdmissionTests
{
    [Fact]
    public void AMatchingProtocolIsAdmitted()
    {
        Assert.True(HandshakeAdmission.Accepts(19, 19));
        Assert.False(HandshakeAdmission.Accepts(18, 19));
    }

    [Fact]
    public void AMismatchNamesBothProtocols()
    {
        var reason = HandshakeAdmission.RejectReason(18, 19);
        Assert.Contains("protocol 19", reason, System.StringComparison.Ordinal);
        Assert.Contains("protocol 18", reason, System.StringComparison.Ordinal);
        Assert.Contains("Update the mod", reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AProtocol18ManifestWithNoEntriesIsRejected()
    {
        AssertRejected(ManifestWithoutAVersion());
    }

    [Fact]
    public void AProtocol18ManifestWithEntriesIsRejected()
    {
        AssertRejected(ManifestWithoutAVersion(
            new ExtensionManifestEntry { Id = "cairn.rope", Version = "1", Required = true }));
        AssertRejected(ManifestWithoutAVersion(
            new ExtensionManifestEntry
            {
                Id = "cairn.rope",
                Version = "1",
                MinimumPeerVersion = "1",
                MaximumPeerVersion = "2",
                Required = false,
            },
            new ExtensionManifestEntry { Id = "cairn.voice", Version = "3", Required = true }));
    }

    /// <summary>
    /// Protocol 18, and 17 before it, wrote the entry count and then the entries.
    /// They did not write a version. Protocol 19 writes the version first.
    /// </summary>
    private static byte[] ManifestWithoutAVersion(params ExtensionManifestEntry[] entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)entries.Length);
            foreach (var entry in entries)
                entry.Serialize(writer);
        }

        return stream.ToArray();
    }

    private static void AssertRejected(byte[] payload)
    {
        using var reader = new BinaryReader(new MemoryStream(payload));
        var admitted = HandshakeAdmission.TryReadManifest(
            reader, out var manifest, out var reason, out _);

        Assert.False(admitted);
        Assert.Contains("protocol 19", reason, StringComparison.Ordinal);
        Assert.Contains("did not send a compatible handshake", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("speaks protocol", reason, StringComparison.Ordinal);
        Assert.Contains("Update the mod", reason, StringComparison.Ordinal);
        Assert.Equal(0, manifest.ProtocolVersion);
        Assert.Null(manifest.Entries);
    }

    [Fact]
    public void AManifestCarrying17NamesProtocol17()
    {
        var reason = ReasonForVersion(17);
        Assert.Contains("speaks protocol 17", reason, StringComparison.Ordinal);
        Assert.Contains("protocol 19", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestCarrying18NamesProtocol18()
    {
        var reason = ReasonForVersion(18);
        Assert.Contains("speaks protocol 18", reason, StringComparison.Ordinal);
        Assert.Contains("protocol 19", reason, StringComparison.Ordinal);
    }

    private static string ReasonForVersion(int version)
    {
        var sent = new ClientExtensionManifest
        {
            ProtocolVersion = version,
            Entries = System.Array.Empty<ExtensionManifestEntry>(),
        };
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            sent.Serialize(writer);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var admitted = HandshakeAdmission.TryReadManifest(reader, out _, out var reason, out _);
        Assert.False(admitted);
        return reason;
    }

    [Fact]
    public void TheCurrentManifestRoundTrips()
    {
        var sent = new ClientExtensionManifest
        {
            ProtocolVersion = Protocol.Version,
            Entries = new[]
            {
                new ExtensionManifestEntry { Id = "cairn.rope", Version = "1", Required = false },
            },
        };
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            sent.Serialize(writer);
        ms.Position = 0;
        using var reader = new BinaryReader(ms);
        var got = new ClientExtensionManifest();
        got.Deserialize(reader);
        Assert.Equal(Protocol.Version, got.ProtocolVersion);
        Assert.NotNull(got.Entries);
        Assert.Equal("cairn.rope", got.Entries![0].Id);
    }
}
