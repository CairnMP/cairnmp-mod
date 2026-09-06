using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Extensions;

namespace CairnMultiplayer.Api;

/// <summary>Managed, host-authoritative integration surface exposed by CairnMP.</summary>
public static class MultiplayerApi
{
    /// <summary>Version of this public C# contract, independent from the network protocol.</summary>
    public const int Version = 1;

    internal static ExtensionRuntime Runtime { get; } = new();

    public static bool IsConnected => Runtime.IsConnected;
    public static bool IsHost => Runtime.IsHost;
    public static MultiplayerPlayer LocalPlayer => Runtime.LocalPlayer;
    public static IReadOnlyList<MultiplayerPlayer> Players => Runtime.Players;

    public static event Action SessionReady
    {
        add => Runtime.SessionReady += value;
        remove => Runtime.SessionReady -= value;
    }

    public static event Action SessionEnded
    {
        add => Runtime.SessionEnded += value;
        remove => Runtime.SessionEnded -= value;
    }

    public static event Action<MultiplayerPlayer> PlayerJoined
    {
        add => Runtime.PlayerJoined += value;
        remove => Runtime.PlayerJoined -= value;
    }

    public static event Action<MultiplayerPlayer> PlayerLeft
    {
        add => Runtime.PlayerLeft += value;
        remove => Runtime.PlayerLeft -= value;
    }

    /// <summary>Raised when the host isolates a repeatedly failing extension for this session.</summary>
    public static event Action<string, string> ExtensionDisabled
    {
        add => Runtime.ExtensionDisabled += value;
        remove => Runtime.ExtensionDisabled -= value;
    }

    /// <summary>Registers one integration. Registration ids are lowercase and globally unique.</summary>
    public static MultiplayerExtension RegisterExtension(ExtensionRegistration registration)
        => Runtime.Register(registration);
}
