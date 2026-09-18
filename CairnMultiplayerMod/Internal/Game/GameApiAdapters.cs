using System;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.GameApi;
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
    public bool IsLocalPlayerInBivouac => GameLifecycleService.IsLocalInBivouac();
    public bool HasReachedSummit => Life.LifeInterop.HasReachedSummit();
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
    public bool IsPauseMenuActive => MultiplayerPausePatch.IsPauseMenuActive;

    public bool WasPressed(GameInputAction action)
    {
        return action switch
        {
            GameInputAction.PrimaryPointer => Mouse.current?.leftButton.wasPressedThisFrame == true,
            GameInputAction.PingController => ModControllerInput.WasPressed(ControllerShortcut.PlacePing),
            GameInputAction.Panic => Keyboard.current?.f10Key.wasPressedThisFrame == true
                                     || ModControllerInput.WasPressed(ControllerShortcut.Panic),
            GameInputAction.PickupSharedItemController =>
                ModControllerInput.WasPressed(ControllerShortcut.PickupSharedItem),
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
    private readonly List<StandingRow> _standings = new();
    private string _standingsTitle = "";
    private GUIStyle _style, _standingsStyle, _standingsTitleStyle;

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
        if (Event.current?.type != EventType.Repaint) return;
        DrawStandings();
        if (_messages.Count == 0) return;
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

    public void ShowStandings(string title, IReadOnlyList<StandingRow> rows)
    {
        _standingsTitle = title ?? "";
        _standings.Clear();
        if (rows != null) _standings.AddRange(rows);
    }

    public void HideStandings()
    {
        _standings.Clear();
        _standingsTitle = "";
    }

    /// <summary>
    /// Drawn in the top-right corner, opposite the messages, because both can be on screen at
    /// once and a standings table that jumps around is unreadable.
    /// </summary>
    private void DrawStandings()
    {
        if (_standings.Count == 0) return;

        _standingsStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleLeft };
        _standingsTitleStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
        };

        const float width = 260f, rowHeight = 22f, padding = 10f;
        var height = padding * 2f + rowHeight * (_standings.Count + 1);
        var x = Screen.width - width - 20f;
        var box = new Rect(x, 20f, width, height);
        GUI.color = new Color(0f, 0f, 0f, 0.45f);
        GUI.DrawTexture(box, Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUI.Label(new Rect(x + padding, 20f + padding, width - padding * 2f, rowHeight),
            _standingsTitle, _standingsTitleStyle);

        var y = 20f + padding + rowHeight;
        foreach (var row in _standings)
        {
            _standingsStyle.normal.textColor = row.IsOut ? new Color(0.72f, 0.72f, 0.72f, 0.6f)
                : row.IsLocal ? new Color(0.98f, 0.82f, 0.45f, 1f)
                : Color.white;
            _standingsStyle.fontStyle = row.IsLocal ? FontStyle.Bold : FontStyle.Normal;

            GUI.Label(new Rect(x + padding, y, 26f, rowHeight), $"{row.Rank}.", _standingsStyle);
            GUI.Label(new Rect(x + padding + 26f, y, width - padding * 2f - 116f, rowHeight),
                row.Name, _standingsStyle);
            var detail = new GUIStyle(_standingsStyle) { alignment = TextAnchor.MiddleRight };
            GUI.Label(new Rect(x + width - padding - 90f, y, 90f, rowHeight), row.Detail, detail);
            y += rowHeight;
        }
    }

    internal void Clear()
    {
        _messages.Clear();
        HideStandings();
    }
}

internal sealed class ClockAdapter : IClockApi
{
    public bool TryGetDayTime(out float dayTime) => TimeInterop.TryGetDayTime01(out dayTime);
    public bool TryGetLocalSleep(out bool asleep) => TimeInterop.TryIsLocalAsleep(out asleep);
    public bool Freeze(float dayTime) => TimeInterop.FreezeDayCycle(dayTime);
    public bool Unfreeze() => TimeInterop.UnfreezeDayCycle();
    public void OnSceneChanged() => TimeInterop.ForgetSceneBoundFreeze();
    public void Reset() => TimeInterop.ResetCaches();
    public void LogDiagnosticsOnce() => TimeInterop.DumpTimeApi();
}

internal sealed class PlayersAdapter : IPlayersApi
{
    private const double MaxLocationAgeSeconds = 2;
    private readonly Func<NetworkManager> _network;
    private readonly RuntimeState _state;
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

    internal PlayersAdapter(Func<NetworkManager> network, RuntimeState state)
    {
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public bool IsLocalPlayerFalling
        => PawnCaptureInterop.GetLocalPawnState() == Il2CppTheGameBakers.Cairn.Netplay.NetFrame.PawnStateType.Falling;

    public bool IsRopedToLocalPlayer(int playerId)
    {
        var network = _network();
        return network != null && Roping.RopeLinkState.IsLinked(network.LocalPlayerId, playerId);
    }

    public bool ShakeLocalClimberGrip() => Roping.RopeShakeInterop.ShakeLocalClimber();

    public bool TryGetLocation(int playerId, out PlayerLocation location)
    {
        location = default;
        var network = _network();
        if (network == null) return false;
        if (playerId == network.LocalPlayerId)
        {
            if (_state.LocalPlayerState != PlayerState.InGame
                || !LocalPlayerInterop.TryGetPose(out var position, out _)) return false;
            location = new PlayerLocation(position.x, position.y, position.z,
                SceneRoles.ResolveNetworkScene(_state.CurrentScene, _state.LastGameplayScene),
                _state.LocalPlayerState);
            return true;
        }

        if (!network.RemotePlayers.TryGetValue(playerId, out var player) || player == null
            || player.State != PlayerState.InGame || player.LastUpdateTime <= 0
            || DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond - player.LastUpdateTime
                > MaxLocationAgeSeconds) return false;
        location = new PlayerLocation(player.X, player.Y, player.Z, player.SceneName, player.State);
        return true;
    }

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
    public void OnSceneChanged() => WeatherInterop.ResetCaches();
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

    public bool TryTeleportLocalPlayer(WorldPosition position, float yawDegrees, out string refusedReason)
        => TeleportInterop.TeleportLocalPlayer(
            new Vector3(position.X, position.Y, position.Z), yawDegrees, out refusedReason);

    private readonly List<Vector3> _markBuffer = new();

    public void SetTrailMarks(int playerId, IReadOnlyList<WorldPosition> positions)
    {
        _markBuffer.Clear();
        if (positions != null)
            foreach (var position in positions)
                _markBuffer.Add(new Vector3(position.X, position.Y, position.Z));
        PingMarkerManager.SetMarks(playerId, _markBuffer);
    }

    public void ClearTrailMarks() => PingMarkerManager.ClearMarks();

    public bool AreTrailsVisible => ClimbTrailManager.IsVisible;

    public void RecordTrailPoint(int playerId, WorldPosition position)
        => ClimbTrailManager.Record(playerId, new Vector3(position.X, position.Y, position.Z));

    public void SetTrailsVisible(bool visible) => ClimbTrailManager.SetVisible(visible);

    public void ForgetTrail(int playerId) => ClimbTrailManager.Forget(playerId);

    public void ClearTrails() => ClimbTrailManager.Clear();
}

internal sealed class ChatAdapter : IChatApi
{
    private readonly Func<NetworkManager> _network;
    private ChatController _controller;
    private ChatRegistration _registration;
    private readonly Dictionary<string, ChatCommandDefinition> _commands =
        new(StringComparer.OrdinalIgnoreCase);

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

        var router = new CommandRouter(_network(), isHost, AddSystemLine, _commands);
        _controller = new ChatController(router, send, canType);
        return _registration = new ChatRegistration(this);
    }

    public IGameRegistration AddCommand(string name, string usage, string description, Action<string> execute)
    {
        name = (name ?? "").Trim().TrimStart('/').ToLowerInvariant();
        if (name.Length == 0) throw new ArgumentException("A command name is required.", nameof(name));
        if (name is "help" or "tp" or "bring")
            throw new InvalidOperationException($"/{name} is a built-in command.");
        if (execute == null) throw new ArgumentNullException(nameof(execute));
        if (_commands.ContainsKey(name)) throw new InvalidOperationException($"/{name} is already registered.");

        var definition = new ChatCommandDefinition(name,
            string.IsNullOrWhiteSpace(usage) ? $"/{name}" : usage.Trim(),
            description?.Trim() ?? "", execute);
        _commands.Add(name, definition);
        return new ChatCommandRegistration(this, definition);
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

    private void RemoveCommand(ChatCommandDefinition definition)
    {
        if (definition != null && _commands.TryGetValue(definition.Name, out var current)
            && ReferenceEquals(current, definition))
            _commands.Remove(definition.Name);
    }

    private sealed class ChatCommandRegistration : IGameRegistration
    {
        private ChatAdapter _owner;
        private readonly ChatCommandDefinition _definition;

        internal ChatCommandRegistration(ChatAdapter owner, ChatCommandDefinition definition)
        { _owner = owner; _definition = definition; }

        public string Id => $"chat-command.{_definition.Name}";
        public bool IsActive => _owner != null;

        public void Dispose()
        {
            var owner = _owner;
            if (owner == null) return;
            _owner = null;
            owner.RemoveCommand(_definition);
        }
    }
}
