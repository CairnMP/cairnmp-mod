using System;
using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Extensions;
using CairnMultiplayerMod.Internal.Networking;

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
    private readonly FeatureStreamRouter _streams;
    private readonly Func<NetworkManager> _network;
    private readonly string _featureId;
    private readonly List<(FeaturePhase Phase, Action Tick)> _ticks = new();
    private readonly List<Action> _onSessionStarted = new();
    private readonly List<Action> _onSessionEnded = new();
    private readonly List<Action> _onSceneReset = new();
    private readonly List<Action> _onDrawHud = new();
    private readonly List<IGameRegistration> _gameRegistrations = new();
    private readonly List<Action> _unsubscribe = new();

    internal FeatureBuilder(ExtensionRuntime runtime, MultiplayerExtension extension,
        FeatureStreamRouter streams, Func<NetworkManager> network, IGameApi game, string featureId)
    {
        _runtime = runtime;
        _extension = extension;
        _streams = streams;
        _network = network;
        _featureId = featureId;
        Game = new FeatureGameApi(game ?? throw new ArgumentNullException(nameof(game)), Own, featureId);
    }

    /// <summary>
    /// Safe access to Cairn. This façade contains no Unity, IL2CPP, Steam or Harmony type;
    /// scene-bound objects and retries are owned by its internal adapters.
    /// </summary>
    public IGameApi Game { get; }

    internal void Dispose()
    {
        foreach (var unsubscribe in _unsubscribe) unsubscribe();
        _unsubscribe.Clear();
        for (var i = _gameRegistrations.Count - 1; i >= 0; i--)
        {
            try
            {
                _gameRegistrations[i].Dispose();
            }
            catch (Exception ex)
            {
                FeatureLog.Error($"[Feature:{_featureId}] game registration cleanup failed: {ex}");
            }
        }
        _gameRegistrations.Clear();
    }

    private IGameRegistration Own(IGameRegistration registration)
    {
        if (registration == null)
            throw new InvalidOperationException("A game API returned a null registration.");
        _gameRegistrations.Add(registration);
        return registration;
    }

    private sealed class FeatureGameApi : IGameApi
    {
        internal FeatureGameApi(IGameApi inner, Func<IGameRegistration, IGameRegistration> own,
            string featureId)
        {
            MainMenu = new FeatureMainMenuApi(inner.MainMenu, own, featureId);
            Hud = new FeatureHudApi(inner.Hud, featureId);
            Chat = new FeatureChatApi(inner.Chat, own);
            Inventory = new FeatureInventoryApi(inner.Inventory, own);
            State = inner.State;
            Time = inner.Time;
            Input = inner.Input;
            Clock = inner.Clock;
            Players = inner.Players;
            Weather = inner.Weather;
            World = inner.World;
            Voice = inner.Voice;
        }

        public IMainMenuApi MainMenu { get; }
        public IGameStateApi State { get; }
        public IGameTimeApi Time { get; }
        public IGameInputApi Input { get; }
        public IGameHudApi Hud { get; }
        public IChatApi Chat { get; }
        public IInventoryApi Inventory { get; }
        public IClockApi Clock { get; }
        public IPlayersApi Players { get; }
        public IWeatherApi Weather { get; }
        public IWorldApi World { get; }
        public IVoiceApi Voice { get; }
    }

    private sealed class FeatureMainMenuApi : IMainMenuApi
    {
        private readonly IMainMenuApi _inner;
        private readonly Func<IGameRegistration, IGameRegistration> _own;

        internal FeatureMainMenuApi(IMainMenuApi inner,
            Func<IGameRegistration, IGameRegistration> own, string featureId)
        {
            _inner = inner;
            _own = own;
            _featureId = featureId;
        }

        private readonly string _featureId;

        public IGameRegistration AddButton(string id, string label, Action onClick)
            => _own(_inner.AddButton($"{_featureId}.{id}", label, onClick));
    }

    private sealed class FeatureHudApi : IGameHudApi
    {
        private readonly IGameHudApi _inner;
        private readonly string _featureId;

        internal FeatureHudApi(IGameHudApi inner, string featureId)
        {
            _inner = inner;
            _featureId = featureId;
        }

        public void ShowMessage(string id, string text, float durationSeconds)
            => _inner.ShowMessage($"{_featureId}.{id}", text, durationSeconds);

        public void HideMessage(string id) => _inner.HideMessage($"{_featureId}.{id}");
    }

    private sealed class FeatureChatApi : IChatApi
    {
        private readonly IChatApi _inner;
        private readonly Func<IGameRegistration, IGameRegistration> _own;

        internal FeatureChatApi(IChatApi inner, Func<IGameRegistration, IGameRegistration> own)
        {
            _inner = inner;
            _own = own;
        }

        public bool IsTyping => _inner.IsTyping;
        public IGameRegistration Configure(Action<string> send, Func<bool> isHost, Func<bool> canType)
            => _own(_inner.Configure(send, isHost, canType));
        public IGameRegistration AddCommand(string name, string usage, string description, Action<string> execute)
            => _own(_inner.AddCommand(name, usage, description, execute));
        public void AddRemoteLine(string fromName, string message) => _inner.AddRemoteLine(fromName, message);
        public void AddSystemLine(string text) => _inner.AddSystemLine(text);
        public void Tick() => _inner.Tick();
        public void Draw() => _inner.Draw();
        public void ForceClose() => _inner.ForceClose();
    }

    private sealed class FeatureInventoryApi : IInventoryApi
    {
        private readonly IInventoryApi _inner;
        private readonly Func<IGameRegistration, IGameRegistration> _own;

        internal FeatureInventoryApi(IInventoryApi inner,
            Func<IGameRegistration, IGameRegistration> own)
        { _inner = inner; _own = own; }

        public bool TryGetSelectedShareableItem(out ShareableItem item)
            => _inner.TryGetSelectedShareableItem(out item);
        public IGameRegistration AddShareActions(Func<bool> canGive, Action<ShareableItem> give,
            Func<bool> canDrop, Action<ShareableItem> drop)
            => _own(_inner.AddShareActions(canGive, give, canDrop, drop));
        public void SetGroundItems(IReadOnlyList<GroundItem> items) => _inner.SetGroundItems(items);
        public void DrawGroundItems() => _inner.DrawGroundItems();
        public uint SelectGroundItem(IReadOnlyList<GroundItem> nearbyItems)
            => _inner.SelectGroundItem(nearbyItems);
        public bool IsShareableDefinition(int definitionId)
            => _inner.IsShareableDefinition(definitionId);
        public bool CanAccept(int definitionId, int count, out string reason)
            => _inner.CanAccept(definitionId, count, out reason);
        public bool TryRemove(ushort uniqueId, int definitionId, int count, out string reason)
            => _inner.TryRemove(uniqueId, definitionId, count, out reason);
        public bool TryRemoveAny(int definitionId, int count, out string reason)
            => _inner.TryRemoveAny(definitionId, count, out reason);
        public bool TryAdd(int definitionId, int count, out string reason)
            => _inner.TryAdd(definitionId, count, out reason);
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
        var relayed = _extension.RegisterEvent($"{_featureId}.{id}.relayed", codec);
        relayed.Received += message =>
        {
            // The host commits its own send locally, clients do not — filtering here keeps
            // both sides behaving identically.
            if (message.SourcePlayerId == _runtime.LocalPlayer.Id) return;
            onReceived(message.SourcePlayerId, message.Payload);
        };

        var request = _extension.RegisterCommand($"{_featureId}.{id}",
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

        var state = _extension.RegisterState($"{_featureId}.{id}", FeatureCodec.For<T>());
        state.Changed += change =>
        {
            if (change.Removed) return;
            onChanged(change.Value);
        };

        return new HostState<T>(_runtime, _extension, state, $"{_featureId}.{id}");
    }

    /// <summary>
    /// Declares a value the host owns for each player separately (a lamp mode, an outfit).
    /// <paramref name="onChanged"/> receives the player it belongs to. Latecomers get
    /// everyone's current value; a leaving player's entry is dropped for you.
    /// </summary>
    public PerPlayerState<T> PerPlayerState<T>(string id, Action<int, T> onChanged) where T : IPacket, new()
    {
        if (onChanged == null) throw new ArgumentNullException(nameof(onChanged));

        var state = _extension.RegisterState($"{_featureId}.{id}", FeatureCodec.For<T>());
        state.Changed += change =>
        {
            if (change.Removed) return;
            onChanged(change.ScopePlayerId, change.Value);
        };

        return new PerPlayerState<T>(_runtime, _extension, state, $"{_featureId}.{id}");
    }

    /// <summary>
    /// Declares a request the host arbitrates: <paramref name="handler"/> runs on the host
    /// only and may reject. Use it whenever a client must not decide the outcome alone.
    /// </summary>
    public HostCommand<T> HostCommand<T>(string id, Action<HostRequest<T>> handler) where T : IPacket, new()
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        var command = _extension.RegisterCommand($"{_featureId}.{id}",
            context => handler(new HostRequest<T>(context)), FeatureCodec.For<T>());

        return new HostCommand<T>(_runtime, command, $"{_featureId}.{id}");
    }

    /// <summary>Declares a transient event that only the authoritative host may emit.</summary>
    public HostEvent<T> HostEvent<T>(string id, Action<int, T> onReceived) where T : IPacket, new()
    {
        if (onReceived == null) throw new ArgumentNullException(nameof(onReceived));
        var multiplayerEvent = _extension.RegisterEvent($"{_featureId}.{id}", FeatureCodec.For<T>());
        multiplayerEvent.Received += message => onReceived(message.SourcePlayerId, message.Payload);
        return new HostEvent<T>(_runtime, _extension, multiplayerEvent, $"{_featureId}.{id}");
    }

    /// <summary>
    /// Declares a stream for data you send constantly and that the next packet replaces —
    /// a position, an animation frame. Skips the transaction machinery, so it is cheap
    /// enough for every frame, and in exchange gives no ordering, no delivery guarantee and
    /// no replay for latecomers.
    /// </summary>
    /// <param name="id">Stable stream identifier within the feature.</param>
    /// <param name="onReceived">Callback receiving the sender id and decoded payload.</param>
    /// <param name="reliable">Leave false for anything sent continuously. Set it only when
    /// a value is sent on change and a drop would leave the others stuck on the old one.</param>
    public Stream<T> Stream<T>(string id, Action<int, T> onReceived, bool reliable = false)
        where T : IPacket, new()
    {
        if (onReceived == null) throw new ArgumentNullException(nameof(onReceived));

        var channel = _streams.Register($"{_featureId}.{id}", (fromPlayerId, payload) =>
        {
            var message = new T();
            using var buffer = new MemoryStream(payload ?? Array.Empty<byte>(), writable: false);
            using var reader = new BinaryReader(buffer);
            message.Deserialize(reader);
            onReceived(fromPlayerId, message);
        });

        return new Stream<T>(_network(), channel, reliable);
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

    /// <summary>Runs when a player joins, with their id and display name.</summary>
    public void OnPlayerJoined(Action<int, string> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        Action<MultiplayerPlayer> callback = player => handler(player.Id, player.Name);
        _runtime.PlayerJoined += callback;
        _unsubscribe.Add(() => _runtime.PlayerJoined -= callback);
    }

    /// <summary>Runs when a player leaves. The name is the last one known — by then the
    /// player is already gone from the roster.</summary>
    public void OnPlayerLeft(Action<int, string> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        Action<MultiplayerPlayer> callback = player => handler(player.Id, player.Name);
        _runtime.PlayerLeft += callback;
        _unsubscribe.Add(() => _runtime.PlayerLeft -= callback);
    }

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
