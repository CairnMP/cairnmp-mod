using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppInterop.Runtime;
using Il2CppSteamworks;

namespace CairnMultiplayerMod.Internal.Networking
{
    /// <summary>
    /// Represents a current member of the Steam lobby (another player or self).
    /// </summary>
    internal sealed class LobbyMember
    {
        public ulong SteamId { get; init; }
        public string Name { get; init; } = "";
        public bool IsHost { get; init; }
        public bool IsSelf { get; init; }
    }

    /// <summary>
    /// Encapsulates the entire Steam Matchmaking layer: create / join (by code or
    /// SteamID64) / browser / invites / leave. The Steam P2P transport is started
    /// by NetworkManager once the lobby is joined.
    ///
    /// All Steam callbacks are pumped by Cairn itself via
    /// <c>SteamAPI.RunCallbacks()</c> in its main loop — the mod has nothing
    /// to pump on its side.
    ///
    /// Threading: Steam callbacks fire on the Unity thread (because Cairn does the
    /// pump there), so there is no marshalling to do when touching Unity
    /// objects.
    /// </summary>
    internal sealed partial class SteamLobbyManager : IDisposable
    {
        // SetLobbyData keys used to filter CairnMP lobbies on the browser side
        // and to store the shareable code.
        private const string KeyCairnApp = "cairnmp_app";
        private const string KeyCode = "code";
        private const string KeyName = "name";
        private const string KeyHostName = "host_name";
        private const string KeyModVersion = "mod_version";
        private const string KeyProtocolVersion = "protocol_version";
        private const string KeyVisibility = "visibility"; // for the browser display
        private const string KeyStartNonce = "start_nonce";
        private const string KeyStartDifficulty = "start_difficulty";
        private const string KeyStartSkipTutorials = "start_skip_tutorials";
        private const string KeyStartSkipPractice = "start_skip_practice";
        private const string KeyStartAssistEnabled = "start_assist_enabled";
        private const int MaxLobbyBrowserResults = 50;
        private const int MaxJoinCodeResults = 10;
        private const float LobbyOperationTimeoutSeconds = 20f;

        // Short-code characters (excluding ambiguous 0/O, 1/I/l).
        private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        private const int CodeBlockLen = 4;

        // ── Current state ─────────────────────────────────────────────────────────

        public CSteamID CurrentLobbyId { get; private set; }
        public string CurrentRoomCode { get; private set; } = "";
        public string CurrentLobbyName { get; private set; } = "";
        public bool IsInLobby => CurrentLobbyId.IsValid();
        public bool IsHost { get; private set; }
        public ulong HostSteamId { get; private set; }
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
                catch (Exception exception)
                {
                    ModLog.SuppressedException("steam-lobby.read-local-persona-name", exception);
                }

                return string.IsNullOrWhiteSpace(ModConfig.PlayerName?.Value)
                    ? "Player"
                    : ModConfig.PlayerName.Value.Trim();
            }
        }

        /// <summary>Capacity of the current lobby (0 if not connected).</summary>
        public int MaxMembers
        {
            get
            {
                if (!IsInLobby) return 0;
                try { return SteamMatchmaking.GetLobbyMemberLimit(CurrentLobbyId); }
                catch (Exception exception)
                {
                    ModLog.SuppressedException("steam-lobby.read-member-limit", exception);
                    return 0;
                }
            }
        }


        private readonly List<LobbyMember> _members = new();

        // Pending configuration used to finalize the SetLobbyData writes once
        // LobbyCreated_t is received.
        private HostConfig _pendingHostConfig;

        // ── TaskCompletionSources ─────────────────────────────────────────────────
        // Only one operation at a time (the UI disables the button while Connecting).

        private TaskCompletionSource<bool> _createTcs;
        private TaskCompletionSource<bool> _joinTcs;
        private TaskCompletionSource<List<LobbyEntry>> _listTcs;
        // For JoinByCode: we first wait for the LobbyMatchList_t, then chain
        // a JoinLobby that resolves via LobbyEnter_t.
        private string _pendingJoinCode;
        private bool _pendingJoinCodeFallbackScan;
        private CSteamID _pendingJoinLobbyId;
        private string _lastStartNonce = "";
        private float _pendingOperationElapsed;
        private string _pendingOperationName = "";
        private bool _disposed;

        // ── Steam callbacks (kept referenced to avoid GC) ─────────────────────────

        private Callback<LobbyCreated_t> _cbLobbyCreated;
        private Callback<LobbyEnter_t> _cbLobbyEnter;
        private Callback<LobbyMatchList_t> _cbLobbyMatchList;
        private Callback<LobbyChatUpdate_t> _cbLobbyChatUpdate;
        private Callback<GameLobbyJoinRequested_t> _cbGameLobbyJoinRequested;
        private Callback<LobbyDataUpdate_t> _cbLobbyDataUpdate;

        // ── Public events ─────────────────────────────────────────────────────────

        public event Action<CSteamID> OnLobbyEntered;
        public event Action<string> OnLobbyError;
        public event Action OnLobbyLeft;
        public event Action OnMembersChanged;
        public event Action<ServerStartGame> OnStartRequested;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        private enum SteamApiLoadStatus
        {
            Loaded,
            Missing,
            LoadFailed,
        }

        private bool _isInitialized;
        private readonly SteamApiLoadStatus _steamApiStatus;
        private readonly string _steamApiLoadError;

        /// <summary>True when steam_api64.dll was found and loaded successfully.</summary>
        public bool IsSteamIntegrationAvailable => _steamApiStatus == SteamApiLoadStatus.Loaded;

        /// <summary>User-facing reason Steam matchmaking cannot initialize.</summary>
        public string SteamUnavailableReason => _steamApiStatus switch
        {
            SteamApiLoadStatus.Missing =>
                "Steam integration is missing (steam_api64.dll). Multiplayer requires the Steam build of Cairn; if this is a Steam installation, verify the game files.",
            SteamApiLoadStatus.LoadFailed =>
                $"Steam integration could not be loaded{_steamApiLoadError}. Verify the game files, then restart Cairn and Steam.",
            _ => "",
        };

        private const string MsgSteamNotReady =
            "Steam isn't ready. Make sure the Steam client is running, then try again in a few seconds.";

        /// <summary>Message explaining why a lobby operation cannot run yet.</summary>
        private string NotInitializedMessage() =>
            IsSteamIntegrationAvailable ? MsgSteamNotReady : SteamUnavailableReason;

        /// <summary>Guards a lobby entry point: emits a clear UI error and returns false
        /// when Steamworks isn't initialized.</summary>
        private bool EnsureSteamReady()
        {
            if (_disposed)
            {
                OnLobbyError?.Invoke("Steam matchmaking is shutting down.");
                return false;
            }
            if (_isInitialized) return true;
            OnLobbyError?.Invoke(NotInitializedMessage());
            return false;
        }

        // Deferred init: we don't attempt Steam in the ctor (too early in the
        // MelonLoader cycle — Cairn hasn't yet had time to call SteamAPI_Init
        // on the native side). We retry during the first frames of Pump().
        private float _initRetryTimer;
        private const float InitRetryInterval = 2f;  // seconds between attempts
        private const int InitMaxAttempts = 5;   // give up after 10 seconds
        private int _initAttempts;

        // Direct P/Invoke into steam_api64.dll — bypasses the Il2Cpp wrapper whose
        // marshaling of the returned bool can be faulty under IL2CPP .NET 6.
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("steam_api64", EntryPoint = "SteamAPI_IsSteamRunning", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool NativeSteamAPI_IsSteamRunning();

        [DllImport("steam_api64", EntryPoint = "SteamAPI_Init", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool NativeSteamAPI_Init();

        [DllImport("steam_api64", EntryPoint = "SteamAPI_GetHSteamPipe", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeSteamAPI_GetHSteamPipe();

        [DllImport("steam_api64", EntryPoint = "SteamAPI_GetHSteamUser", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeSteamAPI_GetHSteamUser();

        // API available since SDK 1.55 — returns an error code and a readable
        // message, unlike the opaque bool of SteamAPI_Init.
        [DllImport("steam_api64", EntryPoint = "SteamInternal_SteamAPI_Init", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int NativeSteamInternal_SteamAPI_Init(string pszVersions, byte[] pOutErrMsg);

        // Game root (Cairn.exe directory) — computed once at preload.
        private static string _gameRoot = "";

        /// <summary>steam_api64.dll ships with Cairn in Cairn_Data/Plugins/x86_64/
        /// — not next to Cairn.exe. Without an explicit pre-load, the P/Invoke fails to
        /// resolve it. Also tries to create steam_appid.txt if missing (required for
        /// SteamAPI.Init() to work outside of a Steam launch).</summary>
        private static SteamApiLoadStatus PreloadSteamApiDll(out string loadError)
        {
            loadError = "";
            try
            {
                // Path of the main module (Cairn.exe) — more reliable than
                // AppDomain.BaseDirectory under MelonLoader.
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                _gameRoot = string.IsNullOrEmpty(exePath)
                    ? (AppDomain.CurrentDomain.BaseDirectory ?? "")
                    : Path.GetDirectoryName(exePath) ?? "";

                var dllPath = Path.GetFullPath(Path.Combine(_gameRoot, "Cairn_Data", "Plugins", "x86_64", "steam_api64.dll"));
                ModLog.Debug($"[SteamLobby] Game root: {_gameRoot}");
                ModLog.Debug($"[SteamLobby] Loading steam_api64.dll from: {dllPath}");

                if (!File.Exists(dllPath))
                {
                    ModLog.Error($"[SteamLobby] steam_api64.dll not found at {dllPath}");
                    return SteamApiLoadStatus.Missing;
                }
                var handle = LoadLibraryW(dllPath);
                if (handle == IntPtr.Zero)
                {
                    var err = Marshal.GetLastWin32Error();
                    ModLog.Error($"[SteamLobby] LoadLibrary failed (win32 error {err}) for: {dllPath}");
                    loadError = $" (Win32 error {err})";
                    return SteamApiLoadStatus.LoadFailed;
                }
                ModLog.Debug($"[SteamLobby] steam_api64.dll loaded (handle=0x{handle:X}).");
                return SteamApiLoadStatus.Loaded;
            }
            catch (Exception ex)
            {
                ModLog.Error($"[SteamLobby] Preload threw: {ex}");
                loadError = $": {ex.Message}";
                return SteamApiLoadStatus.LoadFailed;
            }
        }

        // Cairn AppID only: matchmaking must stay within the game's space.
        private const string CairnSteamAppId = "1588550";
        private static string _activeAppId = CairnSteamAppId;

        /// <summary>Writes steam_appid.txt with the given appId to the game root.</summary>
        private static void WriteAppId(string appId)
        {
            try
            {
                var path = Path.Combine(_gameRoot, "steam_appid.txt");
                File.WriteAllText(path, appId);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"[SteamLobby] WriteAppId({appId}) failed: {ex.Message}");
            }
        }

        public SteamLobbyManager()
        {
            // DLL load + steam_appid.txt here; the Steam init is deferred to
            // TryInitializeSteam() via Pump() to let the game finish starting up.
            _steamApiStatus = PreloadSteamApiDll(out _steamApiLoadError);
            if (IsSteamIntegrationAvailable)
            {
                WriteAppId(CairnSteamAppId);
                Environment.SetEnvironmentVariable("SteamAppId", CairnSteamAppId);
            }
            else
            {
                ModLog.Warning($"[SteamLobby] {SteamUnavailableReason}");
            }
        }

        /// <summary>
        /// Attempts to initialize Steamworks via direct P/Invoke (bypasses the
        /// Il2Cpp wrapper whose marshaling can be faulty). Returns true on success.
        /// </summary>
        private bool TryInitializeSteam()
        {
            // Check 1: is Steam running?
            bool steamRunning = false;
            try { steamRunning = NativeSteamAPI_IsSteamRunning(); }
            catch (Exception ex) { ModLog.Warning($"[SteamLobby] IsSteamRunning threw: {ex.Message}"); }

            // Check 2: native context already established (non-zero pipe = init OK)?
            int nativePipe = 0;
            int nativeUser = 0;
            try
            {
                nativePipe = NativeSteamAPI_GetHSteamPipe();
                nativeUser = NativeSteamAPI_GetHSteamUser();
            }
            catch (Exception ex) { ModLog.Warning($"[SteamLobby] GetHSteam* threw: {ex.Message}"); }

            bool alreadyInited = nativePipe != 0 && nativeUser != 0;

            // Check 3: SteamAPI_Init looks for steam_appid.txt in the CWD —
            // if the launcher changed the CWD, the file is invisible to the SDK.
            string cwd = Environment.CurrentDirectory;
            ModLog.Debug($"[SteamLobby] TryInit: steamRunning={steamRunning} pipe={nativePipe} user={nativeUser} alreadyInited={alreadyInited}");
            ModLog.Debug($"[SteamLobby] CWD='{cwd}'  gameRoot='{_gameRoot}'  match={string.Equals(cwd, _gameRoot, StringComparison.OrdinalIgnoreCase)}");

            if (!steamRunning && !alreadyInited)
            {
                ModLog.Warning("[SteamLobby] Steam is not running — multiplayer requires Steam to be open.");
                return false;
            }

            bool nativeOk = false;
            bool initOk = false;
            string prevDir = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = _gameRoot;

                // If Cairn has already initialized Steam, we reuse that context. Retrying
                // SteamAPI_Init with a different AppID can isolate matchmaking into a
                // different Steam space and make lobbies impossible to join.
                if (alreadyInited)
                {
                    nativeOk = true;
                    try
                    {
                        _activeAppId = SteamUtils.GetAppID().m_AppId.ToString();
                    }
                    catch (Exception exception)
                    {
                        _activeAppId = CairnSteamAppId;
                        ModLog.SuppressedException("steam-lobby.read-active-app-id", exception);
                    }
                    ModLog.Debug($"[SteamLobby] Reusing existing Steam context (appId={_activeAppId}).");
                    if (!string.Equals(_activeAppId, CairnSteamAppId, StringComparison.Ordinal))
                        ModLog.Warning($"[SteamLobby] Existing Steam context uses appId={_activeAppId}; expected Cairn appId={CairnSteamAppId}.");
                }
                else
                {
                    // SteamAPI_Init() reads steam_appid.txt from the CWD: force Cairn.
                    foreach (var candidate in new[] { CairnSteamAppId })
                    {
                        WriteAppId(candidate);
                        Environment.SetEnvironmentVariable("SteamAppId", candidate);

                        bool ok = false;
                        try
                        {
                            // SDK >= 1.55: detailed API with an error message.
                            var errBuf = new byte[1024];
                            int initResult = NativeSteamInternal_SteamAPI_Init(null, errBuf);
                            string errMsg = System.Text.Encoding.ASCII.GetString(errBuf).TrimEnd('\0');
                            ok = initResult == 0;
                            ModLog.Debug($"[SteamLobby] SteamInternal_SteamAPI_Init(appId={candidate}) result={initResult} ({ResultName(initResult)}) msg='{errMsg}'");
                        }
                        catch (EntryPointNotFoundException)
                        {
                            // SDK < 1.55 — old bool-only API.
                            try { ok = NativeSteamAPI_Init(); }
                            catch (Exception ex) { ModLog.Warning($"[SteamLobby] NativeSteamAPI_Init threw: {ex.Message}"); }
                            ModLog.Debug($"[SteamLobby] NativeSteamAPI_Init(appId={candidate}) = {ok}");
                        }
                        catch (Exception ex)
                        {
                            ModLog.Warning($"[SteamLobby] Init threw: {ex.Message}");
                        }

                        if (ok)
                        {
                            _activeAppId = candidate;
                            nativeOk = true;
                            ModLog.Debug($"[SteamLobby] Native init succeeded with app ID {candidate}.");
                            break;
                        }
                    }
                }

                if (nativeOk)
                {
                    // Managed call required to populate CSteamAPIContext and make
                    // the accessors (SteamMatchmaking etc.) valid.
                    try
                    {
                        initOk = SteamAPI.Init();
                        ModLog.Debug($"[SteamLobby] SteamAPI.Init() (managed) = {initOk}");
                    }
                    catch (Exception ex) { ModLog.Error($"[SteamLobby] SteamAPI.Init() threw: {ex}"); }
                }
            }
            finally
            {
                Environment.CurrentDirectory = prevDir;
            }

            if (!initOk && !nativeOk && !alreadyInited)
            {
                ModLog.Error("[SteamLobby] Steam init failed for Cairn app ID.");
                return false;
            }

            try
            {
                // Il2CppInterop converts a managed delegate into an Il2Cpp wrapper via
                // DelegateSupport — the direct DispatchDelegate ctor expects an IntPtr.
                _cbLobbyCreated = Callback<LobbyCreated_t>.Create(
                    DelegateSupport.ConvertDelegate<Callback<LobbyCreated_t>.DispatchDelegate>(new Action<LobbyCreated_t>(OnLobbyCreatedCb)));
                _cbLobbyEnter = Callback<LobbyEnter_t>.Create(
                    DelegateSupport.ConvertDelegate<Callback<LobbyEnter_t>.DispatchDelegate>(new Action<LobbyEnter_t>(OnLobbyEnterCb)));
                _cbLobbyMatchList = Callback<LobbyMatchList_t>.Create(
                    DelegateSupport.ConvertDelegate<Callback<LobbyMatchList_t>.DispatchDelegate>(new Action<LobbyMatchList_t>(OnLobbyMatchListCb)));
                _cbLobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(
                    DelegateSupport.ConvertDelegate<Callback<LobbyChatUpdate_t>.DispatchDelegate>(new Action<LobbyChatUpdate_t>(OnLobbyChatUpdateCb)));
                _cbGameLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(
                    DelegateSupport.ConvertDelegate<Callback<GameLobbyJoinRequested_t>.DispatchDelegate>(new Action<GameLobbyJoinRequested_t>(OnGameLobbyJoinRequestedCb)));
                _cbLobbyDataUpdate = Callback<LobbyDataUpdate_t>.Create(
                    DelegateSupport.ConvertDelegate<Callback<LobbyDataUpdate_t>.DispatchDelegate>(new Action<LobbyDataUpdate_t>(OnLobbyDataUpdateCb)));
                _isInitialized = true;
                ModLog.Info("[SteamLobby] Callbacks registered — Steam matchmaking ready.");
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Error($"[SteamLobby] Callback registration failed: {ex}");
                return false;
            }
        }

        /// <summary>Pumps the Steam callbacks — called every frame by the bootstrap update loop.
        /// Also handles the deferred initialization to give Cairn time to
        /// set up its own Steam context before we try to attach to it.</summary>
        public void Pump(float dt = 0f)
        {
            // Deferred initialization: we keep retrying until the game has finished
            // starting up, then give up after InitMaxAttempts attempts.
            if (!_isInitialized)
            {
                // When the native API is missing or failed to load, retries cannot help.
                if (!IsSteamIntegrationAvailable) return;
                if (_initAttempts >= InitMaxAttempts) return;

                _initRetryTimer -= dt;
                if (_initRetryTimer > 0f) return;

                _initRetryTimer = InitRetryInterval;
                _initAttempts++;
                ModLog.Debug($"[SteamLobby] Init attempt {_initAttempts}/{InitMaxAttempts}...");
                if (TryInitializeSteam())
                    _initAttempts = 0;
                return;
            }

            try { SteamAPI.RunCallbacks(); }
            catch (Exception ex) { ModLog.Warning($"[SteamLobby] RunCallbacks failed: {ex.Message}"); }

            CheckOperationTimeouts(dt);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (IsInLobby)
            {
                try { SteamMatchmaking.LeaveLobby(CurrentLobbyId); }
                catch (Exception exception) { ModLog.SuppressedException("steam-lobby.leave-during-dispose", exception); }
            }

            ResolveCreate(false);
            ResolveJoin(false);
            var list = _listTcs;
            _listTcs = null;
            list?.TrySetResult(new List<LobbyEntry>());
            _pendingJoinCode = null;
            _pendingJoinCodeFallbackScan = false;
            _pendingJoinLobbyId = default;
            ClearOperationIfIdle();

            DisposeCallback("lobby-created", () => _cbLobbyCreated?.Dispose());
            DisposeCallback("lobby-enter", () => _cbLobbyEnter?.Dispose());
            DisposeCallback("lobby-list", () => _cbLobbyMatchList?.Dispose());
            DisposeCallback("lobby-members", () => _cbLobbyChatUpdate?.Dispose());
            DisposeCallback("lobby-invite", () => _cbGameLobbyJoinRequested?.Dispose());
            DisposeCallback("lobby-data", () => _cbLobbyDataUpdate?.Dispose());
            _cbLobbyCreated = null;
            _cbLobbyEnter = null;
            _cbLobbyMatchList = null;
            _cbLobbyChatUpdate = null;
            _cbGameLobbyJoinRequested = null;
            _cbLobbyDataUpdate = null;
            _isInitialized = false;
            ResetLobbyState();
        }

        private static void DisposeCallback(string name, Action dispose)
        {
            try { dispose(); }
            catch (Exception exception)
            {
                ModLog.Debug($"[SteamLobby] Could not dispose {name} callback: {exception.Message}");
            }
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>Creates a Steam lobby with the requested visibility and capacity.
        /// Resolves after LobbyCreated_t + metadata write + LobbyEnter_t.</summary>
        public Task<bool> CreateLobby(HostConfig cfg)
        {
            if (!EnsureSteamReady()) return Task.FromResult(false);
            if (_createTcs != null) return _createTcs.Task;
            if (HasPendingOperation())
                return BusyBoolTask("Another Steam lobby operation is already running.");

            if (IsInLobby)
            {
                ModLog.Warning("[SteamLobby] CreateLobby called while already in a lobby — leaving first.");
                Leave();
            }

            _createTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginOperation("Create lobby");
            _pendingHostConfig = cfg;
            var task = _createTcs.Task;

            var lobbyType = cfg.Visibility switch
            {
                LobbyVisibility.Public => ELobbyType.k_ELobbyTypePublic,
                LobbyVisibility.FriendsOnly => ELobbyType.k_ELobbyTypeFriendsOnly,
                LobbyVisibility.Private => ELobbyType.k_ELobbyTypePrivate,
                _ => ELobbyType.k_ELobbyTypeFriendsOnly,
            };
            var maxPlayers = Math.Max(2, Math.Min(cfg.MaxPlayers, 16));

            try
            {
                SteamMatchmaking.CreateLobby(lobbyType, maxPlayers);
                ModLog.Info($"[SteamLobby] CreateLobby requested ({lobbyType}, {maxPlayers} max).");
            }
            catch (Exception ex)
            {
                ModLog.Error($"[SteamLobby] CreateLobby threw: {ex}");
                FailCreate($"Steam error: {ex.Message}");
            }
            return task;
        }

        /// <summary>Joins a lobby via its short "XXXX-XXXX" code.
        /// First searches via RequestLobbyList filtered on the code, then JoinLobby.</summary>
        public Task<bool> JoinByCode(string code)
        {
            if (!EnsureSteamReady()) return Task.FromResult(false);
            if (_joinTcs != null) return _joinTcs.Task;
            if (HasPendingOperation())
                return BusyBoolTask("Another Steam lobby operation is already running.");
            if (string.IsNullOrWhiteSpace(code))
                return Task.FromResult(false);

            _joinTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginOperation("Join lobby by code");
            _pendingJoinCode = NormalizeCode(code);
            _pendingJoinCodeFallbackScan = false;
            _pendingJoinLobbyId = default;
            var task = _joinTcs.Task;

            try
            {
                RequestJoinCodeLobbyList(exactCodeFilter: true);
                ModLog.Info($"[SteamLobby] JoinByCode requested ('{_pendingJoinCode}').");
            }
            catch (Exception ex)
            {
                ModLog.Error($"[SteamLobby] JoinByCode threw: {ex}");
                FailJoin($"Steam error: {ex.Message}");
            }
            return task;
        }

        /// <summary>Joins a lobby via its SteamID64 (used from the browser or a
        /// Steam Friends invite).</summary>
        public Task<bool> JoinById(ulong lobbyId64)
        {
            if (!EnsureSteamReady()) return Task.FromResult(false);
            if (_joinTcs != null) return _joinTcs.Task;
            if (HasPendingOperation())
                return BusyBoolTask("Another Steam lobby operation is already running.");

            _joinTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                ModLog.Info($"[SteamLobby] JoinLobby({lobbyId64}) requested.");
            }
            catch (Exception ex)
            {
                ModLog.Error($"[SteamLobby] JoinById threw: {ex}");
                FailJoin($"Steam error: {ex.Message}");
            }
            return task;
        }

        /// <summary>Fetches the list of public CairnMP lobbies.</summary>
        public Task<List<LobbyEntry>> RequestLobbyList()
        {
            if (!EnsureSteamReady())
                return Task.FromResult(new List<LobbyEntry>());
            if (_listTcs != null) return _listTcs.Task;
            if (HasPendingOperation())
            {
                OnLobbyError?.Invoke("Another Steam lobby operation is already running.");
                return Task.FromResult(new List<LobbyEntry>());
            }

            _listTcs = new TaskCompletionSource<List<LobbyEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                ModLog.Info("[SteamLobby] RequestLobbyList sent.");
            }
            catch (Exception ex)
            {
                ModLog.Error($"[SteamLobby] RequestLobbyList threw: {ex}");
                var tcs = _listTcs;
                _listTcs = null;
                ClearOperationIfIdle();
                tcs.TrySetResult(new List<LobbyEntry>());
            }
            return task;
        }

        /// <summary>Leaves the current lobby. No-op if not in a lobby.</summary>
        public void Leave()
        {
            if (!IsInLobby) return;
            try { SteamMatchmaking.LeaveLobby(CurrentLobbyId); }
            catch (Exception ex) { ModLog.Warning($"[SteamLobby] LeaveLobby failed: {ex.Message}"); }

            ResetLobbyState();
            OnLobbyLeft?.Invoke();
        }

        /// <summary>Broadcasts a start order via the Steam lobby metadata.</summary>
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
                ModLog.Info($"[SteamLobby] Start broadcast nonce={nonce} difficulty={(GameDifficulty)start.Difficulty}.");
                RaiseStartSignal(nonce, start);
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Error($"[SteamLobby] BroadcastStart failed: {ex}");
                OnLobbyError?.Invoke($"Failed to start lobby: {ex.Message}");
                return false;
            }
        }

        // ── Steam callbacks ───────────────────────────────────────────────────────

        private void RebuildMembers()
        {
            _members.Clear();
            if (!IsInLobby) return;

            var selfId = SteamUser.GetSteamID().m_SteamID;
            var ownerId = SteamMatchmaking.GetLobbyOwner(CurrentLobbyId).m_SteamID;
            if (HostSteamId != 0 && ownerId != HostSteamId)
            {
                Leave();
                OnLobbyError?.Invoke("The host left. Create or join a new lobby to continue multiplayer.");
                return;
            }
            HostSteamId = ownerId;
            IsHost = ownerId == selfId;
            var count = SteamMatchmaking.GetNumLobbyMembers(CurrentLobbyId);
            for (int i = 0; i < count; i++)
            {
                var memberId = SteamMatchmaking.GetLobbyMemberByIndex(CurrentLobbyId, i);
                var name = SteamFriends.GetFriendPersonaName(memberId);
                if (string.IsNullOrEmpty(name)) name = $"Player{memberId.m_SteamID}";
                _members.Add(new LobbyMember
                {
                    SteamId = memberId.m_SteamID,
                    Name = name,
                    IsHost = memberId.m_SteamID == HostSteamId,
                    IsSelf = memberId.m_SteamID == selfId,
                });
            }
        }

        private void ResetLobbyState()
        {
            CurrentLobbyId = default;
            CurrentRoomCode = "";
            CurrentLobbyName = "";
            IsHost = false;
            HostSteamId = 0;
            _lastStartNonce = "";
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

        /// <summary>Generates an "XXXX-XXXX" code from the alphabet without ambiguous characters.</summary>
        private static string GenerateRoomCode()
        {
            var buf = new char[CodeBlockLen * 2 + 1];
            for (int i = 0, j = 0; i < CodeBlockLen * 2; i++, j++)
            {
                if (i == CodeBlockLen) buf[j++] = '-';
                buf[j] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
            }
            return new string(buf);
        }

        /// <summary>Normalizes an entered code (uppercase, strips internal spaces).</summary>
        private static string NormalizeCode(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var s = raw.Trim().ToUpperInvariant().Replace(" ", "");
            // Auto-insert the dash if missing and we have 8 characters.
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
}
