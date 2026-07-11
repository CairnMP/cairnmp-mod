namespace CairnMultiplayer.Shared;

/// <summary>
/// Version du protocole. Incrementee chaque fois que le layout des paquets change
/// de maniere incompatible. Le handshake rejette les clients qui ne correspondent pas.
/// </summary>
public static class Protocol
{
    public const int Version = 6;
    public const string ConnectionKey = "cairnmp";
    public const int DefaultPort = 14000;

    /// <summary>
    /// Version du client CairnMP transmise à l'API lors de la création d'un lobby.
    /// Correspond à la version du launcher/mod déployée.
    /// </summary>
    public const string GameVersion = "1.0.0";

    /// <summary>Frequence a laquelle un client diffuse la position et l'etat de son joueur local.</summary>
    public const float PlayerStateUpdateIntervalSeconds = 1f / 30f; // 30 Hz

    /// <summary>Frequence a laquelle un client diffuse les frames d'animation locales.</summary>
    public const float BoneStateUpdateIntervalSeconds = 1f / 30f; // 30 Hz

    /// <summary>Frequence a laquelle l'hote diffuse la meteo autoritaire.</summary>
    public const float WeatherStateUpdateIntervalSeconds = 0.5f; // 2 Hz

    /// <summary>Duree de transition courte utilisee quand un client rejoint l'etat meteo de l'hote.</summary>
    public const float WeatherStateTransitionSeconds = 0.5f;

    /// <summary>Frequence a laquelle le mod sonde l'etat de la lampe locale pour detecter un changement.</summary>
    public const float LampStatePollIntervalSeconds = 0.2f; // 5 Hz

    /// <summary>Frequence a laquelle le mod sonde l'etat cosmetique local (gants lumineux, ...).</summary>
    public const float CosmeticStatePollIntervalSeconds = 0.5f; // 2 Hz

    /// <summary>Bit du champ Flags cosmetique : gants lumineux (GlowingGloves) actifs.</summary>
    public const byte CosmeticFlagGlowingGloves = 1 << 0;

    /// <summary>Duree d'affichage d'un marqueur de ping avant disparition automatique.</summary>
    public const float PingLifetimeSeconds = 15f;

    /// <summary>Delai minimal entre deux pings poses par le meme joueur (anti-spam).</summary>
    public const float PingCooldownSeconds = 1f;

    /// <summary>Distance par defaut devant la camera quand le raycast de ping ne touche rien.</summary>
    public const float DefaultPingDistance = 50f;

    /// <summary>Nombre d'os de doigts synchronises. Squelette Aava (resolution par nom) :
    /// par main, Thumb 00-02 (3) + Index/Middle/Ring/Pinky 00-03 (4 chacun) = 19 ; x2 mains = 38.</summary>
    public const int FingerBoneCount = 38;

    /// <summary>Taille du payload compresse d'une pose de doigts (FingerBoneCount x 4 octets smallest-three).</summary>
    public const int HandPosePackedSize = FingerBoneCount * QuaternionCodec.PackedSize;

    /// <summary>Frequence a laquelle le mod capture/diffuse la pose des doigts locale.</summary>
    public const float HandPosePollIntervalSeconds = 1f / 12f; // ~12 Hz

    /// <summary>Frequence a laquelle l'hote diffuse l'heure du jour autoritaire.</summary>
    public const float TimeStateUpdateIntervalSeconds = 0.5f; // 2 Hz

    /// <summary>Cadence rapprochee de diffusion de l'heure pendant le fast-forward (tous dorment).</summary>
    public const float TimeStateFastForwardIntervalSeconds = 1f / 15f; // ~15 Hz
}

/// <summary>
/// Etat de cycle de vie d'un joueur vu par le serveur. Les fantomes ne sont
/// rendus que quand le joueur local ET le joueur distant sont tous deux InGame --
/// cela evite de faire apparaitre des prefabs pendant les transitions de scene (ce qui
/// plante le pipeline Addressables de Cairn) et masque les fantomes zombies pendant le chargement.
///
/// Garder les valeurs stables entre les versions du protocole.
/// </summary>
public enum PlayerState : byte
{
    Unknown    = 0, // par defaut, aucun etat recu encore
    Connecting = 1, // socket connecte, handshake pas encore termine
    InMenu     = 2, // menu principal, pas en partie
    Loading    = 3, // transitions de scene en cours, cinematique d'intro
    InGame     = 4, // MC apparu, scenes stables -- ok pour rendre les fantomes
}

/// <summary>
/// Identifiants de type de paquet. Envoyes comme premier octet de chaque paquet.
/// Garder les valeurs stables entre les versions -- les paquets obsoletes vont en bas,
/// jamais renumerotes.
/// </summary>
public enum PacketId : byte
{
    // Client -> Serveur
    ClientHandshake = 1,
    ClientDisconnect = 2,
    ClientPlayerState = 3,
    ClientChat = 4,
    ClientBoneState = 5,
    ClientPitonPlaced = 6,
    ClientPitonRemoved = 7,
    ClientPlayerFrame = 8,
    ClientClimbotFrame = 9,
    ClientWeatherState = 10,
    ClientLampState = 11,
    ClientPingPlaced = 12,
    ClientHandPose = 13,
    ClientSleepState = 14,
    ClientRopeClip = 15,
    ClientCosmeticState = 16,

    // Serveur -> Client
    ServerHandshakeAck = 64,
    ServerHandshakeReject = 65,
    ServerPlayerJoined = 66,
    ServerPlayerLeft = 67,
    ServerPlayerState = 68,
    ServerChatBroadcast = 69,
    ServerStartGame = 70,
    ServerBoneState = 71,
    ServerPitonPlaced = 72,
    ServerPitonRemoved = 73,
    ServerPlayerFrame = 74,
    ServerClimbotFrame = 75,
    ServerWeatherState = 76,
    ServerLampState = 77,
    ServerPingPlaced = 78,
    ServerHandPose = 79,
    ServerTimeState = 80,
    ServerTeleport = 81,
    ServerRopeClip = 82,
    ServerCosmeticState = 83,
}

/// <summary>
/// Les valeurs de difficulte correspondent a l'enum `DifficultyTweakables.SelectedDifficulty`
/// de Cairn -- memes noms, memes valeurs int. On controle laquelle est active pour que
/// le client puisse se brancher directement sur les options de lancement du jeu.
/// </summary>
public enum GameDifficulty : int
{
    Invalid  = 0,
    Alpinist  = 1769420573,
    Explorer  = 766328718,
    FreeSolo  = -1944667143,
    FreeRoam  = 418187680,
}
