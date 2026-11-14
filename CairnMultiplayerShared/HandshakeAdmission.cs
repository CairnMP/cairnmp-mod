using System;
using System.IO;

namespace CairnMultiplayer.Shared;

/// <summary>
/// The extension manifest is the handshake. A peer on another protocol is rejected
/// before they are admitted, so they never become a half-connected player.
/// </summary>
public static class HandshakeAdmission
{
    public static bool Accepts(int clientProtocol, int hostProtocol)
        => clientProtocol == hostProtocol;

    public static string RejectReason(int clientProtocol, int hostProtocol)
        => $"CairnMP protocol {hostProtocol} is required. This client speaks protocol {clientProtocol}. Update the mod before joining.";

    public static string Unreadable(int hostProtocol)
        => $"CairnMP protocol {hostProtocol} is required. This client did not send a compatible handshake. Update the mod before joining.";

    /// <summary>
    /// Reads the manifest the host admits on. A protocol-18 payload has no version
    /// field: it starts with the entry count, the same layout protocol 17 used.
    /// That read is a rejection, and the manifest handed back is empty so the
    /// caller cannot admit it.
    /// </summary>
    public static bool TryReadManifest(
        BinaryReader reader,
        out ClientExtensionManifest manifest,
        out string rejectReason,
        out Exception? error)
    {
        manifest = default;
        rejectReason = Unreadable(Protocol.Version);
        error = null;
        try
        {
            manifest = new ClientExtensionManifest();
            manifest.Deserialize(reader);
        }
        catch (Exception exception)
        {
            error = exception;
            manifest = default;
            return false;
        }

        if (Accepts(manifest.ProtocolVersion, Protocol.Version))
            return true;

        rejectReason = OfferedReason(manifest.ProtocolVersion);
        manifest = default;
        return false;
    }

    /// <summary>
    /// A real older manifest is misread as some large integer, not as protocol 18.
    /// Quoting that integer is noise. A nearby protocol number is still named.
    /// </summary>
    private static string OfferedReason(int offered)
        => offered > 0 && offered < 1000
            ? RejectReason(offered, Protocol.Version)
            : Unreadable(Protocol.Version);
}
