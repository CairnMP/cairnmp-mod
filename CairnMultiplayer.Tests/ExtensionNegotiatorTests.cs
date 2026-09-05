#nullable enable

using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayer.Shared.Extensions;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public sealed class ExtensionNegotiatorTests
{
    [Fact]
    public void RequiredExtensionMissingOnClientRejectsConnection()
    {
        var result = ExtensionNegotiator.Negotiate(
            new[] { Entry("com.example.required", "1.2.0", required: true) },
            System.Array.Empty<ExtensionManifestEntry>());

        Assert.False(result.Accepted);
        Assert.Contains("missing on the client", result.Reason);
    }

    [Fact]
    public void OptionalExtensionMissingOnClientIsSimplyDisabled()
    {
        var result = ExtensionNegotiator.Negotiate(
            new[] { Entry("com.example.optional", "1.0.0") },
            System.Array.Empty<ExtensionManifestEntry>());

        Assert.True(result.Accepted);
        Assert.Empty(result.EnabledExtensionIds);
    }

    [Fact]
    public void MutuallyCompatibleExtensionIsEnabled()
    {
        var host = Entry("com.example.shared", "1.4.0", minimum: "1.2.0", maximum: "1.9.0");
        var client = Entry("com.example.shared", "1.6.0", minimum: "1.0.0", maximum: "2.0.0");

        var result = ExtensionNegotiator.Negotiate(new[] { host }, new[] { client });

        Assert.True(result.Accepted);
        Assert.Equal(new[] { "com.example.shared" }, result.EnabledExtensionIds);
    }

    [Fact]
    public void OptionalIncompatibleExtensionIsDisabled()
    {
        var host = Entry("com.example.shared", "2.0.0", maximum: "1.9.0");
        var client = Entry("com.example.shared", "1.5.0", maximum: "1.9.0");

        var result = ExtensionNegotiator.Negotiate(new[] { host }, new[] { client });

        Assert.True(result.Accepted);
        Assert.Empty(result.EnabledExtensionIds);
    }

    [Fact]
    public void ClientRequiredExtensionMissingOnHostRejectsConnection()
    {
        var result = ExtensionNegotiator.Negotiate(
            System.Array.Empty<ExtensionManifestEntry>(),
            new[] { Entry("com.example.client", "1.0.0", required: true) });

        Assert.False(result.Accepted);
        Assert.Contains("missing on the host", result.Reason);
    }

    [Fact]
    public void DuplicateExtensionIdRejectsConnection()
    {
        var duplicate = Entry("com.example.duplicate", "1.0.0");
        var result = ExtensionNegotiator.Negotiate(
            new[] { duplicate, duplicate },
            System.Array.Empty<ExtensionManifestEntry>());

        Assert.False(result.Accepted);
        Assert.Contains("more than once", result.Reason);
    }

    [Fact]
    public void ExtensionPacketsRoundTripOpaquePayloads()
    {
        var packet = new ClientExtensionCommand
        {
            RequestId = 42,
            ExtensionId = "com.example.roundtrip",
            CommandId = "open-door",
            Payload = new byte[] { 1, 2, 3, 4 },
        };

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            packet.Serialize(writer);
        stream.Position = 0;
        var decoded = new ClientExtensionCommand();
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            decoded.Deserialize(reader);

        Assert.Equal(packet.RequestId, decoded.RequestId);
        Assert.Equal(packet.ExtensionId, decoded.ExtensionId);
        Assert.Equal(packet.CommandId, decoded.CommandId);
        Assert.Equal(packet.Payload, decoded.Payload);
    }

    private static ExtensionManifestEntry Entry(
        string id,
        string version,
        bool required = false,
        string minimum = "",
        string maximum = "")
        => new()
        {
            Id = id,
            Version = version,
            MinimumPeerVersion = minimum,
            MaximumPeerVersion = maximum,
            Required = required,
        };
}
