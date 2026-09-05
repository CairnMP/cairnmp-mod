using CairnMultiplayer.Api;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Extensions;

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

    // ── Session shortcuts, so a feature never reaches through the composition root ──

    /// <summary>Our own player id in the current session.</summary>
    protected int LocalPlayerId => Session.LocalPlayer.Id;

    /// <summary>Our own display name (Steam persona).</summary>
    protected string LocalPlayerName => Session.LocalPlayer.Name;

    protected string GetPlayerName(int playerId)
    {
        foreach (var player in Session.Players)
            if (player.Id == playerId) return player.Name;
        return $"Player{playerId}";
    }

    /// <summary>True when this peer is the authoritative host.</summary>
    protected bool IsHost => Session.IsHost;

    /// <summary>True when a session is established.</summary>
    protected bool IsConnected => Session.IsConnected;

    /// <summary>True while a mod UI (the chat) is consuming key presses. Check it before
    /// reacting to a key, otherwise typing a message triggers your shortcut.</summary>
    protected bool KeyboardCaptured => Game.Input.IsKeyboardCaptured;

    /// <summary>Safe access to Cairn, bound before <see cref="OnRegister"/> runs.</summary>
    protected IGameApi Game { get; private set; } = UnavailableGameApi.Instance;

    protected void LogInfo(string message) => FeatureLog.Info($"[Feature:{Id}] {message}");
    protected void LogWarning(string message) => FeatureLog.Warn($"[Feature:{Id}] {message}");

    /// <summary>Set by the host at registration; tests can supply their own runtime.</summary>
    internal ExtensionRuntime Session { private get; set; } = MultiplayerApi.Runtime;

    internal void BindGame(IGameApi game) => Game = game ?? UnavailableGameApi.Instance;
}
