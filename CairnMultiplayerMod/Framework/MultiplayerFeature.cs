using CairnMultiplayer.Api;

namespace CairnMultiplayerMod.Framework;

/// <summary>
/// Base class for a multiplayer feature. Inherit it, give it an id, declare what you need in
/// <see cref="OnRegister"/> — that is the whole contract. The feature is picked up
/// automatically at build time; nothing else in the codebase has to be edited.
/// </summary>
/// <example>
/// <code>
/// internal sealed class PingFeature : MultiplayerFeature
/// {
///     public override string Id => "ping";
///
///     private Broadcast&lt;PingPlaced&gt; _placed;
///
///     protected override void OnRegister(FeatureBuilder feature)
///     {
///         _placed = feature.Broadcast&lt;PingPlaced&gt;("placed", ShowRemotePing);
///         feature.EveryFrame(TickInput, FeaturePhase.Always);
///         feature.OnSessionEnded(PingMarkerManager.ClearAll);
///     }
/// }
/// </code>
/// </example>
internal abstract class MultiplayerFeature
{
    /// <summary>
    /// Stable identifier, lowercase (letters, digits, '.', '-', '_'). It names the feature's
    /// messages on the wire, so renaming it breaks compatibility with older clients.
    /// </summary>
    public abstract string Id { get; }

    /// <summary>
    /// Declares what the feature needs: its network channels, its per-frame work and its
    /// cleanup. Called once at startup, before any session exists — do not touch the game
    /// here, only declare.
    /// </summary>
    protected internal abstract void OnRegister(FeatureBuilder feature);

    // ── Session shortcuts, so a feature never has to reach through Mod.Instance ──

    /// <summary>Our own player id in the current session.</summary>
    protected int LocalPlayerId => Session.LocalPlayer.Id;

    /// <summary>Our own display name (Steam persona).</summary>
    protected string LocalPlayerName => Session.LocalPlayer.Name;

    /// <summary>True when this peer is the authoritative host.</summary>
    protected bool IsHost => Session.IsHost;

    /// <summary>True when a session is established.</summary>
    protected bool IsConnected => Session.IsConnected;

    /// <summary>True while a mod UI (the chat) is consuming key presses. Check it before
    /// reacting to a key, otherwise typing a message triggers your shortcut.</summary>
    protected static bool KeyboardCaptured => FeatureInput.KeyboardCaptured;

    /// <summary>Set by the host at registration; tests can supply their own runtime.</summary>
    internal Api.Internal.ExtensionRuntime Session { private get; set; } = MultiplayerApi.Runtime;
}
