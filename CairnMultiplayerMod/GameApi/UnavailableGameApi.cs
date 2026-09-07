using System;

namespace CairnMultiplayerMod.GameApi;

/// <summary>
/// Null implementation used by framework-only tests. Production bootstrap always injects
/// the real facade, while a feature can still be declared without a Unity scene.
/// </summary>
internal sealed class UnavailableGameApi : IGameApi
{
    internal static readonly UnavailableGameApi Instance = new();

    private UnavailableGameApi() { }

    public IMainMenuApi MainMenu { get; } = new UnavailableMainMenuApi();
    public IGameStateApi State { get; } = new UnavailableStateApi();
    public IGameTimeApi Time { get; } = new UnavailableTimeApi();
    public IGameInputApi Input { get; } = new UnavailableInputApi();
    public IGameHudApi Hud { get; } = new UnavailableHudApi();
    public IChatApi Chat { get; } = new UnavailableChatApi();
    public IInventoryApi Inventory { get; } = new UnavailableInventoryApi();
    public IClockApi Clock { get; } = new UnavailableClockApi();
    public IPlayersApi Players { get; } = new UnavailablePlayersApi();
    public IWeatherApi Weather { get; } = new UnavailableWeatherApi();
    public IWorldApi World { get; } = new UnavailableWorldApi();

    private sealed class UnavailableMainMenuApi : IMainMenuApi
    {
        public IGameRegistration AddButton(string id, string label, Action onClick)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A button id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A button label is required.", nameof(label));
            if (onClick == null) throw new ArgumentNullException(nameof(onClick));
            return new InactiveRegistration(id);
        }
    }

    private sealed class InactiveRegistration : IGameRegistration
    {
        internal InactiveRegistration(string id) => Id = id;

        public string Id { get; }
        public bool IsActive => false;
        public void Dispose() { }
    }

    private sealed class UnavailableStateApi : IGameStateApi
    {
        public CairnMultiplayer.Shared.PlayerState LocalPlayerState => CairnMultiplayer.Shared.PlayerState.Unknown;
        public bool IsLocalPlayerInGame => false;
    }

    private sealed class UnavailableTimeApi : IGameTimeApi
    {
        public float UnscaledTime => 0f;
        public float UnscaledDeltaTime => 0f;
        public bool IsPaused => false;
    }

    private sealed class UnavailableInputApi : IGameInputApi
    {
        public bool IsKeyboardCaptured => false;
        public bool WasPressed(GameInputAction action) => false;
        public bool WasKeyPressed(GameKey key) => false;
    }

    private sealed class UnavailableHudApi : IGameHudApi
    {
        public void ShowMessage(string id, string text, float durationSeconds) { }
        public void HideMessage(string id) { }
    }

    private sealed class UnavailableChatApi : IChatApi
    {
        public bool IsTyping => false;
        public IGameRegistration Configure(Action<string> send, Func<bool> isHost, Func<bool> canType)
            => new InactiveRegistration("chat-overlay");
        public IGameRegistration AddCommand(string name, string usage, string description, Action<string> execute)
            => new InactiveRegistration($"chat-command.{name}");
        public void AddRemoteLine(string fromName, string message) { }
        public void AddSystemLine(string text) { }
        public void Tick() { }
        public void Draw() { }
        public void ForceClose() { }
    }

    private sealed class UnavailableInventoryApi : IInventoryApi
    {
        public bool TryGetSelectedShareableItem(out ShareableItem item)
        { item = default; return false; }
        public IGameRegistration AddShareActions(Func<bool> canGive, Action<ShareableItem> give,
            Func<bool> canDrop, Action<ShareableItem> drop)
            => new InactiveRegistration("inventory.share-actions");
        public void SetGroundItems(System.Collections.Generic.IReadOnlyList<GroundItem> items) { }
        public void DrawGroundItems() { }
        public uint SelectGroundItem(System.Collections.Generic.IReadOnlyList<GroundItem> nearbyItems) => 0;
        public bool IsShareableDefinition(int definitionId) => false;
        public bool CanAccept(int definitionId, int count, out string reason)
        { reason = "Inventory is unavailable."; return false; }
        public bool TryRemove(ushort uniqueId, int definitionId, int count, out string reason)
        { reason = "Inventory is unavailable."; return false; }
        public bool TryRemoveAny(int definitionId, int count, out string reason)
        { reason = "Inventory is unavailable."; return false; }
        public bool TryAdd(int definitionId, int count, out string reason)
        { reason = "Inventory is unavailable."; return false; }
    }

    private sealed class UnavailableClockApi : IClockApi
    {
        public bool TryGetDayTime(out float dayTime) { dayTime = 0f; return false; }
        public bool TryGetLocalSleep(out bool asleep) { asleep = false; return false; }
        public bool Freeze(float dayTime) => false;
        public bool Unfreeze() => false;
        public void Reset() { }
        public void LogDiagnosticsOnce() { }
    }

    private sealed class UnavailablePlayersApi : IPlayersApi
    {
        public System.Collections.Generic.IReadOnlyList<int> RemoteSleepParticipants => System.Array.Empty<int>();
        public System.Collections.Generic.IReadOnlyList<int> RemotePlayersInGame { get; }
            = Array.Empty<int>();
        public bool TryGetLocation(int playerId, out PlayerLocation location)
        { location = default; return false; }
        public bool TryCaptureHandPose(out byte[] packed) { packed = null; return false; }
        public bool TryCaptureAppearance(out int packed) { packed = 0; return false; }
        public bool TryCaptureCosmetics(out byte flags) { flags = 0; return false; }
        public void SetRemoteHandPose(int playerId, byte[] packed) { }
        public void SetRemoteAppearance(int playerId, int packed) { }
        public void SetRemoteCosmetics(int playerId, byte flags) { }
        public void ResetHandPoseCaches() { }
        public void ResetAppearanceCaches() { }
    }

    private sealed class UnavailableWeatherApi : IWeatherApi
    {
        public bool TryCapture(out CairnMultiplayer.Shared.WeatherSyncData weather)
        { weather = default; return false; }
        public bool IsValid(CairnMultiplayer.Shared.WeatherSyncData weather) => false;
        public void ApplyRemote(CairnMultiplayer.Shared.WeatherSyncData weather) { }
        public void TickRemote() { }
        public void Reset() { }
    }

    private sealed class UnavailableWorldApi : IWorldApi
    {
        public bool IsFreeCameraActive => false;
        public bool TryGetAimPoint(out WorldPosition position) { position = default; return false; }
        public void SpawnPing(int ownerId, WorldPosition position) { }
        public void TickPings() { }
        public void DrawPings() { }
        public void ClearPings() { }
    }
}
