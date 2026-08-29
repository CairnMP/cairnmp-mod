namespace CairnMultiplayer.Shared;

/// <summary>
/// Protocol version. Bumped every time the packet layout changes in an incompatible
/// way. The handshake rejects clients that do not match.
/// </summary>
public static class Protocol
{
    // 12: ClientFeatureStream carries the sender's reliability flag, so the host relays a
    // stream the way it was declared instead of always relaying unreliably.
    // 11: every gameplay packet except the pose/frame streams now travels through the
    // feature framework, freeing ids 4, 10-14, 16, 69, 76-80 and 83. Real-time streams
    // share ids 19 and 89 whatever the feature.
    public const int Version = 12;
    public const string ConnectionKey = "cairnmp";
    public const int DefaultPort = 14000;

    /// <summary>
    /// CairnMP client version sent to the API when creating a lobby.
    /// Matches the deployed launcher/mod version.
    /// </summary>
    public const string GameVersion = "1.0.0";

    /// <summary>Rate at which a client broadcasts the position and state of its local player.</summary>
    public const float PlayerStateUpdateIntervalSeconds = 1f / 30f; // 30 Hz

    /// <summary>Rate at which a client broadcasts local animation frames.</summary>
    public const float BoneStateUpdateIntervalSeconds = 1f / 30f; // 30 Hz

    /// <summary>Rate at which the host broadcasts the authoritative weather.</summary>
    public const float WeatherStateUpdateIntervalSeconds = 0.5f; // 2 Hz

    /// <summary>Short transition duration used when a client joins the host's weather state.</summary>
    public const float WeatherStateTransitionSeconds = 0.5f;

    /// <summary>Rate at which the mod polls the local lamp state to detect a change.</summary>
    public const float LampStatePollIntervalSeconds = 0.2f; // 5 Hz

    /// <summary>Rate at which the mod polls the local cosmetic state (glowing gloves, ...).</summary>
    public const float CosmeticStatePollIntervalSeconds = 0.5f; // 2 Hz

    /// <summary>Bit of the cosmetic Flags field: glowing gloves (GlowingGloves) active.</summary>
    public const byte CosmeticFlagGlowingGloves = 1 << 0;

    /// <summary>How long a ping marker is shown before it automatically disappears.</summary>
    public const float PingLifetimeSeconds = 15f;

    /// <summary>Minimum delay between two pings placed by the same player (anti-spam).</summary>
    public const float PingCooldownSeconds = 1f;

    /// <summary>Default distance in front of the camera when the ping raycast hits nothing.</summary>
    public const float DefaultPingDistance = 50f;

    /// <summary>Number of synchronized finger bones. Aava skeleton (resolved by name):
    /// per hand, Thumb 00-02 (3) + Index/Middle/Ring/Pinky 00-03 (4 each) = 19; x2 hands = 38.</summary>
    public const int FingerBoneCount = 38;

    /// <summary>Size of the compressed finger-pose payload (FingerBoneCount x 4 bytes smallest-three).</summary>
    public const int HandPosePackedSize = FingerBoneCount * QuaternionCodec.PackedSize;

    /// <summary>Rate at which the mod captures/broadcasts the local finger pose.</summary>
    public const float HandPosePollIntervalSeconds = 1f / 12f; // ~12 Hz

    /// <summary>Rate at which the host broadcasts the authoritative time of day.</summary>
    public const float TimeStateUpdateIntervalSeconds = 0.5f; // 2 Hz

    /// <summary>Tighter time broadcast rate during fast-forward (everyone asleep).</summary>
    public const float TimeStateFastForwardIntervalSeconds = 1f / 15f; // ~15 Hz
}

/// <summary>
/// Lifecycle state of a player as seen by the server. Ghosts are only rendered when
/// the local player AND the remote player are both InGame -- this avoids spawning
/// prefabs during scene transitions (which crashes Cairn's Addressables pipeline)
/// and hides zombie ghosts during loading.
///
/// Keep the values stable across protocol versions.
/// </summary>
public enum PlayerState : byte
{
    Unknown    = 0, // default, no state received yet
    Connecting = 1, // socket connected, handshake not finished yet
    InMenu     = 2, // main menu, not in a game
    Loading    = 3, // scene transitions in progress, intro cinematic
    InGame     = 4, // MC spawned, scenes stable -- ok to render ghosts
}

/// <summary>
/// Packet type identifiers. Sent as the first byte of every packet.
/// Keep the values stable across versions -- deprecated packets go at the bottom,
/// never renumbered.
/// </summary>
public enum PacketId : byte
{
    // Client -> Server
    ClientHandshake = 1,
    ClientDisconnect = 2,
    ClientPlayerState = 3,
    // 4 was ClientChat — chat moved to the feature framework (Features/Chat/). Reserved.
    ClientBoneState = 5,
    ClientPitonPlaced = 6,
    ClientPitonRemoved = 7,
    ClientPlayerFrame = 8,
    ClientClimbotFrame = 9,
    // 10 was ClientWeatherState — weather is host-published state in Features/Weather/. Reserved.
    // 11 was ClientLampState — appearance is per-player host state in Features/Players/Avatar/.
    // 12 was ClientPingPlaced — pings moved to the feature framework (Features/World/).
    // Left reserved on purpose: reusing the number would make an old client's ping look
    // like whatever packet takes its place.
    // 13 was ClientHandPose — finger poses stream through Features/Players/Avatar/. Reserved.
    // 14 was ClientSleepState — sleep is reported through Features/Clock/. Reserved.
    ClientRopeClip = 15,
    // 16 was ClientCosmeticState — see the note on 11. Reserved, do not reuse.
    ClientExtensionManifest = 17,
    ClientExtensionCommand = 18,
    /// <summary>Any feature's real-time stream, client to host. The channel is identified
    /// inside the payload, so a new stream never needs a new packet id.</summary>
    ClientFeatureStream = 19,

    // Server -> Client
    ServerHandshakeAck = 64,
    ServerHandshakeReject = 65,
    ServerPlayerJoined = 66,
    ServerPlayerLeft = 67,
    ServerPlayerState = 68,
    // 69 was ServerChatBroadcast — see the note on 4. Reserved, do not reuse.
    ServerStartGame = 70,
    ServerBoneState = 71,
    ServerPitonPlaced = 72,
    ServerPitonRemoved = 73,
    ServerPlayerFrame = 74,
    ServerClimbotFrame = 75,
    // 76 was ServerWeatherState — see the note on 10. Reserved, do not reuse.
    // 77 was ServerLampState — see the note on 11. Reserved, do not reuse.
    // 78 was ServerPingPlaced — see the note on 12. Reserved, do not reuse.
    // 79 was ServerHandPose — see the note on 13. Reserved, do not reuse.
    // 80 was ServerTimeState — time of day moved to Features/Clock/. Reserved.
    ServerTeleport = 81,
    ServerRopeClip = 82,
    // 83 was ServerCosmeticState — see the note on 11. Reserved, do not reuse.
    ServerExtensionManifestResult = 84,
    ServerExtensionCommandResult = 85,
    ServerExtensionEvent = 86,
    ServerExtensionState = 87,
    ServerExtensionPeerStatus = 88,
    /// <summary>Host relay of a feature stream, carrying the sender's id.</summary>
    ServerFeatureStream = 89,
}

/// <summary>
/// The difficulty values match Cairn's `DifficultyTweakables.SelectedDifficulty`
/// enum -- same names, same int values. We control which one is active so the client
/// can hook directly into the game's launch options.
/// </summary>
public enum GameDifficulty : int
{
    Invalid  = 0,
    Alpinist  = 1769420573,
    Explorer  = 766328718,
    FreeSolo  = -1944667143,
    FreeRoam  = 418187680,
}

/// <summary>Outcome of a managed extension command handled by the host.</summary>
public enum ExtensionCommandStatus : byte
{
    Committed = 0,
    Rejected = 1,
    Failed = 2,
    TimedOut = 3,
    Unavailable = 4,
}
