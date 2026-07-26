using System;
using System.Collections.Generic;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Api.Internal;

namespace CairnMultiplayerMod.Framework;

/// <summary>When a per-frame callback runs, relative to the bivouac gate.</summary>
internal enum FeaturePhase
{
    /// <summary>Every frame, including in a bivouac, in the menu and while loading. For input
    /// handling, HUD upkeep and anything that must never freeze.</summary>
    Always,

    /// <summary>Only while gameplay sync is active. For anything that reads or writes the
    /// world — during a bivouac the game drives the pawn itself and we stay out of the way.</summary>
    Gameplay,
}

/// <summary>
/// Everything a feature is allowed to do, in one place. A contributor writing a feature only
/// needs to explore this class: declare what travels over the network, what runs each frame,
/// and what to clean up. The plumbing underneath is not their problem.
/// </summary>
internal sealed class FeatureBuilder
{
    private readonly ExtensionRuntime _runtime;
    private readonly MultiplayerExtension _extension;
    private readonly string _featureId;
    private readonly List<(FeaturePhase Phase, Action Tick)> _ticks = new();
    private readonly List<Action> _onSessionStarted = new();
    private readonly List<Action> _onSessionEnded = new();
    private readonly List<Action> _onSceneReset = new();
    private readonly List<Action> _onDrawHud = new();

    internal FeatureBuilder(ExtensionRuntime runtime, MultiplayerExtension extension, string featureId)
    {
        _runtime = runtime;
        _extension = extension;
        _featureId = featureId;
    }

    internal IReadOnlyList<(FeaturePhase Phase, Action Tick)> Ticks => _ticks;
    internal IReadOnlyList<Action> SessionStartedHandlers => _onSessionStarted;
    internal IReadOnlyList<Action> SessionEndedHandlers => _onSessionEnded;
    internal IReadOnlyList<Action> SceneResetHandlers => _onSceneReset;
    internal IReadOnlyList<Action> DrawHudHandlers => _onDrawHud;

    // ── Network ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Declares a message every other player receives (ping, chat line...). Any player may
    /// send; the host relays. <paramref name="onReceived"/> gets the sender's id and never
    /// fires on the sender itself, so show your own effect locally when you send.
    /// </summary>
    public Broadcast<T> Broadcast<T>(string id, Action<int, T> onReceived) where T : IPacket, new()
    {
        if (onReceived == null) throw new ArgumentNullException(nameof(onReceived));

        var codec = FeatureCodec.For<T>();
        var relayed = _extension.RegisterEvent($"{id}.relayed", codec);
        relayed.Received += message =>
        {
            // The host commits its own send locally, clients do not — filtering here keeps
            // both sides behaving identically.
            if (message.SourcePlayerId == _runtime.LocalPlayer.Id) return;
            onReceived(message.SourcePlayerId, message.Payload);
        };

        var request = _extension.RegisterCommand<T>(id,
            context => context.Broadcast(relayed, context.Request), codec);

        return new Broadcast<T>(_runtime, request, $"{_featureId}.{id}");
    }

    /// <summary>
    /// Declares a value owned by the host and replayed to players who join later (weather,
    /// time of day, placed pitons...). Only the host may publish it.
    /// </summary>
    public HostState<T> HostState<T>(string id, Action<T> onChanged) where T : IPacket, new()
    {
        if (onChanged == null) throw new ArgumentNullException(nameof(onChanged));

        var state = _extension.RegisterState(id, FeatureCodec.For<T>());
        state.Changed += change =>
        {
            if (change.Removed) return;
            onChanged(change.Value);
        };

        return new HostState<T>(_runtime, _extension, state, $"{_featureId}.{id}");
    }

    /// <summary>
    /// Declares a request the host arbitrates: <paramref name="handler"/> runs on the host
    /// only and may reject. Use it whenever a client must not decide the outcome alone.
    /// </summary>
    public HostCommand<T> HostCommand<T>(string id, Action<HostRequest<T>> handler) where T : IPacket, new()
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        var command = _extension.RegisterCommand<T>(id,
            context => handler(new HostRequest<T>(context)), FeatureCodec.For<T>());

        return new HostCommand<T>(_runtime, command, $"{_featureId}.{id}");
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Runs <paramref name="tick"/> every frame during the given phase. Exceptions are
    /// caught and logged: one broken feature never stops the others.</summary>
    public void EveryFrame(Action tick, FeaturePhase phase = FeaturePhase.Gameplay)
    {
        if (tick == null) throw new ArgumentNullException(nameof(tick));
        _ticks.Add((phase, tick));
    }

    /// <summary>Runs once the session is established (handshake complete).</summary>
    public void OnSessionStarted(Action handler)
        => _onSessionStarted.Add(handler ?? throw new ArgumentNullException(nameof(handler)));

    /// <summary>Runs when the session ends (disconnect, leaving the lobby). Clean up here —
    /// anything tied to the session must not survive it.</summary>
    public void OnSessionEnded(Action handler)
        => _onSessionEnded.Add(handler ?? throw new ArgumentNullException(nameof(handler)));

    /// <summary>Runs when the scene changes. Drop any cached reference to a game object: the
    /// game destroys and recreates them, and stale pointers crash under IL2CPP.</summary>
    public void OnSceneReset(Action handler)
        => _onSceneReset.Add(handler ?? throw new ArgumentNullException(nameof(handler)));

    /// <summary>Draws on the screen (IMGUI, from OnGUI). Runs several times per frame — check
    /// <c>Event.current.type</c> and keep it cheap.</summary>
    public void OnDrawHud(Action handler)
        => _onDrawHud.Add(handler ?? throw new ArgumentNullException(nameof(handler)));
}
