using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Networking;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.Internal.UI.Sketch;

/// <summary>
/// Multiplayer panel in the native "sketch" style (photo-mode sprites via GameUiAssetLibrary).
/// Implements IMultiplayerPanel: 4 screens (Host / Join / Browse / Connected) inside a shared frame
/// (icon tabs + title). Networking goes through the shared panel contract.
/// </summary>
internal sealed class SketchMultiplayerPanel : IMultiplayerPanel
{
    private readonly SteamLobbyManager _lobby;
    private enum Screen { Host = 0, Join = 1, Browse = 2, Connected = 3 }

    private const int MinSlots = 2, MaxSlots = 8, MaxBrowseRows = 5;
    private static readonly string[] VisLabels = { "Public", "Friends", "Private" };
    private static readonly LobbyVisibility[] VisValues =
        { LobbyVisibility.Public, LobbyVisibility.FriendsOnly, LobbyVisibility.Private };

    // ── State ────────────────────────────────────────────────────────────────────────
    private GameObject _canvasGo;
    private GameObject _contentGo;       // container whose children (title + body) are rebuilt per screen
    private Screen _current = Screen.Host;
    private bool _visible, _isConnected, _isConnecting;
    private string _statusText = "Disconnected";
    private string _lobbyName = "";
    private IReadOnlyList<LobbyEntry> _lobbies;
    private string _codeDraft = "";
    private int _browsePage;
    private bool _isBrowsing;
    private float _copyFeedbackSeconds, _memberRefreshSeconds;
    private readonly List<Button> _browseButtons = new();
    private readonly List<Button> _navigationButtons = new();
    private readonly List<Button> _settingButtons = new();
    private readonly List<GameObject> _memberRows = new();
    private readonly List<TextMeshProUGUI> _memberNames = new(), _memberRoles = new();

    // Dynamic refs (rebuilt on every screen change; reset to null otherwise).
    private TextMeshProUGUI _statusLabel;
    private TMP_InputField _codeInput;
    private string _autoLobbyName = "";
    private int _slots = MaxSlots, _visIndex;
    private TextMeshProUGUI _slotsLabel, _visLabel, _createLabel, _joinLabel, _refreshLabel;
    private Button _createBtn, _joinBtn, _refreshBtn, _previousPage, _nextPage, _copyBtn;
    private Transform _browseList;
    private GameObject _browseEmpty;
    private TextMeshProUGUI _connCodeLabel, _connCountLabel, _connCopyLabel, _titleLabel, _pageLabel, _browseSummary;
    private GameObject _connStart, _connStartHint;

    // ── IMultiplayerPanel events ─────────────────────────────────────────────────
    public event Action<HostConfig> OnHostRequested;
    public event Action<string> OnJoinByCodeRequested;
    public event Action OnBrowseRequested;
    public event Action<ulong> OnJoinByLobbyIdRequested;
    public event Action OnDisconnectRequested;
    public event Action OnStartRequested;
    public event Action OnPanelClosed;

    public bool IsVisible => _visible;

    internal SketchMultiplayerPanel(SteamLobbyManager lobby)
    {
        _lobby = lobby ?? throw new ArgumentNullException(nameof(lobby));
        _slots = Mathf.Clamp(ModConfig.MaxPlayers?.Value ?? MaxSlots, MinSlots, MaxSlots);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────────
    public void Show()
    {
        _visible = true;
        EnsureBuilt();
        if (_canvasGo != null) _canvasGo.SetActive(true);
    }

    public void Hide()
    {
        _visible = false;
        DestroyResources();
        OnPanelClosed?.Invoke();
    }

    public void Toggle() { if (_visible) Hide(); else Show(); }

    private void EnsureBuilt()
    {
        if (_canvasGo != null) return;
        GameUiAssetLibrary.EnsureHarvested(force: true);

        _canvasGo = new GameObject("CairnMP_SketchPanel");
        Object.DontDestroyOnLoad(_canvasGo);

        var canvas = _canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 210;                       // above the MainMenu (200)

        var scaler = _canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        // Keep the complete frame visible on ultrawide displays as well as 16:9.
        scaler.matchWidthOrHeight = 1f;
        _canvasGo.AddComponent<GraphicRaycaster>();

        // Dark scrim (blocks clicks behind it).
        var dim = SketchUiKit.Make("Dim", _canvasGo.transform);
        SketchUiKit.Stretch(dim);
        SketchUiKit.FillColor(dim, new Color(0f, 0f, 0f, 0.55f), raycast: true);

        // Centered panel + frame + content area (values: SketchLayout).
        var panel = SketchUiKit.Make("Panel", _canvasGo.transform);
        SketchUiKit.Box(panel, Vector2.zero, new Vector2(800f, 760f));

        var frame = SketchUiKit.Make("Frame", panel.transform);
        SketchUiKit.Rect(frame, Vector2.zero, Vector2.one,
            new Vector2(-SketchLayout.FrameL, -SketchLayout.FrameB),
            new Vector2(SketchLayout.FrameR, SketchLayout.FrameT));
        SketchUiKit.Sliced(frame, GameUiAssetLibrary.Frame, SketchUiKit.FrameTint);

        _contentGo = SketchUiKit.Make("Content", panel.transform);
        SketchUiKit.Rect(_contentGo, Vector2.zero, Vector2.one,
            new Vector2(SketchLayout.ContentL, SketchLayout.ContentB),
            new Vector2(-SketchLayout.ContentR, -SketchLayout.ContentT));

        // Close button (panel's top-right corner).
        var close = SketchUiKit.Make("Close", panel.transform);
        SketchUiKit.Box(close, new Vector2(350f, 326f), new Vector2(44f, 44f));
        SketchUiKit.Sliced(close, GameUiAssetLibrary.RowBg, SketchUiKit.RowTint, raycast: true);
        SketchUiKit.LabelBox(close.transform, "X", Vector2.zero, new Vector2(40f, 40f),
            "X", 22f, SketchUiKit.TextCream, TextAlignmentOptions.Center);
        SketchUiKit.MakeButton(close, (UnityAction)Hide);

        SwitchTo(_isConnected ? Screen.Connected : _current == Screen.Connected ? Screen.Host : _current);
    }

    // ── Navigation ───────────────────────────────────────────────────────────────────
    private void SwitchTo(Screen screen)
    {
        if (_isConnecting && _contentGo != null && _contentGo.transform.childCount > 0) return;
        if (_codeInput != null) _codeDraft = _codeInput.text ?? "";
        _current = screen;
        if (_contentGo == null) return;
        ClearScreenRefs();
        DestroyChildren(_contentGo.transform);

        BuildTitle(screen);
        var body = BuildBodyPanel();

        switch (screen)
        {
            case Screen.Host: BuildHost(body); break;
            case Screen.Join: BuildJoin(body); break;
            case Screen.Browse: BuildBrowse(body); break;
            case Screen.Connected: BuildConnected(body); break;
        }

        UpdateStatusLabel();
        if (screen == Screen.Browse) RequestBrowse();
    }

    private void BuildTitle(Screen screen)
    {
        var title = SketchUiKit.Make("TitlePanel", _contentGo.transform);
        SketchUiKit.StretchTop(title, 138f);
        SketchUiKit.Sliced(title, GameUiAssetLibrary.PanelTitle, SketchUiKit.PanelTint);

        if (screen == Screen.Connected)
        {
            // No tabs when connected: the title shows the lobby name.
            var name = string.IsNullOrEmpty(_lobby.CurrentLobbyName) ? _lobbyName : _lobby.CurrentLobbyName;
            _titleLabel = SketchUiKit.LabelBox(title.transform, "Title", new Vector2(0f, 4f), new Vector2(660f, 60f),
                string.IsNullOrEmpty(name) ? "LOBBY" : name, 32f, SketchUiKit.TextCream, TextAlignmentOptions.Center, logo: true);
            return;
        }

        SketchUiKit.LabelBox(title.transform, "Title", new Vector2(0f, 40f), new Vector2(620f, 42f),
            "MULTIPLAYER", 30f, SketchUiKit.TextCream, TextAlignmentOptions.Center, logo: true);
        BuildTab(title.transform, Screen.Host, GameUiAssetLibrary.IconMountain, "HOST", -220f);
        BuildTab(title.transform, Screen.Join, GameUiAssetLibrary.IconFriend, "JOIN", 0f);
        BuildTab(title.transform, Screen.Browse, GameUiAssetLibrary.IconPlayer, "BROWSE", 220f);
    }

    private void BuildTab(Transform parent, Screen screen, string icon, string label, float x)
    {
        bool active = _current == screen;
        var tab = SketchUiKit.Make("Tab_" + label, parent);
        SketchUiKit.Box(tab, new Vector2(x, -24f), new Vector2(198f, 60f));
        SketchUiKit.Sliced(tab, GameUiAssetLibrary.RowBg,
            active ? SketchUiKit.RowTint : new Color(0.04f, 0.06f, 0.12f, 0.12f), raycast: true);
        var iconGo = SketchUiKit.Make("Icon", tab.transform);
        SketchUiKit.Box(iconGo, new Vector2(-58f, 0f), new Vector2(34f, 34f));
        SketchUiKit.Simple(iconGo, icon, active ? SketchUiKit.IconActive : SketchUiKit.IconIdle);
        SketchUiKit.LabelBox(tab.transform, "Label", new Vector2(22f, 0f), new Vector2(112f, 34f),
            label, 20f, active ? SketchUiKit.TextCream : SketchUiKit.TextDim, TextAlignmentOptions.Center, logo: true);
        if (active)
        {
            var underline = SketchUiKit.Make("Selected", tab.transform);
            SketchUiKit.Box(underline, new Vector2(0f, -27f), new Vector2(154f, 2f));
            SketchUiKit.FillColor(underline, SketchUiKit.Accent);
        }
        _navigationButtons.Add(SketchUiKit.MakeButton(tab, (UnityAction)(() => SwitchTo(screen))));
    }

    private GameObject BuildBodyPanel()
    {
        var body = SketchUiKit.Make("BodyPanel", _contentGo.transform);
        SketchUiKit.StretchFill(body, topInset: 142f, bottomInset: 0f);
        SketchUiKit.Sliced(body, GameUiAssetLibrary.PanelBody, SketchUiKit.PanelTint);
        return body;
    }

    // ── Host screen ───────────────────────────────────────────────────────────────────
    private void BuildHost(GameObject body)
    {
        // Automatic lobby name (Steam name): read-only display, no input anymore.
        _autoLobbyName = $"{GetLocalPlayerName()}'s climb";
        var nameCell = SketchUiKit.RowStrip(body.transform, "Lobby name", 172f, 420f);
        SketchUiKit.Label(nameCell, "Value", _autoLobbyName, 16f, SketchUiKit.TextCream, TextAlignmentOptions.Right);

        var slotsCell = SketchUiKit.RowStrip(body.transform, "Slots", 96f);
        _slotsLabel = SketchUiKit.Stepper(slotsCell, _slots.ToString(),
            (UnityAction)(() => AdjustSlots(-1)), (UnityAction)(() => AdjustSlots(+1)));

        var visCell = SketchUiKit.RowStrip(body.transform, "Visibility", 20f);
        _visLabel = SketchUiKit.Stepper(visCell, VisLabels[_visIndex],
            (UnityAction)(() => CycleVisibility(-1)), (UnityAction)(() => CycleVisibility(+1)));

        foreach (var button in slotsCell.GetComponentsInChildren<Button>()) _settingButtons.Add(button);
        foreach (var button in visCell.GetComponentsInChildren<Button>()) _settingButtons.Add(button);

        _createBtn = PrimaryButton(body.transform, new Vector2(0f, -116f), new Vector2(420f, 70f),
            "CREATE LOBBY", out _createLabel, (UnityAction)OnCreateClicked);

        BuildStatus(body.transform);

        UpdateInteractable();
    }

    // ── Join screen ───────────────────────────────────────────────────────────────────
    private void BuildJoin(GameObject body)
    {
        var codeCell = SketchUiKit.RowStrip(body.transform, "Lobby code", 110f, 360f);
        _codeInput = SketchUiKit.NativeField(codeCell, "XXXX-XXXX", 16);
        _codeInput.text = _codeDraft;
        _codeInput.textComponent.fontSize = 24f;
        _codeInput.textComponent.alignment = TextAlignmentOptions.Center;
        _codeInput.onValueChanged.AddListener((UnityAction<string>)(_ => UpdateInteractable()));
        _codeInput.onSubmit.AddListener((UnityAction<string>)(_ => OnJoinClicked()));

        SketchUiKit.LabelBox(body.transform, "Hint", new Vector2(0f, 42f), new Vector2(620f, 28f),
            "Enter a friend's lobby code to join.", 15f, SketchUiKit.TextDim, TextAlignmentOptions.Center);

        _joinBtn = PrimaryButton(body.transform, new Vector2(0f, -86f), new Vector2(420f, 70f),
            "JOIN", out _joinLabel, (UnityAction)OnJoinClicked);

        BuildStatus(body.transform);

        UpdateInteractable();
    }

    // ── Browse screen ─────────────────────────────────────────────────────────────────
    private void BuildBrowse(GameObject body)
    {
        SketchUiKit.LabelBox(body.transform, "BrowseTitle", new Vector2(-170f, 226f), new Vector2(340f, 28f),
            "Public lobbies", 18f, SketchUiKit.TextCream, TextAlignmentOptions.Left);
        _refreshBtn = PrimaryButton(body.transform, new Vector2(255f, 214f), new Vector2(180f, 44f),
            _isBrowsing ? "SEARCHING..." : "REFRESH", out _refreshLabel, (UnityAction)RequestBrowse);
        _browseSummary = SketchUiKit.LabelBox(body.transform, "Results", new Vector2(-170f, 194f),
            new Vector2(340f, 24f), "", 14f, SketchUiKit.TextDim, TextAlignmentOptions.Left);

        _browseList = body.transform;   // rows are placed directly, at fixed positions
        _browseEmpty = SketchUiKit.Make("Empty", body.transform);
        SketchUiKit.Box(_browseEmpty, new Vector2(0f, 20f), new Vector2(640f, 80f));
        var emptyLabel = SketchUiKit.Label(_browseEmpty.transform, "T", "No public lobbies right now.\nRefresh or host one.",
            18f, SketchUiKit.TextDim, TextAlignmentOptions.Center);
        emptyLabel.enableWordWrapping = true;

        _previousPage = SketchUiKit.ArrowButton(body.transform, new Vector2(-110f, -196f), new Vector2(32f, 38f), true,
            (UnityAction)(() => ChangeBrowsePage(-1))).GetComponent<Button>();
        _nextPage = SketchUiKit.ArrowButton(body.transform, new Vector2(110f, -196f), new Vector2(32f, 38f), false,
            (UnityAction)(() => ChangeBrowsePage(1))).GetComponent<Button>();
        _pageLabel = SketchUiKit.LabelBox(body.transform, "Page", new Vector2(0f, -196f), new Vector2(150f, 28f),
            "", 16f, SketchUiKit.TextDim, TextAlignmentOptions.Center);
        BuildStatus(body.transform);

        RenderBrowseRows();
    }

    private void RenderBrowseRows()
    {
        if (_browseList == null) return;
        _browseButtons.Clear();
        // Clean up the old rows.
        var toKill = new List<GameObject>();
        for (int i = 0; i < _browseList.childCount; i++)
        {
            var ch = _browseList.GetChild(i);
            if (ch != null && ch.name.StartsWith("LobbyRow")) toKill.Add(ch.gameObject);
        }
        foreach (var go in toKill) { go.SetActive(false); Object.Destroy(go); }

        int count = _lobbies?.Count ?? 0;
        if (_browseEmpty != null) _browseEmpty.SetActive(count == 0 && !_isBrowsing);
        int pageCount = Math.Max(1, (count + MaxBrowseRows - 1) / MaxBrowseRows);
        _browsePage = Mathf.Clamp(_browsePage, 0, pageCount - 1);
        if (_pageLabel != null) _pageLabel.text = $"{_browsePage + 1} / {pageCount}";
        if (_browseSummary != null) _browseSummary.text = _isBrowsing ? "Searching for lobbies..." : $"{count} public lobbies";

        int first = _browsePage * MaxBrowseRows;
        int shown = Mathf.Min(count - first, MaxBrowseRows);
        for (int i = 0; i < shown; i++)
        {
            var entry = _lobbies[first + i];
            ulong id = entry.LobbyId;
            var row = SketchUiKit.Make("LobbyRow", _browseList);
            var rt = row.GetComponent<RectTransform>() ?? row.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(-64f, 62f);
            rt.anchoredPosition = new Vector2(0f, 136f - i * 68f);
            SketchUiKit.Sliced(row, GameUiAssetLibrary.RowBg, SketchUiKit.RowTint, raycast: true);

            SketchUiKit.LabelBox(row.transform, "Name", new Vector2(-80f, 12f), new Vector2(536f, 26f),
                entry.Name, 19f, SketchUiKit.TextCream, TextAlignmentOptions.Left);
            SketchUiKit.LabelBox(row.transform, "Host", new Vector2(-80f, -14f), new Vector2(536f, 20f),
                $"Hosted by {entry.HostName}", 12f, SketchUiKit.TextDim, TextAlignmentOptions.Left);
            bool full = entry.MaxPlayers > 0 && entry.PlayerCount >= entry.MaxPlayers;
            SketchUiKit.LabelBox(row.transform, "Count", new Vector2(280f, 0f), new Vector2(120f, 28f),
                full ? "FULL" : $"{entry.PlayerCount} / {entry.MaxPlayers}", 16f,
                full ? SketchUiKit.TextDim : SketchUiKit.Accent, TextAlignmentOptions.Right);
            var button = SketchUiKit.MakeButton(row, (UnityAction)(() =>
            {
                if (!_isConnecting && !_isConnected && !_isBrowsing && !full) OnJoinByLobbyIdRequested?.Invoke(id);
            }));
            button.interactable = !full && !_isConnecting && !_isConnected && !_isBrowsing;
            // Full lobbies stay disabled when connection state changes.
            if (!full) _browseButtons.Add(button);
        }
        UpdateInteractable();
    }

    private void RequestBrowse()
    {
        if (_isBrowsing || _isConnecting || _isConnected) return;
        _isBrowsing = true;
        if (_refreshLabel != null) _refreshLabel.text = "SEARCHING...";
        RenderBrowseRows();
        OnBrowseRequested?.Invoke();
    }

    private void ChangeBrowsePage(int direction)
    {
        if (_isConnecting || _isBrowsing) return;
        _browsePage += direction;
        RenderBrowseRows();
    }

    // ── Connected screen ───────────────────────────────────────────────────────────────
    private void BuildConnected(GameObject body)
    {
        SketchUiKit.LabelBox(body.transform, "CodeLabel", new Vector2(-250f, 214f), new Vector2(180f, 30f),
            "Lobby code", 18f, SketchUiKit.TextDim, TextAlignmentOptions.Left);
        _connCodeLabel = SketchUiKit.LabelBox(body.transform, "Code", new Vector2(0f, 214f), new Vector2(340f, 60f),
            "----", 40f, SketchUiKit.Accent, TextAlignmentOptions.Center, logo: true);
        _copyBtn = PrimaryButton(body.transform, new Vector2(278f, 214f), new Vector2(136f, 48f),
            "COPY", out _connCopyLabel, (UnityAction)OnCopyClicked);

        SketchUiKit.LabelBox(body.transform, "PlayersHdr", new Vector2(-240f, 156f), new Vector2(200f, 24f),
            "PLAYERS", 15f, SketchUiKit.TextDim, TextAlignmentOptions.Left);
        _connCountLabel = SketchUiKit.LabelBox(body.transform, "Count", new Vector2(280f, 156f), new Vector2(120f, 24f),
            "", 15f, SketchUiKit.TextDim, TextAlignmentOptions.Right);

        for (int i = 0; i < MaxSlots; i++)
        {
            var row = SketchUiKit.Make("PlayerRow", body.transform);
            SketchUiKit.Box(row, new Vector2(0f, 116f - i * 32f), new Vector2(696f, 30f));
            SketchUiKit.Sliced(row, GameUiAssetLibrary.RowBg, SketchUiKit.RowTint);
            _memberRows.Add(row);
            _memberNames.Add(SketchUiKit.LabelBox(row.transform, "Name", new Vector2(-90f, 0f), new Vector2(484f, 28f),
                "", 17f, SketchUiKit.TextCream, TextAlignmentOptions.Left));
            _memberRoles.Add(SketchUiKit.LabelBox(row.transform, "Role", new Vector2(248f, 0f), new Vector2(166f, 28f),
                "", 13f, SketchUiKit.Accent, TextAlignmentOptions.Right));
        }
        _statusLabel = SketchUiKit.LabelBox(body.transform, "Status", new Vector2(0f, -141f), new Vector2(680f, 28f),
            "", 14f, SketchUiKit.TextDim, TextAlignmentOptions.Center);

        _connStart = PrimaryButton(body.transform, new Vector2(0f, -187f), new Vector2(420f, 60f),
            "START CLIMB", out _, (UnityAction)(() => OnStartRequested?.Invoke())).gameObject;
        _connStartHint = SketchUiKit.LabelBox(body.transform, "StartHint", new Vector2(0f, -187f), new Vector2(600f, 30f),
            "Waiting for host to start", 15f, SketchUiKit.TextDim, TextAlignmentOptions.Center).gameObject;

        var leave = SketchUiKit.Make("LeaveLobby", body.transform);
        SketchUiKit.Box(leave, new Vector2(0f, -240f), new Vector2(260f, 38f));
        SketchUiKit.Sliced(leave, GameUiAssetLibrary.RowBg, SketchUiKit.RowTint, raycast: true);
        SketchUiKit.Label(leave.transform, "Label", "LEAVE LOBBY", 16f, SketchUiKit.TextDim, TextAlignmentOptions.Center);
        SketchUiKit.MakeButton(leave, (UnityAction)(() => OnDisconnectRequested?.Invoke()));

        RefreshConnected();
    }

    private void RefreshConnected()
    {
        var lobby = _lobby;
        string code = lobby?.CurrentRoomCode;
        if (_connCodeLabel != null) _connCodeLabel.text = string.IsNullOrEmpty(code) ? "----" : code;
        if (_copyBtn != null) _copyBtn.interactable = !string.IsNullOrEmpty(code);
        if (_titleLabel != null)
        {
            string name = string.IsNullOrEmpty(lobby.CurrentLobbyName) ? _lobbyName : lobby.CurrentLobbyName;
            _titleLabel.text = string.IsNullOrEmpty(name) ? "LOBBY" : name;
        }

        bool isHost = lobby?.IsHost ?? false;
        if (_connStart != null) _connStart.SetActive(isHost);
        if (_connStartHint != null) _connStartHint.SetActive(!isHost);

        var members = lobby?.Members;
        int count = members?.Count ?? 0;
        if (_connCountLabel != null)
        {
            int max = lobby?.MaxMembers ?? 0;
            _connCountLabel.text = max > 0 ? $"{count} / {max}" : count.ToString();
        }
        for (int i = 0; i < _memberRows.Count; i++)
        {
            var member = i < count ? members[i] : null;
            _memberRows[i].SetActive(member != null);
            if (member == null) continue;
            _memberNames[i].text = string.IsNullOrEmpty(member.Name) ? "Player" : member.Name;
            _memberRoles[i].text = member.IsSelf && member.IsHost ? "YOU / HOST" : member.IsHost ? "HOST" : member.IsSelf ? "YOU" : "";
        }
    }

    // ── Actions ─────────────────────────────────────────────────────────────────────
    private void OnCreateClicked()
    {
        if (_isConnecting || _isConnected) return;
        var cfg = new HostConfig
        {
            PlayerName = GetLocalPlayerName(),
            LobbyName = !string.IsNullOrEmpty(_autoLobbyName) ? _autoLobbyName : $"{GetLocalPlayerName()}'s climb",
            MaxPlayers = _slots,
            Visibility = VisValues[_visIndex],
        };
        OnHostRequested?.Invoke(cfg);
    }

    private void OnJoinClicked()
    {
        if (_isConnecting || _isConnected) return;
        string code = _codeInput != null ? _codeInput.text?.Trim().ToUpperInvariant() : "";
        if (string.IsNullOrEmpty(code)) return;
        OnJoinByCodeRequested?.Invoke(code);
    }

    private void OnCopyClicked()
    {
        var code = _lobby.CurrentRoomCode;
        if (string.IsNullOrEmpty(code)) return;
        GUIUtility.systemCopyBuffer = code;
        if (_connCopyLabel != null) _connCopyLabel.text = "COPIED";
        _copyFeedbackSeconds = 2f;
    }

    private void AdjustSlots(int d)
    {
        if (_isConnecting || _isConnected) return;
        _slots = Mathf.Clamp(_slots + d, MinSlots, MaxSlots);
        if (_slotsLabel != null) _slotsLabel.text = _slots.ToString();
    }

    private void CycleVisibility(int d)
    {
        if (_isConnecting || _isConnected) return;
        _visIndex = ((_visIndex + d) % VisLabels.Length + VisLabels.Length) % VisLabels.Length;
        if (_visLabel != null) _visLabel.text = VisLabels[_visIndex];
    }

    // ── IMultiplayerPanel: state pushed by Mod.cs ────────────────────────────────────
    public void SetStatus(string status, bool connected)
    {
        _statusText = status ?? "";
        _isConnected = connected;
        _isConnecting = false;

        if (connected && _current != Screen.Connected) SwitchTo(Screen.Connected);
        else if (!connected && _current == Screen.Connected) SwitchTo(Screen.Host);

        UpdateStatusLabel();
        UpdateInteractable();
    }

    public void SetConnecting(string status)
    {
        _statusText = status ?? "";
        _isConnecting = true;
        UpdateStatusLabel();
        UpdateInteractable();
    }

    public void SetBrowserLobbies(IReadOnlyList<LobbyEntry> lobbies)
    {
        _lobbies = lobbies;
        _isBrowsing = false;
        if (_refreshLabel != null) _refreshLabel.text = "REFRESH";
        if (_current == Screen.Browse) RenderBrowseRows();
    }

    public void SetCurrentLobbyName(string name)
    {
        _lobbyName = name ?? "";
        if (_titleLabel != null) _titleLabel.text = string.IsNullOrEmpty(_lobbyName) ? "LOBBY" : _lobbyName;
    }

    public void Tick(float dt)
    {
        if (!_visible) return;
        if (_copyFeedbackSeconds > 0f)
        {
            _copyFeedbackSeconds -= dt;
            if (_copyFeedbackSeconds <= 0f && _connCopyLabel != null) _connCopyLabel.text = "COPY";
        }
        _memberRefreshSeconds -= dt;
        if (_current == Screen.Connected && _memberRefreshSeconds <= 0f)
        {
            _memberRefreshSeconds = 0.25f;
            RefreshConnected();
        }
    }

    public void OnGUI() { }   // input handled by TMP_InputField

    public void DestroyResources()
    {
        _visible = false;
        if (_codeInput != null) _codeDraft = _codeInput.text ?? "";
        if (_canvasGo != null) { _canvasGo.SetActive(false); Object.Destroy(_canvasGo); }
        _canvasGo = null;
        _contentGo = null;
        ClearScreenRefs();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────
    private void BuildStatus(Transform parent)
    {
        _statusLabel = SketchUiKit.LabelBox(parent, "Status", new Vector2(0f, -240f),
            new Vector2(680f, 44f), _statusText, 15f, SketchUiKit.TextDim, TextAlignmentOptions.Center);
        _statusLabel.enableWordWrapping = true;
    }

    private void UpdateStatusLabel()
    {
        if (_statusLabel == null) return;
        Color c = _isConnected ? new Color(0.38f, 0.76f, 0.45f, 1f)
            : _isConnecting ? new Color(0.86f, 0.73f, 0.29f, 1f)
            : (_statusText.StartsWith("Failed") || _statusText.StartsWith("Rejected"))
                ? new Color(0.82f, 0.38f, 0.33f, 1f) : SketchUiKit.TextDim;
        _statusLabel.color = c;
        _statusLabel.text = string.IsNullOrEmpty(_statusText) ? "Disconnected" : _statusText;
    }

    private void UpdateInteractable()
    {
        bool enabled = !_isConnecting && !_isConnected;
        if (_codeInput != null) _codeInput.interactable = enabled;
        if (_createBtn != null) _createBtn.interactable = enabled;
        if (_joinBtn != null) _joinBtn.interactable = enabled && !string.IsNullOrWhiteSpace(_codeInput?.text);
        foreach (var button in _navigationButtons) button.interactable = enabled;
        foreach (var button in _settingButtons) button.interactable = enabled;
        foreach (var button in _browseButtons) button.interactable = enabled && !_isBrowsing;
        if (_refreshBtn != null) _refreshBtn.interactable = enabled && !_isBrowsing;
        int pageCount = Math.Max(1, ((_lobbies?.Count ?? 0) + MaxBrowseRows - 1) / MaxBrowseRows);
        if (_previousPage != null) _previousPage.interactable = enabled && !_isBrowsing && _browsePage > 0;
        if (_nextPage != null) _nextPage.interactable = enabled && !_isBrowsing && _browsePage < pageCount - 1;
        if (_createLabel != null) _createLabel.text = _isConnecting ? "CONNECTING..." : "CREATE LOBBY";
        if (_joinLabel != null) _joinLabel.text = _isConnecting ? "CONNECTING..." : "JOIN";
    }

    private static Button PrimaryButton(Transform parent, Vector2 pos, Vector2 size, string text,
        out TextMeshProUGUI label, UnityAction onClick)
    {
        var go = SketchUiKit.Make("Button", parent);
        SketchUiKit.Box(go, pos, size);
        SketchUiKit.Sliced(go, GameUiAssetLibrary.Button, Color.white, raycast: true);
        label = SketchUiKit.LabelBox(go.transform, "Label", Vector2.zero, size,
            text, 20f, SketchUiKit.ButtonText, TextAlignmentOptions.Center, logo: true);
        return SketchUiKit.MakeButton(go, onClick);
    }

    private void ClearScreenRefs()
    {
        _statusLabel = null; _codeInput = null;
        _slotsLabel = null; _visLabel = null; _createLabel = null; _joinLabel = null; _refreshLabel = null;
        _createBtn = null; _joinBtn = null; _browseList = null; _browseEmpty = null;
        _connCodeLabel = null; _connCountLabel = null; _connCopyLabel = null;
        _titleLabel = null; _pageLabel = null; _browseSummary = null;
        _refreshBtn = null; _previousPage = null; _nextPage = null; _copyBtn = null;
        _browseButtons.Clear(); _navigationButtons.Clear(); _settingButtons.Clear();
        _memberRows.Clear(); _memberNames.Clear(); _memberRoles.Clear();
        _copyFeedbackSeconds = 0f;
        _connStart = null; _connStartHint = null;
    }

    private static void DestroyChildren(Transform parent)
    {
        if (parent == null) return;
        var kids = new List<GameObject>();
        for (int i = 0; i < parent.childCount; i++) kids.Add(parent.GetChild(i).gameObject);
        foreach (var go in kids) { go.SetActive(false); Object.Destroy(go); }
    }

    private string GetLocalPlayerName()
    {
        var n = _lobby.LocalPersonaName;
        if (string.IsNullOrEmpty(n)) n = ModConfig.PlayerName?.Value;
        return string.IsNullOrEmpty(n) ? "Player" : n;
    }
}
