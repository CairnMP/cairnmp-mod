using System;
using System.Collections.Generic;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;
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
///     protected internal override void OnRegister(FeatureBuilder feature)
///     {
///         _placed = feature.Broadcast&lt;PingPlaced&gt;("placed", ShowRemotePing);
///         feature.EveryFrame(TickInput, FeaturePhase.Always);
///         feature.OnSessionEnded(Game.World.ClearPings);
///     }
/// }
/// </code>
/// </example>
internal abstract class MultiplayerFeature
{
    private readonly List<FeaturePlayer> _players = new();
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


    /// <summary>
    /// The rules this lobby is played under. A feature that behaves differently per mode asks
    /// here rather than testing the mode by name: outside a session, and in solo, it reads as
    /// the default rope-team rules, so there is never a null case to handle.
    /// </summary>
    protected MultiplayerModeRules Rules => _rules();

    private Func<MultiplayerModeRules> _rules = () => MultiplayerModes.RopeTeam;

    internal void BindRules(Func<MultiplayerModeRules> rules)
        => _rules = rules ?? (() => MultiplayerModes.RopeTeam);

    protected int LocalPlayerId => Session.LocalPlayerId;

    protected string LocalPlayerName => Session.LocalPlayer.Name;

    protected string GetPlayerName(int playerId)
    {
        foreach (var player in Session.Players)
            if (player.Id == playerId) return player.Name;
        return $"Player{playerId}";
    }

    protected IReadOnlyList<FeaturePlayer> Players
    {
        get
        {
            _players.Clear();
            foreach (var player in Session.Players)
                _players.Add(new FeaturePlayer(player.Id, player.Name, player.IsLocal, player.IsHost));
            return _players;
        }
    }

    protected bool IsHost => Session.IsHost;

    protected bool IsConnected => Session.IsConnected;

    /// <summary>True while a mod UI (the chat) is consuming key presses. Check it before
    /// reacting to a key, otherwise typing a message triggers your shortcut.</summary>
    protected bool KeyboardCaptured => Game.Input.IsKeyboardCaptured;

    protected IGameApi Game { get; private set; } = UnavailableGameApi.Instance;

    protected void LogInfo(string message) => FeatureLog.Info($"[Feature:{Id}] {message}");
    protected void LogWarning(string message) => FeatureLog.Warn($"[Feature:{Id}] {message}");

    internal ExtensionRuntime Session { private get; set; } = MultiplayerApi.Runtime;

    internal void BindGame(IGameApi game) => Game = game ?? UnavailableGameApi.Instance;
}

internal readonly struct FeaturePlayer
{
    internal FeaturePlayer(int id, string name, bool isLocal, bool isHost)
    { Id = id; Name = name ?? ""; IsLocal = isLocal; IsHost = isHost; }
    internal int Id { get; }
    internal string Name { get; }
    internal bool IsLocal { get; }
    internal bool IsHost { get; }
}
