using System;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Game.Chat;
using CairnMultiplayerMod.Internal.Game.Input;
using CairnMultiplayerMod.Internal.Game.PhotoMode;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Game.World;
using CairnMultiplayerMod.Internal.Networking;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed class GameStateAdapter : IGameStateApi
{
    private readonly Func<PlayerState> _localState;

    internal GameStateAdapter(Func<PlayerState> localState)
        => _localState = localState ?? throw new ArgumentNullException(nameof(localState));

    public PlayerState LocalPlayerState => _localState();
    public bool IsLocalPlayerInGame => LocalPlayerState == PlayerState.InGame;
}

internal sealed class GameTimeAdapter : IGameTimeApi
{
    public float UnscaledTime => Time.unscaledTime;
    public float UnscaledDeltaTime => Time.unscaledDeltaTime;
    public bool IsPaused => Time.timeScale <= 0f;
}

internal sealed class GameInputAdapter : IGameInputApi
{
    public bool IsKeyboardCaptured => InputCaptureState.IsKeyboardCaptured;

    public bool WasPressed(GameInputAction action)
    {
        return action switch
        {
            GameInputAction.PrimaryPointer => Mouse.current?.leftButton.wasPressedThisFrame == true,
            GameInputAction.PingController => Gamepad.current?.rightShoulder.wasPressedThisFrame == true,
            GameInputAction.Panic => Keyboard.current?.f10Key.wasPressedThisFrame == true,
            _ => false,
        };
    }

    public bool WasKeyPressed(GameKey key)
    {
        var keyboard = Keyboard.current;
        return keyboard != null
               && Enum.TryParse<Key>(key.ToString(), out var unityKey)
               && keyboard[unityKey].wasPressedThisFrame;
    }
}

internal sealed class HudAdapter : IGameHudApi
{
    private sealed class Message
    {
        internal string Text;
        internal float HideAt;
    }

    private readonly Dictionary<string, Message> _messages = new(StringComparer.Ordinal);
    private GUIStyle _style;

    public void ShowMessage(string id, string text, float durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A message id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Message text is required.", nameof(text));
        if (durationSeconds <= 0f) throw new ArgumentOutOfRangeException(nameof(durationSeconds));

        _messages[id] = new Message { Text = text, HideAt = Time.unscaledTime + durationSeconds };
    }

    public void HideMessage(string id)
    {
        if (!string.IsNullOrWhiteSpace(id)) _messages.Remove(id);
    }

    internal void Tick()
    {
        if (_messages.Count == 0) return;
        List<string> expired = null;
        foreach (var pair in _messages)
        {
            if (Time.unscaledTime >= pair.Value.HideAt)
                (expired ??= new List<string>()).Add(pair.Key);
        }
        if (expired == null) return;
        foreach (var id in expired) _messages.Remove(id);
    }

    internal void Draw()
    {
        if (_messages.Count == 0 || Event.current?.type != EventType.Repaint) return;
        _style ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
        };

        var y = 20f;
        foreach (var message in _messages.Values)
        {
            GUI.Label(new Rect(20f, y, Math.Max(400f, Screen.width - 40f), 30f), message.Text, _style);
            y += 32f;
        }
    }

    internal void Clear() => _messages.Clear();
}

internal sealed class ClockAdapter : IClockApi
{
    public bool TryGetDayTime(out float dayTime) => TimeInterop.TryGetDayTime01(out dayTime);
    public bool TryGetLocalSleep(out bool asleep) => TimeInterop.TryIsLocalAsleep(out asleep);
    public bool Freeze(float dayTime) => TimeInterop.FreezeDayCycle(dayTime);
    public bool Unfreeze() => TimeInterop.UnfreezeDayCycle();
    public void Reset() => TimeInterop.ResetCaches();
    public void LogDiagnosticsOnce() => TimeInterop.DumpTimeApi();
}

internal sealed class PlayersAdapter : IPlayersApi
{
    private readonly Func<NetworkManager> _network;
    private readonly List<int> _sleepParticipants = new();
    private readonly List<int> _inGamePlayers = new();

    public IReadOnlyList<int> RemoteSleepParticipants
    {
        get
        {
            _sleepParticipants.Clear();
            var network = _network();
            if (network != null)
                foreach (var pair in network.RemotePlayers)
                    if (pair.Value?.State is PlayerState.InGame or PlayerState.Bivouac)
                        _sleepParticipants.Add(pair.Key);
            return _sleepParticipants;
        }
    }

    internal PlayersAdapter(Func<NetworkManager> network)
        => _network = network ?? throw new ArgumentNullException(nameof(network));

    public IReadOnlyList<int> RemotePlayersInGame
    {
        get
        {
            var players = _inGamePlayers;
            players.Clear();
            var network = _network();
            if (network == null) return players;

            foreach (var pair in network.RemotePlayers)
            {
                if (pair.Value?.State == PlayerState.InGame) players.Add(pair.Key);
            }
            return players;
        }
    }

    public bool TryCaptureHandPose(out byte[] packed)
        => FingerInterop.TryCaptureLocalPose(out packed);

    public bool TryCaptureAppearance(out int packed)
    {
        packed = 0;
        if (!LampInterop.TryGetLocalState(out var lightMode)) return false;

        CosmeticInterop.TryGetLocalStickAnchorMode(out var anchorMode);
        var outfitBits = CosmeticInterop.GetLocalOutfitBits();
        packed = (lightMode & 0xFF)
                 | ((anchorMode & 0xFF) << 8)
                 | ((outfitBits & CosmeticInterop.OutfitBitsMask) << 16);
        return true;
    }

    public bool TryCaptureCosmetics(out byte flags)
        => CosmeticInterop.TryGetLocalCosmetics(out flags);

    public void SetRemoteHandPose(int playerId, byte[] packed)
    {
        if (!TryGetRemote(playerId, out var player)) return;
        player.HandPosePacked = packed;
        player.HasHandPose = true;
    }

    public void SetRemoteAppearance(int playerId, int packed)
    {
        if (!TryGetRemote(playerId, out var player)) return;
        player.LampMode = packed;
        player.HasLampState = true;
    }

    public void SetRemoteCosmetics(int playerId, byte flags)
    {
        if (!TryGetRemote(playerId, out var player)) return;
        player.CosmeticFlags = flags;
        player.HasCosmeticState = true;
    }

    public void ResetHandPoseCaches() => FingerInterop.ResetCaches();

    public void ResetAppearanceCaches()
    {
        LampInterop.ResetCaches();
        CosmeticInterop.ResetCaches();
    }

    private bool TryGetRemote(int playerId, out RemotePlayer player)
    {
        player = null;
        var network = _network();
        return network != null
               && network.RemotePlayers.TryGetValue(playerId, out player)
               && player != null;
    }
}

internal sealed class WeatherAdapter : IWeatherApi
{
    public bool TryCapture(out WeatherSyncData weather)
        => WeatherInterop.TryCaptureWeather(out weather);

    public bool IsValid(WeatherSyncData weather)
        => PacketValidation.IsValidWeatherState(weather);

    public void ApplyRemote(WeatherSyncData weather) => WeatherInterop.ApplyRemoteWeather(weather);
    public void TickRemote() => WeatherInterop.TickRemote();
    public void Reset() => WeatherInterop.ResetRemoteState();
}

internal sealed class WorldAdapter : IWorldApi
{
    public bool IsFreeCameraActive
        => FreecamInterop.TryIsActive(out var active) && active;

    public bool TryGetAimPoint(out WorldPosition position)
    {
        position = default;
        if (!FreecamInterop.TryComputePingPoint(out var point)) return false;
        position = new WorldPosition(point.x, point.y, point.z);
        return true;
    }

    public void SpawnPing(int ownerId, WorldPosition position)
        => PingMarkerManager.Spawn(ownerId, new Vector3(position.X, position.Y, position.Z));

    public void TickPings() => PingMarkerManager.Update();
    public void DrawPings() => PingMarkerManager.OnGUI();
    public void ClearPings() => PingMarkerManager.ClearAll();
}

internal sealed class ChatAdapter : IChatApi
{
    private readonly Func<NetworkManager> _network;
    private ChatController _controller;
    private ChatRegistration _registration;

    internal ChatAdapter(Func<NetworkManager> network)
        => _network = network ?? throw new ArgumentNullException(nameof(network));

    public bool IsTyping => _controller?.IsTyping == true;

    public IGameRegistration Configure(Action<string> send, Func<bool> isHost, Func<bool> canType)
    {
        if (_registration != null)
            throw new InvalidOperationException("The chat overlay is already configured.");
        if (send == null) throw new ArgumentNullException(nameof(send));
        if (isHost == null) throw new ArgumentNullException(nameof(isHost));
        if (canType == null) throw new ArgumentNullException(nameof(canType));

        var router = new CommandRouter(_network(), isHost, AddSystemLine);
        _controller = new ChatController(router, send, canType);
        return _registration = new ChatRegistration(this);
    }

    public void AddRemoteLine(string fromName, string message) => _controller?.AddRemoteLine(fromName, message);
    public void AddSystemLine(string text) => _controller?.AddSystemLine(text);
    public void Tick() => _controller?.Update();
    public void Draw() => _controller?.OnGUI();
    public void ForceClose() => _controller?.ForceClose();

    private void Release(ChatRegistration registration)
    {
        if (!ReferenceEquals(_registration, registration)) return;
        _controller?.ForceClose();
        _controller = null;
        _registration = null;
    }

    private sealed class ChatRegistration : IGameRegistration
    {
        private ChatAdapter _owner;

        internal ChatRegistration(ChatAdapter owner) => _owner = owner;

        public string Id => "chat-overlay";
        public bool IsActive => _owner != null;

        public void Dispose()
        {
            var owner = _owner;
            if (owner == null) return;
            _owner = null;
            owner.Release(this);
        }
    }
}
