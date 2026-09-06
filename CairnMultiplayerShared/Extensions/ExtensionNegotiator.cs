using System;
using System.Collections.Generic;
using System.Linq;

namespace CairnMultiplayer.Shared.Extensions;

/// <summary>Result of comparing a host and client extension inventory.</summary>
public sealed class ExtensionNegotiationResult
{
    private ExtensionNegotiationResult(bool accepted, string reason, IReadOnlyList<string> enabledExtensionIds)
    {
        Accepted = accepted;
        Reason = reason ?? "";
        EnabledExtensionIds = enabledExtensionIds ?? Array.Empty<string>();
    }

    public bool Accepted { get; }
    public string Reason { get; }
    public IReadOnlyList<string> EnabledExtensionIds { get; }

    public static ExtensionNegotiationResult Accept(IEnumerable<string> enabledIds)
        => new(true, "", enabledIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());

    public static ExtensionNegotiationResult Reject(string reason)
        => new(false, reason, Array.Empty<string>());
}

/// <summary>
/// Pure, transport-independent compatibility negotiation for the managed multiplayer API.
/// Required extensions reject a peer when absent or incompatible. Optional extensions are
/// enabled only when both sides advertise mutually compatible versions.
/// </summary>
public static class ExtensionNegotiator
{
    public static ExtensionNegotiationResult Negotiate(
        IReadOnlyList<ExtensionManifestEntry> hostEntries,
        IReadOnlyList<ExtensionManifestEntry> clientEntries)
    {
        if (!TryIndex(hostEntries, "host", out var host, out var error))
            return ExtensionNegotiationResult.Reject(error);
        if (!TryIndex(clientEntries, "client", out var client, out error))
            return ExtensionNegotiationResult.Reject(error);

        var enabled = new List<string>();
        foreach (var pair in host)
        {
            var hostEntry = pair.Value;
            if (!client.TryGetValue(pair.Key, out var clientEntry))
            {
                if (hostEntry.Required)
                    return ExtensionNegotiationResult.Reject(
                        $"Required extension '{pair.Key}' is missing on the client.");
                continue;
            }

            if (!AreMutuallyCompatible(hostEntry, clientEntry, out var incompatibility))
            {
                if (hostEntry.Required || clientEntry.Required)
                    return ExtensionNegotiationResult.Reject(incompatibility);
                continue;
            }

            enabled.Add(pair.Key);
        }

        foreach (var pair in client)
        {
            if (pair.Value.Required && !host.ContainsKey(pair.Key))
            {
                return ExtensionNegotiationResult.Reject(
                    $"Client-required extension '{pair.Key}' is missing on the host.");
            }
        }

        return ExtensionNegotiationResult.Accept(enabled);
    }

    public static bool IsValidExtensionId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length < 3 || id.Length > 64)
            return false;
        if (!IsLowerAlphaNumeric(id[0]))
            return false;

        for (int i = 1; i < id.Length; i++)
        {
            char c = id[i];
            if (!IsLowerAlphaNumeric(c) && c != '.' && c != '-' && c != '_')
                return false;
        }

        return true;
    }

    private static bool TryIndex(
        IReadOnlyList<ExtensionManifestEntry> entries,
        string owner,
        out Dictionary<string, ExtensionManifestEntry> index,
        out string error)
    {
        index = new Dictionary<string, ExtensionManifestEntry>(StringComparer.Ordinal);
        error = "";
        entries ??= Array.Empty<ExtensionManifestEntry>();
        if (entries.Count > ClientExtensionManifest.MaxEntries)
        {
            error = $"The {owner} advertised too many extensions ({entries.Count}).";
            return false;
        }

        foreach (var entry in entries)
        {
            if (!IsValidExtensionId(entry.Id))
            {
                error = $"The {owner} advertised an invalid extension id '{entry.Id}'.";
                return false;
            }
            if (!Version.TryParse(entry.Version, out _))
            {
                error = $"Extension '{entry.Id}' advertised an invalid version '{entry.Version}'.";
                return false;
            }
            if (!TryParseBound(entry.MinimumPeerVersion, out _) ||
                !TryParseBound(entry.MaximumPeerVersion, out _))
            {
                error = $"Extension '{entry.Id}' advertised an invalid compatibility range.";
                return false;
            }
            if (!index.TryAdd(entry.Id, entry))
            {
                error = $"The {owner} advertised extension '{entry.Id}' more than once.";
                return false;
            }
        }

        return true;
    }

    private static bool AreMutuallyCompatible(
        ExtensionManifestEntry host,
        ExtensionManifestEntry client,
        out string reason)
    {
        var hostVersion = Version.Parse(host.Version);
        var clientVersion = Version.Parse(client.Version);

        if (!Accepts(host, clientVersion) || !Accepts(client, hostVersion))
        {
            reason = $"Extension '{host.Id}' is incompatible: host {host.Version}, client {client.Version}.";
            return false;
        }

        reason = "";
        return true;
    }

    private static bool Accepts(ExtensionManifestEntry consumer, Version peerVersion)
    {
        TryParseBound(consumer.MinimumPeerVersion, out var minimum);
        TryParseBound(consumer.MaximumPeerVersion, out var maximum);
        return (minimum == null || peerVersion >= minimum) &&
               (maximum == null || peerVersion <= maximum);
    }

    private static bool TryParseBound(string value, out Version? version)
    {
        version = null;
        return string.IsNullOrWhiteSpace(value) || Version.TryParse(value, out version);
    }

    private static bool IsLowerAlphaNumeric(char c)
        => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
}
