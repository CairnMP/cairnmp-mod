using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Core;
using CairnMultiplayerMod.UI;
using Il2CppInterop.Runtime;
using Il2CppSteamworks;

namespace CairnMultiplayerMod.Networking;

/// <summary>
/// Représente un membre courant du lobby Steam (autre joueur ou self).
/// </summary>
public sealed class LobbyMember
{
    public ulong  SteamId  { get; init; }
    public string Name     { get; init; } = "";
    public bool   IsHost   { get; init; }
    public bool   IsSelf   { get; init; }
}

/// <summary>
/// Encapsule toute la couche Steam Matchmaking : création / join (par code ou
/// SteamID64) / browser / invites / leave. Le transport P2P Steam est démarré
/// par NetworkManager une fois le lobby rejoint.
///
/// Tous les callbacks Steam sont pompés par Cairn lui-même via
/// <c>SteamAPI.RunCallbacks()</c> dans son boucle principal — le mod n'a rien
/// à pomper de son côté.
///
/// Threading : les callbacks Steam tirent sur le thread Unity (parce que Cairn
/// fait le pump là), donc pas de marshalling à faire pour toucher des objets
/// Unity.
/// </summary>
public sealed class SteamLobbyManager : IDisposable
{
    // Clés SetLobbyData utilisées pour filtrer les lobbies CairnMP côté browser
    // et pour stocker le code partageable.
    private const string KeyCairnApp   = "cairnmp_app";
    private const string KeyCode       = "code";
    private const string KeyName       = "name";
    private const string KeyHostName   = "host_name";
    private const string KeyModVersion = "mod_version";
    private const string KeyProtocolVersion = "protocol_version";
    private const string KeyVisibility = "visibility"; // pour le browser display
    private const string KeyStartNonce = "start_nonce";
    private const string KeyStartDifficulty = "start_difficulty";
    private const string KeyStartSkipTutorials = "start_skip_tutorials";
    private const string KeyStartSkipPractice = "start_skip_practice";
    private const string KeyStartAssistEnabled = "start_assist_enabled";
    private const int MaxLobbyBrowserResults = 50;
    private const int MaxJoinCodeResults = 10;
    private const float LobbyOperationTimeoutSeconds = 20f;

    // Caractères du code court (sans 0/O, 1/I/l ambigus).
    private const string CodeAlphabet  = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int    CodeBlockLen  = 4;
    private static readonly Random CodeRng = new();

    // ── État courant ──────────────────────────────────────────────────────────

    public CSteamID CurrentLobbyId    { get; private set; }
    public string   CurrentRoomCode   { get; private set; } = "";
    public string   CurrentLobbyName  { get; private set; } = "";
    public bool     IsInLobby         => CurrentLobbyId.IsValid();
    public bool     IsHost            { get; private set; }
    public ulong    HostSteamId       { get; private set; }
    public IReadOnlyList<LobbyMember> Members => _members;

    public string LocalPersonaName
    {
        get
        {
            try
            {
                var name = SteamFriends.GetPersonaName();
                if (!string.IsNullOrWhiteSpace(name))
                    return name.Trim();
            }
            catch
            {
                // Steam peut ne pas etre initialise pendant les toutes premieres frames.
            }

            return string.IsNullOrWhiteSpace(ModConfig.PlayerName?.Value)
                ? "Player"
                : ModConfig.PlayerName.Value.Trim();
        }
    }

    /// <summary>Capacité du lobby courant (0 si pas connecté).</summary>
    public int MaxMembers
    {
        get
        {
            if (!IsInLobby) return 0;
            try { return SteamMatchmaking.GetLobbyMemberLimit(CurrentLobbyId); }
            catch { return 0; }
        }
    }


    private readonly List<LobbyMember> _members = new();

    // Configuration en attente pour finaliser l'écriture des SetLobbyData une
    // fois LobbyCreated_t reçu.
    private HostConfig _pendingHostConfig;

    // ── TaskCompletionSources ─────────────────────────────────────────────────
    // Une seule opération à la fois (UI bloque le bouton pendant Connecting).

    private TaskCompletionSource<bool>             _createTcs;
    private TaskCompletionSource<bool>             _joinTcs;
    private TaskCompletionSource<List<LobbyEntry>> _listTcs;
    // Pour JoinByCode : on attend d'abord le LobbyMatchList_t, puis on chaîne
    // un JoinLobby qui résout via LobbyEnter_t.
    private string _pendingJoinCode;
    private bool _pendingJoinCodeFallbackScan;
    private CSteamID _pendingJoinLobbyId;
    private string _lastStartNonce = "";
    private float _pendingOperationElapsed;
    private string _pendingOperationName = "";

    // ── Callbacks Steam (gardés en référence pour éviter le GC) ───────────────

    private Callback<LobbyCreated_t>            _cbLobbyCreated;
    private Callback<LobbyEnter_t>              _cbLobbyEnter;
    private Callback<LobbyMatchList_t>          _cbLobbyMatchList;
    private Callback<LobbyChatUpdate_t>         _cbLobbyChatUpdate;
    private Callback<GameLobbyJoinRequested_t>  _cbGameLobbyJoinRequested;
    private Callback<LobbyDataUpdate_t>         _cbLobbyDataUpdate;

    // ── Events publics ────────────────────────────────────────────────────────

    public event Action<CSteamID>           OnLobbyEntered;
    public event Action<string>             OnLobbyError;
    public event Action                     OnLobbyLeft;
    public event Action                     OnMembersChanged;
    public event Action<ServerStartGame>    OnStartRequested;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private bool _isInitialized;
    // Init différée : on ne tente pas Steam dans le ctor (trop tôt dans le
    // cycle MelonLoader — Cairn n'a pas encore eu le temps d'appeler SteamAPI_Init
    // côté natif). On réessaie dans les premières frames de Pump().
    private float _initRetryTimer;
    private const float InitRetryInterval = 2f;  // secondes entre les tentatives
    private const int   InitMaxAttempts   = 5;   // abandonne après 10 secondes
    private int         _initAttempts;

    // P/Invoke direct sur steam_api64.dll — contourne le wrapper Il2Cpp dont le
    // marshaling du bool de retour peut être défaillant en IL2CPP .NET 6.
    [DllImport("kernel32",    SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("steam_api64", EntryPoint = "SteamAPI_IsSteamRunning",  CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool NativeSteamAPI_IsSteamRunning();

    [DllImport("steam_api64", EntryPoint = "SteamAPI_Init",           CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool NativeSteamAPI_Init();

    [DllImport("steam_api64", EntryPoint = "SteamAPI_GetHSteamPipe",  CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeSteamAPI_GetHSteamPipe();

    [DllImport("steam_api64", EntryPoint = "SteamAPI_GetHSteamUser",  CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeSteamAPI_GetHSteamUser();

    // API disponible depuis SDK 1.55 — retourne un code d'erreur et un message
    // lisible, contrairement au bool opaque de SteamAPI_Init.
    [DllImport("steam_api64", EntryPoint = "SteamInternal_SteamAPI_Init", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int NativeSteamInternal_SteamAPI_Init(string pszVersions, byte[] pOutErrMsg);

    // Racine du jeu (Cairn.exe directory) — calculé une seule fois au preload.
    private static string _gameRoot = "";

    /// <summary>steam_api64.dll est livré par Cairn dans Cairn_Data/Plugins/x86_64/
    /// — pas à côté de Cairn.exe. Sans pre-load explicite, le P/Invoke échoue à le
    /// résoudre. Aussi tente de créer steam_appid.txt si absent (nécessaire pour
    /// que SteamAPI.Init() fonctionne en dehors d'un lancement Steam).</summary>
    private static bool PreloadSteamApiDll()
    {
        try
        {
            // Chemin du module principal (Cairn.exe) — plus fiable que
            // AppDomain.BaseDirectory sous MelonLoader.
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            _gameRoot = string.IsNullOrEmpty(exePath)
                ? (AppDomain.CurrentDomain.BaseDirectory ?? "")
                : Path.GetDirectoryName(exePath) ?? "";

            var dllPath = Path.GetFullPath(Path.Combine(_gameRoot, "Cairn_Data", "Plugins", "x86_64", "steam_api64.dll"));
            Mod.LogDebug($"[SteamLobby] Game root: {_gameRoot}");
            Mod.LogDebug($"[SteamLobby] Loading steam_api64.dll from: {dllPath}");

            if (!File.Exists(dllPath))
            {
                Mod.Log.Error($"[SteamLobby] steam_api64.dll not found at {dllPath}");
                return false;
            }
            var handle = LoadLibraryW(dllPath);
            if (handle == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                Mod.Log.Error($"[SteamLobby] LoadLibrary failed (win32 error {err}) for: {dllPath}");
                return false;
            }
            Mod.LogDebug($"[SteamLobby] steam_api64.dll loaded (handle=0x{handle:X}).");
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[SteamLobby] Preload threw: {ex}");
            return false;
        }
    }

    // AppID Cairn uniquement : le matchmaking doit rester dans l'espace du jeu.
    private const string CairnSteamAppId = "1588550";
    private static string _activeAppId = CairnSteamAppId;

    /// <summary>Écrit steam_appid.txt avec l'appId donné dans la racine du jeu.</summary>
    private static void WriteAppId(string appId)
    {
        try
        {
            var path = Path.Combine(_gameRoot, "steam_appid.txt");
            File.WriteAllText(path, appId);
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[SteamLobby] WriteAppId({appId}) failed: {ex.Message}");
        }
    }

    public SteamLobbyManager()
    {
        // Chargement DLL + steam_appid.txt ici ; l'init Steam est différée dans
        // TryInitializeSteam() via Pump() pour laisser le jeu finir son démarrage.
        if (PreloadSteamApiDll())
        {
            WriteAppId(CairnSteamAppId);
            Environment.SetEnvironmentVariable("SteamAppId", CairnSteamAppId);
        }
    }

    /// <summary>
    /// Tente d'initialiser Steamworks via P/Invoke direct (contourne le wrapper
    /// Il2Cpp dont le marshaling peut être défaillant). Retourne vrai si réussi.
    /// </summary>
    private bool TryInitializeSteam()
    {
        // Vérification 1 : Steam en cours d'exécution ?
        bool steamRunning = false;
        try { steamRunning = NativeSteamAPI_IsSteamRunning(); }
        catch (Exception ex) { Mod.Log.Warning($"[SteamLobby] IsSteamRunning threw: {ex.Message}"); }

        // Vérification 2 : contexte natif déjà établi (pipe non-nul = init OK) ?
        int nativePipe = 0;
        int nativeUser = 0;
        try
        {
            nativePipe = NativeSteamAPI_GetHSteamPipe();
            nativeUser = NativeSteamAPI_GetHSteamUser();
        }
        catch (Exception ex) { Mod.Log.Warning($"[SteamLobby] GetHSteam* threw: {ex.Message}"); }

        bool alreadyInited = nativePipe != 0 && nativeUser != 0;

        // Vérification 3 : SteamAPI_Init cherche steam_appid.txt dans le CWD —
        // si le launcher a changé le CWD, le fichier est invisible pour le SDK.
        string cwd = Environment.CurrentDirectory;
        Mod.LogDebug($"[SteamLobby] TryInit: steamRunning={steamRunning} pipe={nativePipe} user={nativeUser} alreadyInited={alreadyInited}");
        Mod.LogDebug($"[SteamLobby] CWD='{cwd}'  gameRoot='{_gameRoot}'  match={string.Equals(cwd, _gameRoot, StringComparison.OrdinalIgnoreCase)}");

        if (!steamRunning && !alreadyInited)
        {
            Mod.Log.Warning("[SteamLobby] Steam is not running — multiplayer requires Steam to be open.");
            return false;
        }

        bool nativeOk = false;
        bool initOk   = false;
        string prevDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _gameRoot;

            // Si Cairn a déjà initialisé Steam, on réutilise ce contexte. Retenter
            // SteamAPI_Init avec un autre AppID peut isoler le matchmaking dans un
            // autre espace Steam et rendre les lobbies impossibles à rejoindre.
            if (alreadyInited)
            {
                nativeOk = true;
                try
                {
                    _activeAppId = SteamUtils.GetAppID().m_AppId.ToString();
                }
                catch
                {
                    _activeAppId = CairnSteamAppId;
                }
                Mod.LogDebug($"[SteamLobby] Reusing existing Steam context (appId={_activeAppId}).");
                if (!string.Equals(_activeAppId, CairnSteamAppId, StringComparison.Ordinal))
                    Mod.Log.Warning($"[SteamLobby] Existing Steam context uses appId={_activeAppId}; expected Cairn appId={CairnSteamAppId}.");
            }
            else
            {
                // SteamAPI_Init() lit steam_appid.txt dans le CWD : on force Cairn.
                foreach (var candidate in new[] { CairnSteamAppId })
                {
                    WriteAppId(candidate);
                    Environment.SetEnvironmentVariable("SteamAppId", candidate);

                    bool ok = false;
                    try
                    {
                        // SDK >= 1.55 : API détaillée avec message d'erreur.
                        var errBuf     = new byte[1024];
                        int initResult = NativeSteamInternal_SteamAPI_Init(null, errBuf);
                        string errMsg  = System.Text.Encoding.ASCII.GetString(errBuf).TrimEnd('\0');
                        ok = initResult == 0;
                        Mod.LogDebug($"[SteamLobby] SteamInternal_SteamAPI_Init(appId={candidate}) result={initResult} ({ResultName(initResult)}) msg='{errMsg}'");
                    }
                    catch (EntryPointNotFoundException)
                    {
                        // SDK < 1.55 — ancienne API bool uniquement.
                        try { ok = NativeSteamAPI_Init(); }
                        catch (Exception ex) { Mod.Log.Warning($"[SteamLobby] NativeSteamAPI_Init threw: {ex.Message}"); }
                        Mod.LogDebug($"[SteamLobby] NativeSteamAPI_Init(appId={candidate}) = {ok}");
                    }
                    catch (Exception ex)
                    {
                        Mod.Log.Warning($"[SteamLobby] Init threw: {ex.Message}");
                    }

                    if (ok)
                    {
                        _activeAppId = candidate;
                        nativeOk     = true;
                        Mod.LogDebug($"[SteamLobby] Native init succeeded with app ID {candidate}.");
                        break;
                    }
                }
            }

            if (nativeOk)
            {
                // Appel managé requis pour peupler CSteamAPIContext et rendre
                // les accesseurs (SteamMatchmaking etc.) valides.
                try
                {
                    initOk = SteamAPI.Init();
                    Mod.LogDebug($"[SteamLobby] SteamAPI.Init() (managed) = {initOk}");
                }
                catch (Exception ex) { Mod.Log.Error($"[SteamLobby] SteamAPI.Init() threw: {ex}"); }
            }
        }
        finally
        {
            Environment.CurrentDirectory = prevDir;
        }

        if (!initOk && !nativeOk && !alreadyInited)
        {
            Mod.Log.Error("[SteamLobby] Steam init failed for Cairn app ID.");
            return false;
        }

        try
        {
            // Il2CppInterop convertit un délégué managé en wrapper Il2Cpp avec
            // DelegateSupport — le ctor direct des DispatchDelegate attend un IntPtr.
            _cbLobbyCreated           = Callback<LobbyCreated_t>.Create(
                DelegateSupport.ConvertDelegate<Callback<LobbyCreated_t>.DispatchDelegate>(new Action<LobbyCreated_t>(OnLobbyCreatedCb)));
            _cbLobbyEnter             = Callback<LobbyEnter_t>.Create(
                DelegateSupport.ConvertDelegate<Callback<LobbyEnter_t>.DispatchDelegate>(new Action<LobbyEnter_t>(OnLobbyEnterCb)));
            _cbLobbyMatchList         = Callback<LobbyMatchList_t>.Create(
                DelegateSupport.ConvertDelegate<Callback<LobbyMatchList_t>.DispatchDelegate>(new Action<LobbyMatchList_t>(OnLobbyMatchListCb)));
            _cbLobbyChatUpdate        = Callback<LobbyChatUpdate_t>.Create(
                DelegateSupport.ConvertDelegate<Callback<LobbyChatUpdate_t>.DispatchDelegate>(new Action<LobbyChatUpdate_t>(OnLobbyChatUpdateCb)));
            _cbGameLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(
                DelegateSupport.ConvertDelegate<Callback<GameLobbyJoinRequested_t>.DispatchDelegate>(new Action<GameLobbyJoinRequested_t>(OnGameLobbyJoinRequestedCb)));
            _cbLobbyDataUpdate        = Callback<LobbyDataUpdate_t>.Create(
                DelegateSupport.ConvertDelegate<Callback<LobbyDataUpdate_t>.DispatchDelegate>(new Action<LobbyDataUpdate_t>(OnLobbyDataUpdateCb)));
            _isInitialized = true;
            Mod.Log.Msg("[SteamLobby] Callbacks registered — Steam matchmaking ready.");
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[SteamLobby] Callback registration failed: {ex}");
            return false;
        }
    }

    /// <summary>Pompe les callbacks Steam — appelé chaque frame depuis Mod.OnUpdate.
    /// Gère aussi l'initialisation différée pour laisser le temps à Cairn de
    /// configurer son propre contexte Steam avant qu'on tente de s'y rattacher.</summary>
    public void Pump(float dt = 0f)
    {
        // Initialisation différée : on réessaie tant que le jeu n'a pas fini de
        // démarrer, puis on abandonne après InitMaxAttempts tentatives.
        if (!_isInitialized)
        {
            if (_initAttempts >= InitMaxAttempts) return;

            _initRetryTimer -= dt;
            if (_initRetryTimer > 0f) return;

            _initRetryTimer = InitRetryInterval;
            _initAttempts++;
            Mod.LogDebug($"[SteamLobby] Init attempt {_initAttempts}/{InitMaxAttempts}...");
            TryInitializeSteam();
            return;
        }

        try { SteamAPI.RunCallbacks(); }
        catch (Exception ex) { Mod.Log.Warning($"[SteamLobby] RunCallbacks failed: {ex.Message}"); }

        CheckOperationTimeouts(dt);
    }

    public void Dispose()
    {
        try { _cbLobbyCreated?.Dispose(); } catch { }
        try { _cbLobbyEnter?.Dispose(); } catch { }
        try { _cbLobbyMatchList?.Dispose(); } catch { }
        try { _cbLobbyChatUpdate?.Dispose(); } catch { }
        try { _cbGameLobbyJoinRequested?.Dispose(); } catch { }
        try { _cbLobbyDataUpdate?.Dispose(); } catch { }
    }

    // ── API publique ──────────────────────────────────────────────────────────

    /// <summary>Crée un lobby Steam selon la visibilité et la capacité demandées.
    /// Résout après LobbyCreated_t + écriture des metadonnées + LobbyEnter_t.</summary>
    public Task<bool> CreateLobby(HostConfig cfg)
    {
        if (!_isInitialized)
        {
            OnLobbyError?.Invoke("Steamworks not initialized — is Steam running?");
            return Task.FromResult(false);
        }
        if (_createTcs != null) return _createTcs.Task;
        if (HasPendingOperation())
            return BusyBoolTask("Another Steam lobby operation is already running.");

        if (IsInLobby)
        {
            Mod.Log.Warning("[SteamLobby] CreateLobby called while already in a lobby — leaving first.");
            Leave();
        }

        _createTcs = new TaskCompletionSource<bool>();
        BeginOperation("Create lobby");
        _pendingHostConfig = cfg;

        var lobbyType = cfg.Visibility switch
        {
            LobbyVisibility.Public      => ELobbyType.k_ELobbyTypePublic,
            LobbyVisibility.FriendsOnly => ELobbyType.k_ELobbyTypeFriendsOnly,
            LobbyVisibility.Private     => ELobbyType.k_ELobbyTypePrivate,
            _                           => ELobbyType.k_ELobbyTypeFriendsOnly,
        };
        var maxPlayers = Math.Max(2, Math.Min(cfg.MaxPlayers, 16));

        try
        {
            SteamMatchmaking.CreateLobby(lobbyType, maxPlayers);
            Mod.Log.Msg($"[SteamLobby] CreateLobby requested ({lobbyType}, {maxPlayers} max).");
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[SteamLobby] CreateLobby threw: {ex}");
            FailCreate($"Steam error: {ex.Message}");
        }
        return _createTcs.Task;
    }

    /// <summary>Rejoint un lobby via son code court "XXXX-XXXX".
    /// Cherche d'abord via RequestLobbyList filtré sur le code, puis JoinLobby.</summary>
    public Task<bool> JoinByCode(string code)
    {
        if (_joinTcs != null) return _joinTcs.Task;
        if (HasPendingOperation())
            return BusyBoolTask("Another Steam lobby operation is already running.");
        if (string.IsNullOrWhiteSpace(code))
            return Task.FromResult(false);

        _joinTcs = new TaskCompletionSource<bool>();
        BeginOperation("Join lobby by code");
        _pendingJoinCode = NormalizeCode(code);
        _pendingJoinCodeFallbackScan = false;
        _pendingJoinLobbyId = default;
        var task = _joinTcs.Task;

        try
        {
            RequestJoinCodeLobbyList(exactCodeFilter: true);
            Mod.Log.Msg($"[SteamLobby] JoinByCode requested ('{_pendingJoinCode}').");
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[SteamLobby] JoinByCode threw: {ex}");
            FailJoin($"Steam error: {ex.Message}");
        }
        return task;
    }

    /// <summary>Rejoint un lobby via son SteamID64 (utilisé depuis le browser ou un
    /// invite Steam Friends).</summary>
    public Task<bool> JoinById(ulong lobbyId64)
    {
        if (_joinTcs != null) return _joinTcs.Task;
        if (HasPendingOperation())
            return BusyBoolTask("Another Steam lobby operation is already running.");

        _joinTcs = new TaskCompletionSource<bool>();
        BeginOperation("Join lobby");
        _pendingJoinCode = null;
        _pendingJoinCodeFallbackScan = false;
        _pendingJoinLobbyId = new CSteamID(lobbyId64);
        var task = _joinTcs.Task;

        try
        {
            if (!HasUsableSteamId(_pendingJoinLobbyId))
            {
                FailJoin("Steam returned an invalid lobby ID.");
                return task;
            }
            SteamMatchmaking.JoinLobby(_pendingJoinLobbyId);
            Mod.Log.Msg($"[SteamLobby] JoinLobby({lobbyId64}) requested.");
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[SteamLobby] JoinById threw: {ex}");
            FailJoin($"Steam error: {ex.Message}");
        }
        return task;
    }

    /// <summary>Récupère la liste des lobbies CairnMP publics.</summary>
    public Task<List<LobbyEntry>> RequestLobbyList()
    {
        if (_listTcs != null) return _listTcs.Task;
        if (HasPendingOperation())
        {
            OnLobbyError?.Invoke("Another Steam lobby operation is already running.");
            return Task.FromResult(new List<LobbyEntry>());
        }

        _listTcs = new TaskCompletionSource<List<LobbyEntry>>();
        BeginOperation("Browse lobbies");
        var task = _listTcs.Task;
        try
        {
            SteamMatchmaking.AddRequestLobbyListStringFilter(KeyCairnApp, "1", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(KeyProtocolVersion, Protocol.Version.ToString(CultureInfo.InvariantCulture), ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(KeyModVersion, Protocol.GameVersion, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(KeyVisibility, LobbyVisibility.Public.ToString(), ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(50);
            SteamMatchmaking.RequestLobbyList();
            Mod.Log.Msg("[SteamLobby] RequestLobbyList sent.");
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[SteamLobby] RequestLobbyList threw: {ex}");
            var tcs = _listTcs;
            _listTcs = null;
            ClearOperationIfIdle();
            tcs.TrySetResult(new List<LobbyEntry>());
        }
        return task;
    }

    /// <summary>Quitte le lobby courant. No-op si pas dans un lobby.</summary>
    public void Leave()
    {
        if (!IsInLobby) return;
        try { SteamMatchmaking.LeaveLobby(CurrentLobbyId); }
        catch (Exception ex) { Mod.Log.Warning($"[SteamLobby] LeaveLobby failed: {ex.Message}"); }

        ResetLobbyState();
        OnLobbyLeft?.Invoke();
    }

    /// <summary>Diffuse un ordre de lancement via les metadata du lobby Steam.</summary>
    public bool BroadcastStart(ServerStartGame start)
    {
        if (!IsInLobby)
        {
            OnLobbyError?.Invoke("Not connected to a lobby.");
            return false;
        }
        if (!IsHost)
        {
            OnLobbyError?.Invoke("Only the host can start the lobby.");
            return false;
        }

        var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        try
        {
            SteamMatchmaking.SetLobbyData(CurrentLobbyId, KeyStartDifficulty,
                start.Difficulty.ToString(CultureInfo.InvariantCulture));
            SteamMatchmaking.SetLobbyData(CurrentLobbyId, KeyStartSkipTutorials,
                start.SkipTutorials ? "true" : "false");
            SteamMatchmaking.SetLobbyData(CurrentLobbyId, KeyStartSkipPractice,
                start.SkipPractice ? "true" : "false");
            SteamMatchmaking.SetLobbyData(CurrentLobbyId, KeyStartAssistEnabled,
                start.AssistEnabled ? "true" : "false");
            SteamMatchmaking.SetLobbyData(CurrentLobbyId, KeyStartNonce, nonce);
            Mod.Log.Msg($"[SteamLobby] Start broadcast nonce={nonce} difficulty={(GameDifficulty)start.Difficulty}.");
            RaiseStartSignal(nonce, start);
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[SteamLobby] BroadcastStart failed: {ex}");
            OnLobbyError?.Invoke($"Failed to start lobby: {ex.Message}");
            return false;
        }
    }

    // ── Callbacks Steam ───────────────────────────────────────────────────────

    private void OnLobbyCreatedCb(LobbyCreated_t evt)
    {
        if (evt.m_eResult != EResult.k_EResultOK)
        {
            Mod.Log.Error($"[SteamLobby] LobbyCreated failed: {evt.m_eResult}");
            FailCreate($"Steam error: {evt.m_eResult}");
            return;
        }

        var lobbyId = new CSteamID(evt.m_ulSteamIDLobby);
        IsHost      = true;
        HostSteamId = SteamUser.GetSteamID().m_SteamID;

        // Code court partageable : on tente quelques fois en cas de collision.
        // (Probabilité ~0 sur 30^8 mais faible coût de retry.)
        var code = GenerateRoomCode();
        var cfg  = _pendingHostConfig ?? new HostConfig();
        var hostName = string.IsNullOrWhiteSpace(cfg.PlayerName)
            ? SteamFriends.GetPersonaName()
            : cfg.PlayerName;
        var lobbyName = string.IsNullOrWhiteSpace(cfg.LobbyName) ? $"{hostName}'s lobby" : cfg.LobbyName;

        try
        {
            SteamMatchmaking.SetLobbyData(lobbyId, KeyCairnApp,   "1");
            SteamMatchmaking.SetLobbyData(lobbyId, KeyCode,       code);
            SteamMatchmaking.SetLobbyData(lobbyId, KeyName,       lobbyName);
            SteamMatchmaking.SetLobbyData(lobbyId, KeyHostName,   hostName);
            SteamMatchmaking.SetLobbyData(lobbyId, KeyModVersion, ModVersion());
            SteamMatchmaking.SetLobbyData(lobbyId, KeyProtocolVersion, Protocol.Version.ToString(CultureInfo.InvariantCulture));
            SteamMatchmaking.SetLobbyData(lobbyId, KeyVisibility, cfg.Visibility.ToString());
            SteamMatchmaking.SetLobbyData(lobbyId, KeyStartNonce, "");
            SteamMatchmaking.SetLobbyMemberLimit(lobbyId, Math.Max(2, Math.Min(cfg.MaxPlayers, 16)));
            SteamMatchmaking.SetLobbyJoinable(lobbyId, true);
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[SteamLobby] SetLobbyData failed: {ex}");
        }

        CurrentLobbyId   = lobbyId;
        CurrentRoomCode  = code;
        CurrentLobbyName = lobbyName;

        Mod.Log.Msg($"[SteamLobby] Lobby created: id={lobbyId.m_SteamID} code={code} name='{lobbyName}'");

        // LobbyEnter_t va suivre automatiquement (le créateur entre dans son
        // propre lobby). On résout _createTcs là-bas pour avoir _members peuplé.
    }

    private void OnLobbyEnterCb(LobbyEnter_t evt)
    {
        if (evt.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
        {
            var reason = DescribeLobbyEnterFailure(evt.m_EChatRoomEnterResponse);
            Mod.Log.Error($"[SteamLobby] LobbyEnter failed: {reason} eventLobby={evt.m_ulSteamIDLobby} pendingLobby={_pendingJoinLobbyId.m_SteamID} appId={_activeAppId}");
            FailCreate(reason);
            FailJoin(reason);
            return;
        }

        var lobbyId = new CSteamID(evt.m_ulSteamIDLobby);
        CurrentLobbyId = lobbyId;
        HostSteamId    = SteamMatchmaking.GetLobbyOwner(lobbyId).m_SteamID;
        IsHost         = HostSteamId == SteamUser.GetSteamID().m_SteamID;

        if (!IsCompatibleLobby(lobbyId, out var incompatibilityReason))
        {
            Mod.Log.Warning($"[SteamLobby] Rejected incompatible lobby {lobbyId.m_SteamID}: {incompatibilityReason}");
            try { SteamMatchmaking.LeaveLobby(lobbyId); } catch { }
            ResetLobbyState();
            FailCreate(incompatibilityReason);
            FailJoin(incompatibilityReason);
            return;
        }

        // Pour un join (pas un create), on récupère le code et le nom depuis
        // les LobbyData déjà publiés par le host.
        if (string.IsNullOrEmpty(CurrentRoomCode))
            CurrentRoomCode = SteamMatchmaking.GetLobbyData(lobbyId, KeyCode);
        if (string.IsNullOrEmpty(CurrentLobbyName))
            CurrentLobbyName = SteamMatchmaking.GetLobbyData(lobbyId, KeyName);

        RebuildMembers();
        Mod.Log.Msg($"[SteamLobby] Entered lobby {lobbyId.m_SteamID} (host? {IsHost}, members={_members.Count}).");

        OnLobbyEntered?.Invoke(lobbyId);
        OnMembersChanged?.Invoke();
        TryHandleStartSignal();

        ResolveCreate(true);
        ResolveJoin(true);
        _pendingJoinLobbyId = default;
    }

    private void OnLobbyMatchListCb(LobbyMatchList_t evt)
    {
        // Cas 1 : on attend un JoinByCode → chaîner JoinLobby sur le premier
        // résultat (ou échouer si vide).
        if (_pendingJoinCode != null)
        {
            var context = _pendingJoinCodeFallbackScan ? "JoinByCodeFallback" : "JoinByCode";
            var maxResults = _pendingJoinCodeFallbackScan ? MaxLobbyBrowserResults : MaxJoinCodeResults;
            var results = ReadLobbyListResults(maxResults, context, evt.m_nLobbiesMatching);
            var count = results.Count;
            if (count == 0)
            {
                if (TryStartJoinCodeFallbackSearch())
                    return;

                var failedCode = _pendingJoinCode;
                _pendingJoinCode = null;
                _pendingJoinCodeFallbackScan = false;
                Mod.Log.Warning($"[SteamLobby] Code '{failedCode}' did not match any lobby.");
                FailJoin("Lobby not found. Ask the host to keep the lobby public or send a Steam invite.");
                return;
            }

            CSteamID lobbyId = default;
            for (int i = 0; i < results.Count; i++)
            {
                var candidate = results[i];
                var candidateCode = SafeLobbyData(candidate, KeyCode);
                var candidateName = SafeLobbyData(candidate, KeyName);
                var candidateHost = SafeLobbyData(candidate, KeyHostName);
                var isValid = HasUsableSteamId(candidate);
                Mod.LogDebug($"[SteamLobby] JoinByCode candidate index={i} id={candidate.m_SteamID} valid={isValid} code='{candidateCode}' name='{candidateName}' host='{candidateHost}' appId={_activeAppId}");

                if (!isValid) continue;
                var normalizedCandidateCode = NormalizeCode(candidateCode);
                if (!string.Equals(normalizedCandidateCode, _pendingJoinCode, StringComparison.OrdinalIgnoreCase))
                {
                    if (!_pendingJoinCodeFallbackScan && results.Count == 1 && string.IsNullOrEmpty(normalizedCandidateCode))
                    {
                        Mod.Log.Warning("[SteamLobby] Single code-filtered lobby candidate has no readable code metadata; joining by Steam result ID.");
                    }
                    else
                    {
                        continue;
                    }
                }

                lobbyId = candidate;
                break;
            }

            if (!HasUsableSteamId(lobbyId))
            {
                if (TryStartJoinCodeFallbackSearch())
                    return;

                var failedCode = _pendingJoinCode;
                _pendingJoinCode = null;
                _pendingJoinCodeFallbackScan = false;
                Mod.Log.Warning($"[SteamLobby] Code '{failedCode}' returned {count} lobby result(s), but none matched the requested code.");
                FailJoin("Lobby not found. Ask the host to keep the lobby public or send a Steam invite.");
                return;
            }

            _pendingJoinCode = null;
            _pendingJoinCodeFallbackScan = false;
            _pendingJoinLobbyId = lobbyId;
            try
            {
                SteamMatchmaking.JoinLobby(lobbyId);
                Mod.LogDebug($"[SteamLobby] JoinLobby({lobbyId.m_SteamID}) requested from code search.");
            }
            catch (Exception ex) { FailJoin($"Steam error: {ex.Message}"); }
            return;
        }

        // Cas 2 : RequestLobbyList pour le browser.
        if (_listTcs != null)
        {
            var results = ReadLobbyListResults(MaxLobbyBrowserResults, "RequestLobbyList", evt.m_nLobbiesMatching);
            var list = new List<LobbyEntry>(results.Count);
            for (int i = 0; i < results.Count; i++)
            {
                var id   = results[i];
                if (!HasUsableSteamId(id)) continue;
                var name = SteamMatchmaking.GetLobbyData(id, KeyName);
                var host = SteamMatchmaking.GetLobbyData(id, KeyHostName);
                var cap  = SteamMatchmaking.GetLobbyMemberLimit(id);
                var cnt  = SteamMatchmaking.GetNumLobbyMembers(id);
                list.Add(new LobbyEntry
                {
                    LobbyId     = id.m_SteamID,
                    Name        = string.IsNullOrEmpty(name) ? "(unnamed)" : name,
                    HostName    = host ?? "",
                    PlayerCount = cnt,
                    MaxPlayers  = cap,
                    Region      = "",
                });
            }
            var tcs = _listTcs;
            _listTcs = null;
            ClearOperationIfIdle();
            tcs.TrySetResult(list);
        }
    }

    private static List<CSteamID> ReadLobbyListResults(int maxResults, string context, uint callbackCount)
    {
        // Le wrapper IL2CPP de LobbyMatchList_t peut remonter un compteur corrompu.
        // On lit donc les slots Steam de facon bornee et on s'arrete au premier
        // ID invalide apres avoir trouve au moins un resultat.
        var reported = unchecked((int)callbackCount);
        if (reported < 0 || reported > maxResults)
            Mod.Log.Warning($"[SteamLobby] {context} callback count looked invalid ({reported}); scanning up to {maxResults}.");

        var scanLimit = reported >= 0 && reported <= maxResults ? reported : maxResults;
        var results = new List<CSteamID>(Math.Max(0, scanLimit));
        for (int i = 0; i < scanLimit; i++)
        {
            CSteamID id;
            try { id = SteamMatchmaking.GetLobbyByIndex(i); }
            catch (Exception ex)
            {
                Mod.Log.Warning($"[SteamLobby] {context} GetLobbyByIndex({i}) failed: {ex.Message}");
                break;
            }

            if (!HasUsableSteamId(id))
            {
                if (results.Count > 0) break;
                continue;
            }
            results.Add(id);
        }

        return results;
    }

    private void OnLobbyChatUpdateCb(LobbyChatUpdate_t evt)
    {
        if (!IsInLobby || evt.m_ulSteamIDLobby != CurrentLobbyId.m_SteamID) return;
        RebuildMembers();
        OnMembersChanged?.Invoke();
    }

    private void OnLobbyDataUpdateCb(LobbyDataUpdate_t evt)
    {
        if (!IsInLobby || evt.m_ulSteamIDLobby != CurrentLobbyId.m_SteamID) return;
        // Quand un member update son LobbyMemberData (ex: nom mis à jour) on
        // peut rafraîchir la liste pour refléter les changements de pseudo.
        RebuildMembers();
        OnMembersChanged?.Invoke();
        TryHandleStartSignal();
    }

    private void OnGameLobbyJoinRequestedCb(GameLobbyJoinRequested_t evt)
    {
        // Un ami clique "Join Game" depuis l'overlay Steam → join direct.
        Mod.Log.Msg($"[SteamLobby] GameLobbyJoinRequested for {evt.m_steamIDLobby.m_SteamID}.");
        _ = JoinById(evt.m_steamIDLobby.m_SteamID);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void TryHandleStartSignal()
    {
        if (!IsInLobby) return;

        var nonce = SafeLobbyData(CurrentLobbyId, KeyStartNonce);
        if (string.IsNullOrEmpty(nonce) || string.Equals(nonce, _lastStartNonce, StringComparison.Ordinal))
            return;

        var start = new ServerStartGame
        {
            Difficulty = ParseIntLobbyData(KeyStartDifficulty, (int)GameDifficulty.Explorer),
            SkipTutorials = ParseBoolLobbyData(KeyStartSkipTutorials, true),
            SkipPractice = ParseBoolLobbyData(KeyStartSkipPractice, true),
            AssistEnabled = ParseBoolLobbyData(KeyStartAssistEnabled, false),
        };
        RaiseStartSignal(nonce, start);
    }

    private void RaiseStartSignal(string nonce, ServerStartGame start)
    {
        if (string.IsNullOrEmpty(nonce) || string.Equals(nonce, _lastStartNonce, StringComparison.Ordinal))
            return;

        _lastStartNonce = nonce;
        Mod.Log.Msg($"[SteamLobby] Start received nonce={nonce} difficulty={(GameDifficulty)start.Difficulty}.");
        OnStartRequested?.Invoke(start);
    }

    private int ParseIntLobbyData(string key, int fallback)
    {
        var raw = SafeLobbyData(CurrentLobbyId, key);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private bool ParseBoolLobbyData(string key, bool fallback)
    {
        var raw = SafeLobbyData(CurrentLobbyId, key);
        return bool.TryParse(raw, out var value) ? value : fallback;
    }

    private void RequestJoinCodeLobbyList(bool exactCodeFilter)
    {
        SteamMatchmaking.AddRequestLobbyListStringFilter(KeyCairnApp, "1", ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListStringFilter(KeyProtocolVersion, Protocol.Version.ToString(CultureInfo.InvariantCulture), ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListStringFilter(KeyModVersion, Protocol.GameVersion, ELobbyComparison.k_ELobbyComparisonEqual);
        if (exactCodeFilter)
            SteamMatchmaking.AddRequestLobbyListStringFilter(KeyCode, _pendingJoinCode, ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
        SteamMatchmaking.AddRequestLobbyListResultCountFilter(exactCodeFilter ? MaxJoinCodeResults : MaxLobbyBrowserResults);
        SteamMatchmaking.RequestLobbyList();
    }

    private bool TryStartJoinCodeFallbackSearch()
    {
        if (_pendingJoinCodeFallbackScan)
            return false;

        _pendingJoinCodeFallbackScan = true;
        try
        {
            RequestJoinCodeLobbyList(exactCodeFilter: false);
            Mod.LogDebug($"[SteamLobby] JoinByCode fallback scan requested for '{_pendingJoinCode}'.");
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[SteamLobby] JoinByCode fallback scan failed: {ex}");
            return false;
        }
    }

    private bool HasPendingOperation() => _createTcs != null || _joinTcs != null || _listTcs != null;

    private Task<bool> BusyBoolTask(string message)
    {
        OnLobbyError?.Invoke(message);
        return Task.FromResult(false);
    }

    private void BeginOperation(string name)
    {
        _pendingOperationName = name;
        _pendingOperationElapsed = 0f;
    }

    private void ClearOperationIfIdle()
    {
        if (HasPendingOperation()) return;
        _pendingOperationName = "";
        _pendingOperationElapsed = 0f;
    }

    private void CheckOperationTimeouts(float dt)
    {
        if (!HasPendingOperation())
        {
            ClearOperationIfIdle();
            return;
        }

        _pendingOperationElapsed += Math.Max(0f, dt);
        if (_pendingOperationElapsed < LobbyOperationTimeoutSeconds)
            return;

        var name = string.IsNullOrEmpty(_pendingOperationName) ? "Steam lobby operation" : _pendingOperationName;
        var message = $"{name} timed out. Try again in a moment.";

        if (_createTcs != null)
            FailCreate(message);
        if (_joinTcs != null)
            FailJoin(message);
        if (_listTcs != null)
        {
            var tcs = _listTcs;
            _listTcs = null;
            ClearOperationIfIdle();
            OnLobbyError?.Invoke(message);
            tcs.TrySetResult(new List<LobbyEntry>());
        }
    }

    private static bool IsCompatibleLobby(CSteamID lobbyId, out string reason)
    {
        var protocolRaw = SafeLobbyData(lobbyId, KeyProtocolVersion);
        if (!int.TryParse(protocolRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lobbyProtocol)
            || lobbyProtocol != Protocol.Version)
        {
            reason = $"Lobby protocol {protocolRaw} is incompatible with this mod protocol {Protocol.Version}.";
            return false;
        }

        var modVersion = SafeLobbyData(lobbyId, KeyModVersion);
        if (!string.Equals(modVersion, Protocol.GameVersion, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Lobby mod version {modVersion} is incompatible with this mod version {Protocol.GameVersion}.";
            return false;
        }

        reason = "";
        return true;
    }

    private static bool HasUsableSteamId(CSteamID lobbyId)
    {
        // Les IDs renvoyes par SteamMatchmaking.GetLobbyByIndex sont l'autorite.
        // Sur IL2CPP, CSteamID.IsLobby() peut retourner faux pour un ID pourtant joignable.
        return lobbyId.m_SteamID != 0;
    }

    private static string SafeLobbyData(CSteamID lobbyId, string key)
    {
        if (!HasUsableSteamId(lobbyId)) return "";
        try { return SteamMatchmaking.GetLobbyData(lobbyId, key) ?? ""; }
        catch { return ""; }
    }

    private static string DescribeLobbyEnterFailure(uint response)
    {
        if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist)
            return "Lobby is not visible to Steam. Ask the host to use Public visibility or send a Steam invite.";
        if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseNotAllowed)
            return "You are not allowed to join this lobby.";
        if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseFull)
            return "Lobby is full.";
        if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseError)
            return "Steam returned a lobby join error.";
        if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseBanned)
            return "You are banned from this lobby.";
        if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseLimited)
            return "Your Steam account is limited and cannot join this lobby.";
        if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseMemberBlockedYou)
            return "A lobby member has blocked you.";
        if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseYouBlockedMember)
            return "You have blocked a lobby member.";
        if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseRatelimitExceeded)
            return "Steam rate limit exceeded. Try again in a moment.";
        return $"Steam lobby join failed (response {response}).";
    }

    private void RebuildMembers()
    {
        _members.Clear();
        if (!IsInLobby) return;

        var selfId = SteamUser.GetSteamID().m_SteamID;
        var ownerId = SteamMatchmaking.GetLobbyOwner(CurrentLobbyId).m_SteamID;
        HostSteamId = ownerId;
        IsHost = ownerId == selfId;
        var count  = SteamMatchmaking.GetNumLobbyMembers(CurrentLobbyId);
        for (int i = 0; i < count; i++)
        {
            var memberId = SteamMatchmaking.GetLobbyMemberByIndex(CurrentLobbyId, i);
            var name     = SteamFriends.GetFriendPersonaName(memberId);
            if (string.IsNullOrEmpty(name)) name = $"Player{memberId.m_SteamID}";
            _members.Add(new LobbyMember
            {
                SteamId = memberId.m_SteamID,
                Name    = name,
                IsHost  = memberId.m_SteamID == HostSteamId,
                IsSelf  = memberId.m_SteamID == selfId,
            });
        }
    }

    private void ResetLobbyState()
    {
        CurrentLobbyId   = default;
        CurrentRoomCode  = "";
        CurrentLobbyName = "";
        IsHost           = false;
        HostSteamId      = 0;
        _lastStartNonce  = "";
        _members.Clear();
        _pendingHostConfig = null;
    }

    private void ResolveCreate(bool ok)
    {
        var tcs = _createTcs;
        _createTcs = null;
        ClearOperationIfIdle();
        tcs?.TrySetResult(ok);
    }

    private void ResolveJoin(bool ok)
    {
        var tcs = _joinTcs;
        _joinTcs = null;
        if (!ok) _pendingJoinCodeFallbackScan = false;
        if (!ok) _pendingJoinLobbyId = default;
        ClearOperationIfIdle();
        tcs?.TrySetResult(ok);
    }

    private void FailCreate(string err)
    {
        var hadTcs = _createTcs != null;
        ResolveCreate(false);
        ResetLobbyState();
        if (hadTcs) OnLobbyError?.Invoke(err);
    }

    private void FailJoin(string err)
    {
        var hadTcs = _joinTcs != null;
        ResolveJoin(false);
        if (hadTcs) OnLobbyError?.Invoke(err);
    }

    /// <summary>Génère un code "XXXX-XXXX" sur l'alphabet sans caractères ambigus.</summary>
    private static string GenerateRoomCode()
    {
        var buf = new char[CodeBlockLen * 2 + 1];
        for (int i = 0, j = 0; i < CodeBlockLen * 2; i++, j++)
        {
            if (i == CodeBlockLen) buf[j++] = '-';
            buf[j] = CodeAlphabet[CodeRng.Next(CodeAlphabet.Length)];
        }
        return new string(buf);
    }

    /// <summary>Normalise un code saisi (uppercase, supprime les espaces internes).</summary>
    private static string NormalizeCode(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = raw.Trim().ToUpperInvariant().Replace(" ", "");
        // Auto-insert du tiret si absent et qu'on a 8 caractères.
        if (!s.Contains('-') && s.Length == CodeBlockLen * 2)
            s = s.Substring(0, CodeBlockLen) + "-" + s.Substring(CodeBlockLen);
        return s;
    }

    private static string ModVersion()
    {
        return Protocol.GameVersion;
    }

    private static string ResultName(int r) => r switch
    {
        0 => "OK",
        1 => "FailedGeneric",
        2 => "NoSteamClient",
        3 => "VersionMismatch",
        _ => $"Unknown({r})",
    };
}
