using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.UI.Toolkit;

public sealed class UiToolkitMultiplayerPanel : IMultiplayerPanel
{
    private enum Screen
    {
        Home,
        Browser,
        Connected,
    }

    private GameObject _documentGo;
    private UIDocument _document;
    private PanelSettings _panelSettings;
    private VisualElement _root;
    private VisualElement _card;
    private VisualElement _screenHost;
    private VisualElement _homeScreen;
    private VisualElement _browserScreen;
    private VisualElement _connectedScreen;
    private Label _statusLabel;

    private Label _steamNameLabel;
    private TextField _lobbyNameField;
    private TextField _codeField;
    private SegmentControl _homeModeSegment;
    private VisualElement _hostPane;
    private VisualElement _joinPane;
    private VisualElement _browsePane;
    private Button _createButton;
    private Button _joinButton;
    private Button _browseButton;
    private Button _refreshButton;
    private StepperControl _slotsStepper;
    private SegmentControl _visibilitySegment;

    private VisualElement _lobbyList;
    private Label _browserEmptyLabel;

    private Label _connectedLobbyTitle;
    private Label _connectedCode;
    private Label _connectedPlayers;
    private Label _connectedCount;
    private Button _copyButton;
    private Button _startButton;
    private Label _startHint;

    private Screen _currentScreen = Screen.Home;
    private bool _visible;
    private bool _isConnected;
    private bool _isConnecting;
    private string _statusText = "";
    private string _lobbyName = "";
    private string _lastDefaultLobbyName = "";

    public event Action<HostConfig> OnHostRequested;
    public event Action<string, string> OnJoinByCodeRequested;
    public event Action OnBrowseRequested;
    public event Action<ulong> OnJoinByLobbyIdRequested;
    public event Action OnDisconnectRequested;
    public event Action OnStartRequested;
    public event Action OnPanelClosed;

    public bool IsVisible => _visible;

    public UiToolkitMultiplayerPanel()
    {
    }

    public void Show()
    {
        _visible = true;
        EnsureDocumentReady();
        SetVisible(true);
        UpdateSteamProfile();
        UpdateScreenVisibility();
        UpdateStatus();
        UpdateInteractable();
    }

    public void Hide()
    {
        _visible = false;
        DestroyResources();
        OnPanelClosed?.Invoke();
    }

    public void Toggle()
    {
        if (_visible) Hide();
        else Show();
    }

    public void SetStatus(string status, bool connected)
    {
        _statusText = status ?? "";
        _isConnected = connected;
        _isConnecting = false;

        if (connected)
            _currentScreen = Screen.Connected;
        else if (_currentScreen == Screen.Connected)
            _currentScreen = Screen.Home;

        UpdateScreenVisibility();
        UpdateStatus();
        UpdateInteractable();
    }

    public void SetConnecting(string status)
    {
        _statusText = status ?? "";
        _isConnecting = true;
        UpdateStatus();
        UpdateInteractable();
    }

    public void SetBrowserLobbies(IReadOnlyList<LobbyEntry> lobbies)
    {
        RebuildLobbyList(lobbies ?? Array.Empty<LobbyEntry>());
        SetRefreshing(false);
    }

    public void SetCurrentLobbyName(string name)
    {
        _lobbyName = name ?? "";
    }

    public void Tick(float dt)
    {
        if (!_visible) return;
        UpdateSteamProfile();
        if (_currentScreen == Screen.Connected)
            RefreshConnected(Mod.Instance?.Lobby, GetLocalPlayerName());
    }

    public void OnGUI()
    {
    }

    public void DestroyResources()
    {
        _visible = false;

        if (_documentGo != null)
            Object.Destroy(_documentGo);

        _documentGo = null;
        _document = null;
        _panelSettings = null;
        _root = null;
        _card = null;
        _screenHost = null;
        _homeScreen = null;
        _browserScreen = null;
        _connectedScreen = null;
        _statusLabel = null;
        _steamNameLabel = null;
        _lobbyNameField = null;
        _codeField = null;
        _homeModeSegment = null;
        _hostPane = null;
        _joinPane = null;
        _browsePane = null;
        _createButton = null;
        _joinButton = null;
        _browseButton = null;
        _refreshButton = null;
        _slotsStepper = null;
        _visibilitySegment = null;
        _lobbyList = null;
        _browserEmptyLabel = null;
        _connectedLobbyTitle = null;
        _connectedCode = null;
        _connectedPlayers = null;
        _connectedCount = null;
        _copyButton = null;
        _startButton = null;
        _startHint = null;
    }

    private void BuildDocument()
    {
        if (CairnUi.Font == null)
            throw new InvalidOperationException("UI Toolkit font unavailable");

        _documentGo = new GameObject("MP_UIToolkit");
        Object.DontDestroyOnLoad(_documentGo);

        _panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
        _panelSettings.name = "MP_UIToolkitPanelSettings";
        _panelSettings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        _panelSettings.referenceResolution = new Vector2Int(1920, 1080);
        _panelSettings.sortingOrder = 210;

        _document = _documentGo.AddComponent<UIDocument>();
        _document.panelSettings = _panelSettings;

        _root = _document.rootVisualElement;
        CairnUi.FullScreen(_root);
    }

    private void BuildLayout()
    {
        _root.Clear();

        var overlay = new VisualElement();
        CairnUi.FullScreen(overlay);
        overlay.style.backgroundColor = CairnUi.Overlay;
        overlay.style.alignItems = Align.Center;
        overlay.style.justifyContent = Justify.Center;
        _root.Add(overlay);

        _card = new VisualElement();
        _card.style.width = 760;
        _card.style.height = 640;
        _card.style.backgroundColor = CairnUi.CardBg;
        _card.style.borderTopLeftRadius = 6;
        _card.style.borderTopRightRadius = 6;
        _card.style.borderBottomLeftRadius = 6;
        _card.style.borderBottomRightRadius = 6;
        _card.style.borderTopWidth = 1;
        _card.style.borderRightWidth = 1;
        _card.style.borderBottomWidth = 1;
        _card.style.borderLeftWidth = 1;
        CairnUi.SetBorderColor(_card, CairnUi.Border);
        _card.style.paddingTop = 24;
        _card.style.paddingRight = 28;
        _card.style.paddingBottom = 18;
        _card.style.paddingLeft = 28;
        overlay.Add(_card);

        BuildHeader();

        _screenHost = new VisualElement();
        _screenHost.style.flexGrow = 1;
        _screenHost.style.marginTop = 18;
        _screenHost.style.marginBottom = 12;
        _card.Add(_screenHost);

        BuildHomeScreen();
        BuildBrowserScreen();
        BuildConnectedScreen();

        _statusLabel = CairnUi.Label("", 11, CairnUi.TextMuted, FontStyle.Italic);
        _statusLabel.style.height = 24;
        _statusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        _card.Add(_statusLabel);
    }

    private void BuildHeader()
    {
        var header = new VisualElement();
        header.style.flexDirection = FlexDirection.Row;
        header.style.alignItems = Align.Center;
        header.style.height = 38;
        _card.Add(header);

        var titleBox = new VisualElement();
        titleBox.style.flexGrow = 1;
        header.Add(titleBox);

        var eyebrow = CairnUi.Label("CAIRN MULTIPLAYER", 10, CairnUi.TextMuted);
        eyebrow.style.height = 14;
        titleBox.Add(eyebrow);

        var title = CairnUi.Label("Play together", 24, CairnUi.TextPrimary);
        title.style.height = 28;
        titleBox.Add(title);

        var closeButton = CairnUi.Button("X", Hide);
        closeButton.style.width = 42;
        closeButton.style.height = 34;
        closeButton.style.backgroundColor = Color.clear;
        closeButton.style.color = CairnUi.TextMuted;
        header.Add(closeButton);
    }

    private void BuildHomeScreen()
    {
        _homeScreen = CreateScreen();
        _screenHost.Add(_homeScreen);

        var profileRow = BuildSteamProfileRow();
        _homeScreen.Add(profileRow);

        _homeModeSegment = new SegmentControl(new[] { "Host", "Join", "Browse" }, 0);
        _homeModeSegment.Root.style.marginBottom = 12;
        _homeModeSegment.Changed += _ => UpdateHomeMode();
        _homeScreen.Add(_homeModeSegment.Root);

        BuildHostCard();
        BuildJoinCard();
        BuildBrowseCard();
        UpdateHomeMode();
        UpdateSteamProfile();
    }

    private VisualElement BuildSteamProfileRow()
    {
        var row = CairnUi.Card();
        row.style.marginBottom = 12;
        row.style.paddingTop = 10;
        row.style.paddingBottom = 10;
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Center;

        var text = Column();
        text.style.flexGrow = 1;
        row.Add(text);

        text.Add(CairnUi.MicroLabel("STEAM PROFILE"));
        _steamNameLabel = CairnUi.Label(GetLocalPlayerName(), 15, CairnUi.TextPrimary, FontStyle.Bold);
        _steamNameLabel.style.height = 20;
        text.Add(_steamNameLabel);

        var hint = CairnUi.Label("Used automatically in lobbies", 11, CairnUi.TextDim, FontStyle.Italic);
        hint.style.unityTextAlign = TextAnchor.MiddleRight;
        hint.style.width = 220;
        row.Add(hint);

        return row;
    }

    private void BuildHostCard()
    {
        var card = CairnUi.Card();
        card.style.flexGrow = 1;
        _homeScreen.Add(card);
        _hostPane = card;

        var title = CairnUi.Label("Host a climb", 18, CairnUi.TextPrimary);
        title.style.marginBottom = 2;
        card.Add(title);

        var subtitle = CairnUi.Label("Create a lobby, choose visibility, and pick how you want to start.", 11, CairnUi.TextMuted);
        subtitle.style.marginBottom = 14;
        card.Add(subtitle);

        var lobbyNameShell = BuildLobbyNameField();
        card.Add(lobbyNameShell);

        var configRow = CairnUi.Row();
        card.Add(configRow);

        var slotsCol = Column();
        slotsCol.style.flexBasis = 160;
        slotsCol.style.marginRight = 10;
        slotsCol.Add(CairnUi.MicroLabel("SLOTS"));
        _slotsStepper = new StepperControl(Mathf.Clamp(ModConfig.MaxPlayers.Value, 2, 8), 2, 8);
        slotsCol.Add(_slotsStepper.Root);
        configRow.Add(slotsCol);

        var visibilityCol = Column();
        visibilityCol.style.flexGrow = 1;
        visibilityCol.Add(CairnUi.MicroLabel("VISIBILITY"));
        _visibilitySegment = new SegmentControl(new[] { "Public", "Friends", "Private" }, 0);
        visibilityCol.Add(_visibilitySegment.Root);
        configRow.Add(visibilityCol);

        // Save note: at startup each player lands in the game's native save menu
        // and picks new/existing themselves, just like in single-player.
        var saveNote = CairnUi.Label("Everyone picks new or existing save in Cairn's menu when the host starts.",
            11, CairnUi.TextMuted, FontStyle.Italic);
        saveNote.style.marginTop = 8;
        saveNote.style.marginBottom = 12;
        card.Add(saveNote);

        _createButton = CairnUi.Button("CREATE LOBBY", OnCreateClicked, primary: true);
        _createButton.style.marginTop = 4;
        card.Add(_createButton);
    }

    private void BuildJoinCard()
    {
        var card = CairnUi.Card();
        card.style.flexGrow = 1;
        _homeScreen.Add(card);
        _joinPane = card;

        var title = CairnUi.Label("Join with code", 16, CairnUi.TextPrimary);
        title.style.marginBottom = 8;
        card.Add(title);

        var row = CairnUi.Row(0);
        row.style.marginBottom = 0;
        card.Add(row);

        _codeField = CairnUi.TextField("XXXX-XXXX", 9);
        _codeField.style.flexGrow = 1;
        _codeField.style.marginRight = 10;
        row.Add(_codeField);

        _joinButton = CairnUi.Button("JOIN", OnJoinClicked, primary: true);
        _joinButton.style.width = 150;
        _joinButton.style.height = 38;
        row.Add(_joinButton);
    }

    private void BuildBrowseCard()
    {
        var card = CairnUi.Card();
        card.style.flexGrow = 1;
        card.style.marginBottom = 0;
        _homeScreen.Add(card);
        _browsePane = card;

        var row = CairnUi.Row(0);
        row.style.alignItems = Align.Center;
        card.Add(row);

        var textCol = Column();
        textCol.style.flexGrow = 1;
        row.Add(textCol);
        textCol.Add(CairnUi.Label("Browse public lobbies", 16, CairnUi.TextPrimary));
        textCol.Add(CairnUi.Label("Find an open expedition hosted through Steam.", 11, CairnUi.TextMuted));

        _browseButton = CairnUi.Button("BROWSE", OnBrowseClicked);
        _browseButton.style.width = 150;
        row.Add(_browseButton);
    }

    private void BuildBrowserScreen()
    {
        _browserScreen = CreateScreen();
        _screenHost.Add(_browserScreen);

        var top = CairnUi.Row(12);
        top.style.height = 44;
        _browserScreen.Add(top);

        var backButton = CairnUi.Button("BACK", () =>
        {
            _currentScreen = Screen.Home;
            UpdateScreenVisibility();
        });
        backButton.style.width = 110;
        top.Add(backButton);

        var title = CairnUi.Label("Browse public lobbies", 20, CairnUi.TextPrimary);
        title.style.flexGrow = 1;
        title.style.unityTextAlign = TextAnchor.MiddleCenter;
        top.Add(title);

        _refreshButton = CairnUi.Button("REFRESH", OnRefreshClicked);
        _refreshButton.style.width = 120;
        top.Add(_refreshButton);

        _lobbyList = new ScrollView();
        _lobbyList.style.flexGrow = 1;
        _lobbyList.style.backgroundColor = CairnUi.Surface;
        _lobbyList.style.borderTopWidth = 1;
        _lobbyList.style.borderRightWidth = 1;
        _lobbyList.style.borderBottomWidth = 1;
        _lobbyList.style.borderLeftWidth = 1;
        CairnUi.SetBorderColor(_lobbyList, CairnUi.BorderSubtle);
        _lobbyList.style.paddingTop = 10;
        _lobbyList.style.paddingRight = 10;
        _lobbyList.style.paddingBottom = 10;
        _lobbyList.style.paddingLeft = 10;
        _browserScreen.Add(_lobbyList);

        _browserEmptyLabel = CairnUi.Label("No public lobbies right now.\nTry refreshing or host one yourself.",
            13, CairnUi.TextDim, FontStyle.Italic);
        _browserEmptyLabel.style.flexGrow = 1;
        _browserEmptyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        _lobbyList.Add(_browserEmptyLabel);
    }

    private void BuildConnectedScreen()
    {
        _connectedScreen = CreateScreen();
        _screenHost.Add(_connectedScreen);

        _connectedLobbyTitle = CairnUi.Label("Lobby", 24, CairnUi.TextPrimary);
        _connectedLobbyTitle.style.marginBottom = 18;
        _connectedScreen.Add(_connectedLobbyTitle);

        var codeCard = CairnUi.Card();
        codeCard.style.marginBottom = 18;
        _connectedScreen.Add(codeCard);

        codeCard.Add(CairnUi.MicroLabel("ROOM CODE - SHARE WITH YOUR FRIENDS"));
        var codeRow = CairnUi.Row(0);
        codeRow.style.alignItems = Align.Center;
        codeCard.Add(codeRow);

        _connectedCode = CairnUi.Label("----", 42, CairnUi.Accent, FontStyle.Bold);
        _connectedCode.style.flexGrow = 1;
        _connectedCode.style.unityTextAlign = TextAnchor.MiddleCenter;
        codeRow.Add(_connectedCode);

        _copyButton = CairnUi.Button("COPY", OnCopyClicked);
        _copyButton.style.width = 130;
        _copyButton.style.height = 48;
        codeRow.Add(_copyButton);

        var playersCard = CairnUi.Card();
        playersCard.style.flexGrow = 1;
        _connectedScreen.Add(playersCard);

        var playersHeader = CairnUi.Row(0);
        playersCard.Add(playersHeader);
        playersHeader.Add(CairnUi.Label("PLAYERS", 11, CairnUi.TextMuted));
        _connectedCount = CairnUi.Label("0", 11, CairnUi.TextMuted);
        _connectedCount.style.flexGrow = 1;
        _connectedCount.style.unityTextAlign = TextAnchor.MiddleRight;
        playersHeader.Add(_connectedCount);

        _connectedPlayers = CairnUi.Label("", 14, CairnUi.TextPrimary);
        _connectedPlayers.style.flexGrow = 1;
        _connectedPlayers.style.marginTop = 8;
        playersCard.Add(_connectedPlayers);

        var saveHint = CairnUi.Label("Everyone picks new or existing save in Cairn's menu when the host starts.",
            11, CairnUi.TextMuted, FontStyle.Italic);
        saveHint.style.marginTop = 12;
        saveHint.style.height = 18;
        _connectedScreen.Add(saveHint);

        _startButton = CairnUi.Button("START CLIMB", () => OnStartRequested?.Invoke(), primary: true);
        _startButton.style.marginTop = 16;
        _connectedScreen.Add(_startButton);

        _startHint = CairnUi.Label("Waiting for host to start", 12, CairnUi.TextMuted, FontStyle.Italic);
        _startHint.style.height = 40;
        _startHint.style.marginTop = 16;
        _startHint.style.unityTextAlign = TextAnchor.MiddleCenter;
        _connectedScreen.Add(_startHint);

        var leaveButton = CairnUi.Button("LEAVE LOBBY", () => OnDisconnectRequested?.Invoke());
        leaveButton.style.marginTop = 10;
        leaveButton.style.color = CairnUi.Error;
        _connectedScreen.Add(leaveButton);
    }

    private VisualElement CreateScreen()
    {
        var screen = new VisualElement();
        screen.style.flexGrow = 1;
        screen.style.display = DisplayStyle.None;
        return screen;
    }

    private static VisualElement Column()
    {
        var column = new VisualElement();
        column.style.flexDirection = FlexDirection.Column;
        return column;
    }

    private VisualElement BuildLobbyNameField()
    {
        var shell = new VisualElement();
        shell.style.height = 66;
        shell.style.marginBottom = 14;
        shell.style.paddingTop = 8;
        shell.style.paddingRight = 12;
        shell.style.paddingBottom = 8;
        shell.style.paddingLeft = 12;
        shell.style.backgroundColor = CairnUi.SurfaceSoft;
        shell.style.borderTopWidth = 1;
        shell.style.borderRightWidth = 1;
        shell.style.borderBottomWidth = 1;
        shell.style.borderLeftWidth = 1;
        shell.style.borderTopLeftRadius = 5;
        shell.style.borderTopRightRadius = 5;
        shell.style.borderBottomLeftRadius = 5;
        shell.style.borderBottomRightRadius = 5;
        CairnUi.SetBorderColor(shell, CairnUi.BorderSubtle);

        var labelRow = CairnUi.Row(0);
        labelRow.style.height = 18;
        labelRow.style.marginBottom = 2;
        shell.Add(labelRow);

        var label = CairnUi.MicroLabel("LOBBY NAME");
        label.style.flexGrow = 1;
        label.style.marginBottom = 0;
        labelRow.Add(label);

        var hint = CairnUi.Label("Editable", 10, CairnUi.TextDim, FontStyle.Italic);
        hint.style.unityTextAlign = TextAnchor.MiddleRight;
        hint.style.width = 86;
        labelRow.Add(hint);

        _lobbyNameField = CairnUi.TextField("Lobby name", 32);
        _lobbyNameField.style.height = 28;
        _lobbyNameField.style.backgroundColor = Color.clear;
        _lobbyNameField.style.borderTopWidth = 0;
        _lobbyNameField.style.borderRightWidth = 0;
        _lobbyNameField.style.borderBottomWidth = 0;
        _lobbyNameField.style.borderLeftWidth = 0;
        _lobbyNameField.style.paddingLeft = 0;
        _lobbyNameField.style.paddingRight = 0;
        _lobbyNameField.style.fontSize = 14;
        SetDefaultLobbyName(force: true);
        shell.Add(_lobbyNameField);

        return shell;
    }

    private void UpdateHomeMode()
    {
        if (_homeModeSegment == null) return;

        CairnUi.Show(_hostPane, _homeModeSegment.SelectedIndex == 0);
        CairnUi.Show(_joinPane, _homeModeSegment.SelectedIndex == 1);
        CairnUi.Show(_browsePane, _homeModeSegment.SelectedIndex == 2);
    }

    private void UpdateSteamProfile()
    {
        var name = GetLocalPlayerName();
        if (_steamNameLabel != null)
            _steamNameLabel.text = name;

        SetDefaultLobbyName(force: false);
    }

    private void SetDefaultLobbyName(bool force)
    {
        if (_lobbyNameField == null) return;

        var nextDefault = $"{GetLocalPlayerName()}'s climb";
        if (force || string.IsNullOrWhiteSpace(_lobbyNameField.value) ||
            string.Equals(_lobbyNameField.value, _lastDefaultLobbyName, StringComparison.Ordinal))
        {
            _lobbyNameField.SetValueWithoutNotify(nextDefault);
            _lastDefaultLobbyName = nextDefault;
        }
    }

    private static string GetLocalPlayerName()
    {
        var name = Mod.Instance?.Lobby?.LocalPersonaName;
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim();

        return string.IsNullOrWhiteSpace(ModConfig.PlayerName?.Value)
            ? "Player"
            : ModConfig.PlayerName.Value.Trim();
    }

    private void OnCreateClicked()
    {
        if (_isConnecting) return;

        var lobbyName = _lobbyNameField.value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(lobbyName))
            return;

        var visibility = _visibilitySegment.SelectedIndex switch
        {
            0 => LobbyVisibility.Public,
            1 => LobbyVisibility.FriendsOnly,
            _ => LobbyVisibility.Private,
        };

        OnHostRequested?.Invoke(new HostConfig
        {
            PlayerName = GetLocalPlayerName(),
            LobbyName = lobbyName,
            MaxPlayers = _slotsStepper.Value,
            Visibility = visibility,
        });
    }

    private void OnJoinClicked()
    {
        if (_isConnecting) return;

        var code = _codeField.value?.Trim().ToUpperInvariant() ?? "";
        if (string.IsNullOrWhiteSpace(code))
            return;

        OnJoinByCodeRequested?.Invoke(GetLocalPlayerName(), code);
    }

    private void OnBrowseClicked()
    {
        _currentScreen = Screen.Browser;
        UpdateScreenVisibility();
        SetRefreshing(true);
        OnBrowseRequested?.Invoke();
    }

    private void OnRefreshClicked()
    {
        SetRefreshing(true);
        OnBrowseRequested?.Invoke();
    }

    private void OnCopyClicked()
    {
        var code = Mod.Instance?.Lobby?.CurrentRoomCode;
        if (string.IsNullOrEmpty(code)) return;

        try
        {
            GUIUtility.systemCopyBuffer = code;
            _copyButton.text = "COPIED";
            _copyButton.style.color = CairnUi.Ok;
            _statusText = $"Copied {code}";
            UpdateStatus();
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnMP] Copy failed: {ex.Message}");
        }
    }

    private void RebuildLobbyList(IReadOnlyList<LobbyEntry> lobbies)
    {
        if (_lobbyList == null) return;
        _lobbyList.Clear();

        if (lobbies == null || lobbies.Count == 0)
        {
            _lobbyList.Add(_browserEmptyLabel);
            CairnUi.Show(_browserEmptyLabel, true);
            return;
        }

        foreach (var lobby in lobbies)
        {
            var row = CairnUi.Button("", () => OnJoinByLobbyIdRequested?.Invoke(lobby.LobbyId));
            row.style.height = 58;
            row.style.marginBottom = 8;
            row.style.backgroundColor = CairnUi.SurfaceSoft;
            row.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.text = $"{lobby.Name}\nHosted by {lobby.HostName} - {lobby.PlayerCount}/{lobby.MaxPlayers}";
            _lobbyList.Add(row);
        }
    }

    private void RefreshConnected(SteamLobbyManager lobby, string localPlayerName)
    {
        if (lobby == null) return;

        _connectedLobbyTitle.text = string.IsNullOrWhiteSpace(_lobbyName) ? "Lobby" : _lobbyName;
        _connectedCode.text = string.IsNullOrWhiteSpace(lobby.CurrentRoomCode) ? "----" : lobby.CurrentRoomCode;

        var sb = new StringBuilder();
        var count = 0;
        foreach (var member in lobby.Members)
        {
            if (count > 0) sb.Append('\n');
            var displayName = string.IsNullOrWhiteSpace(member.Name)
                ? (member.IsSelf ? (localPlayerName ?? "you") : "?")
                : member.Name;

            sb.Append(member.IsSelf ? "* " : "- ");
            sb.Append(displayName);

            if (member.IsSelf && member.IsHost) sb.Append("  (you, host)");
            else if (member.IsSelf) sb.Append("  (you)");
            else if (member.IsHost) sb.Append("  (host)");
            count++;
        }

        if (count == 0)
        {
            sb.Append("* ").Append(localPlayerName ?? "you");
            count = 1;
        }

        _connectedPlayers.text = sb.ToString();
        _connectedCount.text = lobby.MaxMembers > 0 ? $"{count} / {lobby.MaxMembers}" : count.ToString();

        var isHost = lobby.IsHost;
        CairnUi.Show(_startButton, isHost);
        CairnUi.Show(_startHint, !isHost);
        _copyButton.text = "COPY";
        _copyButton.style.color = CairnUi.TextPrimary;
    }

    private void UpdateInteractable()
    {
        var enabled = !_isConnecting && !_isConnected;
        _lobbyNameField?.SetEnabled(enabled);
        _codeField?.SetEnabled(enabled);
        _slotsStepper?.SetEnabled(enabled);
        _visibilitySegment?.SetEnabled(enabled);
        _joinButton?.SetEnabled(enabled);
        _browseButton?.SetEnabled(enabled);
        _createButton?.SetEnabled(enabled);

        if (_createButton != null)
            _createButton.text = _isConnecting ? "CONNECTING..." : "CREATE LOBBY";
        if (_joinButton != null)
            _joinButton.text = _isConnecting ? "CONNECTING..." : "JOIN";
    }

    private void UpdateStatus()
    {
        if (_statusLabel == null) return;

        if (_isConnecting)
        {
            _statusLabel.style.color = CairnUi.Warn;
            _statusLabel.text = string.IsNullOrWhiteSpace(_statusText) ? "Connecting..." : _statusText;
        }
        else if (_isConnected)
        {
            _statusLabel.style.color = CairnUi.Ok;
            _statusLabel.text = string.IsNullOrWhiteSpace(_statusText) ? "Connected" : _statusText;
        }
        else
        {
            var isError = !string.IsNullOrWhiteSpace(_statusText) &&
                          (_statusText.StartsWith("Failed") || _statusText.StartsWith("Rejected"));
            _statusLabel.style.color = isError ? CairnUi.Error : CairnUi.TextMuted;
            _statusLabel.text = string.IsNullOrWhiteSpace(_statusText) ? "Disconnected" : _statusText;
        }
    }

    private void UpdateScreenVisibility()
    {
        CairnUi.Show(_homeScreen, _currentScreen == Screen.Home);
        CairnUi.Show(_browserScreen, _currentScreen == Screen.Browser);
        CairnUi.Show(_connectedScreen, _currentScreen == Screen.Connected);
    }

    private void SetRefreshing(bool refreshing)
    {
        if (_refreshButton != null)
            _refreshButton.text = refreshing ? "LOADING" : "REFRESH";
    }

    private void SetVisible(bool visible)
    {
        if (_documentGo != null)
            _documentGo.SetActive(visible);
        if (_document != null)
            _document.enabled = visible;
        if (_root != null)
        {
            _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            _root.pickingMode = visible ? PickingMode.Position : PickingMode.Ignore;
        }
    }

    private void EnsureDocumentReady()
    {
        if (_document == null || _documentGo == null || _root == null)
        {
            BuildDocument();
            BuildLayout();
            UpdateScreenVisibility();
            return;
        }

        _document.enabled = true;

        var root = _document.rootVisualElement;
        if (root == null)
            return;

        if (!ReferenceEquals(_root, root))
            _root = root;

        if (_root.childCount > 0)
            return;

        CairnUi.FullScreen(_root);
        BuildLayout();
        UpdateScreenVisibility();
    }

    private sealed class StepperControl
    {
        private readonly int _min;
        private readonly int _max;
        private readonly Label _valueLabel;
        private readonly Button _minus;
        private readonly Button _plus;

        public VisualElement Root { get; }
        public int Value { get; private set; }

        public StepperControl(int value, int min, int max)
        {
            _min = min;
            _max = max;
            Value = Mathf.Clamp(value, min, max);

            Root = CairnUi.Row(0);
            Root.style.height = 38;
            Root.style.marginBottom = 0;

            _minus = CairnUi.Button("-", () => Change(-1));
            _minus.style.width = 38;
            Root.Add(_minus);

            _valueLabel = CairnUi.Label(Value.ToString(), 16, CairnUi.Accent, FontStyle.Bold);
            _valueLabel.style.flexGrow = 1;
            _valueLabel.style.backgroundColor = CairnUi.Surface;
            _valueLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            Root.Add(_valueLabel);

            _plus = CairnUi.Button("+", () => Change(1));
            _plus.style.width = 38;
            Root.Add(_plus);
        }

        public void SetEnabled(bool enabled)
        {
            _minus.SetEnabled(enabled);
            _plus.SetEnabled(enabled);
        }

        private void Change(int delta)
        {
            Value = Mathf.Clamp(Value + delta, _min, _max);
            _valueLabel.text = Value.ToString();
        }
    }

    private sealed class SegmentControl
    {
        private readonly List<Button> _buttons = new();
        private readonly string[] _labels;

        public VisualElement Root { get; }
        public int SelectedIndex { get; private set; }
        public event Action<int> Changed;

        public SegmentControl(string[] labels, int initialIndex)
        {
            _labels = labels;
            SelectedIndex = Mathf.Clamp(initialIndex, 0, labels.Length - 1);

            Root = CairnUi.Row(0);
            Root.style.height = 38;
            Root.style.marginBottom = 0;

            for (var i = 0; i < labels.Length; i++)
            {
                var captured = i;
                var button = CairnUi.Button(labels[i], () => Select(captured));
                button.style.flexGrow = 1;
                if (i > 0) button.style.marginLeft = 4;
                Root.Add(button);
                _buttons.Add(button);
            }

            Refresh();
        }

        public void SetEnabled(bool enabled)
        {
            foreach (var button in _buttons)
                button.SetEnabled(enabled);
        }

        public void SelectWithoutNotify(int index)
        {
            var next = Mathf.Clamp(index, 0, _labels.Length - 1);
            if (next == SelectedIndex) return;
            SelectedIndex = next;
            Refresh();
        }

        private void Select(int index)
        {
            if (index == SelectedIndex) return;
            SelectedIndex = index;
            Refresh();
            Changed?.Invoke(index);
        }

        private void Refresh()
        {
            for (var i = 0; i < _buttons.Count; i++)
            {
                var selected = i == SelectedIndex;
                _buttons[i].text = _labels[i];
                _buttons[i].style.backgroundColor = selected ? CairnUi.AccentSoft : CairnUi.Surface;
                _buttons[i].style.color = selected ? CairnUi.Accent : CairnUi.TextMuted;
                CairnUi.SetBorderColor(_buttons[i], selected ? CairnUi.Accent : CairnUi.BorderSubtle);
            }
        }
    }
}
