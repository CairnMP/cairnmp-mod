using System;
using System.Text;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using CairnMultiplayerMod.Core;
using CairnMultiplayerMod.Networking;
using CairnMultiplayerMod.UI.Inputs;

namespace CairnMultiplayerMod.UI.Screens;

/// <summary>
/// View shown when the player is connected to a lobby. "Refined alpine" direction:
/// the room code is the hero (monumental type + gold accent), the player list is
/// laid out in TMP rich-text, and the leave button is deliberately discreet at the bottom.
/// The Steam Friends invite was removed — the Steam overlay isn't injectable
/// when Cairn isn't launched through Steam, so sharing happens via the room code.
/// </summary>
internal sealed class ConnectedScreen
{
    private readonly TMP_FontAsset _font;

    private TextMeshProUGUI _lobbyTitleTmp;
    private TextMeshProUGUI _codeTmp;
    private TextMeshProUGUI _playersListTmp;
    private TextMeshProUGUI _playersCountTmp;
    private TextMeshProUGUI _copyBtnLabel;
    private TextMeshProUGUI _emptyPlayersHint;
    private GameObject _startBtnRoot;
    private Button _startBtn;
    private TextMeshProUGUI _startBtnLabel;
    private TextMeshProUGUI _startHintTmp;

    public GameObject Root { get; }

    public event Action DisconnectRequested;
    public event Action CopyCodeRequested;
    public event Action StartRequested;

    public ConnectedScreen(Transform parent, TMP_FontAsset font)
    {
        _font = font;
        Root  = MultiplayerPanelTheme.MakeGo("ConnectedScreen", parent);
        MultiplayerPanelTheme.FullStretch(Root);

        // ── Lobby title + accent rule ──────────────────────────────────────────
        _lobbyTitleTmp = MultiplayerPanelTheme.Tmp(Root.transform, "LobbyTitle", "Lobby",
            _font, 22, MultiplayerPanelTheme.TextPrimary, TextAlignmentOptions.TopLeft, FontStyles.Normal);
        MultiplayerPanelTheme.Anchor(_lobbyTitleTmp.gameObject, new Vector2(0f, 0.93f), new Vector2(1f, 1.00f));

        var titleRule = MultiplayerPanelTheme.MakeGo("TitleRule", Root.transform);
        MultiplayerPanelTheme.Anchor(titleRule, new Vector2(0f, 0.915f), new Vector2(0.07f, 0.92f));
        MultiplayerPanelTheme.Fill(titleRule, MultiplayerPanelTheme.Accent);

        // ── ROOM CODE micro-label ──────────────────────────────────────────────
        var codeLbl = MultiplayerPanelTheme.Tmp(Root.transform, "CodeLbl", "ROOM CODE · share with your friends",
            _font, 10, MultiplayerPanelTheme.TextDim, TextAlignmentOptions.MidlineLeft, FontStyles.Normal);
        codeLbl.characterSpacing = 4f;
        MultiplayerPanelTheme.Anchor(codeLbl.gameObject, new Vector2(0f, 0.855f), new Vector2(1f, 0.890f));

        // ── Hero code box ──────────────────────────────────────────────────────
        var codeBox = MultiplayerPanelTheme.MakeGo("CodeBox", Root.transform);
        MultiplayerPanelTheme.Anchor(codeBox, new Vector2(0f, 0.70f), new Vector2(1f, 0.85f));
        MultiplayerPanelTheme.Fill(codeBox, MultiplayerPanelTheme.Border);

        var codeInner = MultiplayerPanelTheme.MakeGo("Inner", codeBox.transform);
        MultiplayerPanelTheme.Anchor(codeInner, Vector2.zero, Vector2.one, new Vector2(1f, 1f), new Vector2(-1f, -1f));
        MultiplayerPanelTheme.Fill(codeInner, MultiplayerPanelTheme.SubBg);

        // Accent strip on the left to emphasize this is the hero element
        var codeAccentBar = MultiplayerPanelTheme.MakeGo("AccentBar", codeInner.transform);
        MultiplayerPanelTheme.Anchor(codeAccentBar, new Vector2(0f, 0.18f), new Vector2(0f, 0.82f),
            new Vector2(0f, 0f), new Vector2(2f, 0f));
        MultiplayerPanelTheme.Fill(codeAccentBar, MultiplayerPanelTheme.Accent);

        _codeTmp = MultiplayerPanelTheme.Tmp(codeInner.transform, "Code", "----",
            _font, 40, MultiplayerPanelTheme.Accent, TextAlignmentOptions.Center, FontStyles.Bold);
        _codeTmp.characterSpacing = 12f;
        MultiplayerPanelTheme.Anchor(_codeTmp.gameObject, new Vector2(0.04f, 0f), new Vector2(0.78f, 1f));

        // Copy button (plain text on the right, raycast active)
        _copyBtnLabel = BuildIconButton(codeInner.transform, "CopyBtn", "Copy",
            new Vector2(0.78f, 0f), new Vector2(0.985f, 1f),
            (UnityAction)(() => CopyCodeRequested?.Invoke()));

        // ── PLAYERS: header + rule + count ────────────────────────────────────
        var playersLbl = MultiplayerPanelTheme.Tmp(Root.transform, "PlayersLbl", "PLAYERS",
            _font, 10, MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.MidlineLeft, FontStyles.Normal);
        playersLbl.characterSpacing = 6f;
        MultiplayerPanelTheme.Anchor(playersLbl.gameObject, new Vector2(0f, 0.62f), new Vector2(0.25f, 0.66f));

        var headerRule = MultiplayerPanelTheme.MakeGo("PlayersRule", Root.transform);
        MultiplayerPanelTheme.Anchor(headerRule, new Vector2(0.26f, 0.638f), new Vector2(0.85f, 0.642f));
        MultiplayerPanelTheme.Fill(headerRule, MultiplayerPanelTheme.BorderSubtle);

        _playersCountTmp = MultiplayerPanelTheme.Tmp(Root.transform, "PlayersCount", "0 / 0",
            _font, 10, MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.MidlineRight, FontStyles.Normal);
        _playersCountTmp.characterSpacing = 2f;
        MultiplayerPanelTheme.Anchor(_playersCountTmp.gameObject, new Vector2(0.86f, 0.62f), new Vector2(1f, 0.66f));

        // ── Players list (TMP rich-text per line) ─────────────────────────────
        var listBg = MultiplayerPanelTheme.MakeGo("ListBg", Root.transform);
        MultiplayerPanelTheme.Anchor(listBg, new Vector2(0f, 0.31f), new Vector2(1f, 0.61f));
        MultiplayerPanelTheme.Fill(listBg, MultiplayerPanelTheme.SubBg);

        _playersListTmp = MultiplayerPanelTheme.Tmp(listBg.transform, "Players", "",
            _font, 13, MultiplayerPanelTheme.TextPrimary, TextAlignmentOptions.TopLeft, FontStyles.Normal);
        _playersListTmp.richText    = true;
        _playersListTmp.lineSpacing = 6f;
        MultiplayerPanelTheme.Anchor(_playersListTmp.gameObject, Vector2.zero, Vector2.one,
            new Vector2(18f, 12f), new Vector2(-18f, -12f));

        _emptyPlayersHint = MultiplayerPanelTheme.Tmp(listBg.transform, "EmptyHint",
            "Waiting for players to join…", _font, 11,
            MultiplayerPanelTheme.TextDim, TextAlignmentOptions.Center, FontStyles.Italic);
        MultiplayerPanelTheme.Anchor(_emptyPlayersHint.gameObject, new Vector2(0f, 0f), new Vector2(1f, 0.25f));
        _emptyPlayersHint.gameObject.SetActive(false);

        BuildStartArea();

        // ── Leave: plain text, discreet ───────────────────────────────────────
        BuildLeaveButton();
    }

    /// <summary>Updates code, title and player list from the SteamLobbyManager.</summary>
    public void Refresh(string lobbyName, SteamLobbyManager lobby, string localPlayerName)
    {
        if (lobby == null) return;

        _lobbyTitleTmp.text = string.IsNullOrEmpty(lobbyName) ? "Lobby" : lobbyName;
        _codeTmp.text       = string.IsNullOrEmpty(lobby.CurrentRoomCode) ? "----" : lobby.CurrentRoomCode;

        var sb = new StringBuilder();
        int count = 0;
        var bullet    = ColorHex(MultiplayerPanelTheme.Accent);
        var bulletDim = ColorHex(MultiplayerPanelTheme.TextDim);
        var tagColor  = ColorHex(MultiplayerPanelTheme.TextMuted);

        foreach (var m in lobby.Members)
        {
            if (count > 0) sb.Append('\n');

            var dotColor = m.IsSelf ? bullet : bulletDim;
            sb.Append($"<color=#{dotColor}>●</color>  ");

            var displayName = string.IsNullOrEmpty(m.Name)
                ? (m.IsSelf ? (localPlayerName ?? "you") : "?")
                : m.Name;
            sb.Append(displayName);

            string tag = null;
            if (m.IsSelf && m.IsHost)      tag = "you · host";
            else if (m.IsSelf)             tag = "you";
            else if (m.IsHost)             tag = "host";

            if (tag != null)
                sb.Append($"   <color=#{tagColor}>{tag}</color>");

            count++;
        }

        if (count == 0)
        {
            // Fallback: no members reported yet (window during connect).
            sb.Append($"<color=#{bullet}>●</color>  ").Append(localPlayerName ?? "you");
            count = 1;
        }

        _playersListTmp.text = sb.ToString();
        var max = lobby.MaxMembers;
        _playersCountTmp.text = max > 0 ? $"{count} / {max}" : $"{count}";

        if (_emptyPlayersHint != null)
            _emptyPlayersHint.gameObject.SetActive(count <= 1);

        bool isHost = lobby.IsHost;
        if (_startBtnRoot != null) _startBtnRoot.SetActive(isHost);
        if (_startBtn != null) _startBtn.interactable = isHost;
        if (_startBtnLabel != null) _startBtnLabel.text = "START CLIMB";
        if (_startHintTmp != null)
        {
            _startHintTmp.gameObject.SetActive(!isHost);
            _startHintTmp.text = "Waiting for host to start";
        }
    }

    public void SetCopyFeedback(bool copied)
    {
        _copyBtnLabel.text  = copied ? "Copied!" : "Copy";
        _copyBtnLabel.color = copied ? MultiplayerPanelTheme.StatusOk : MultiplayerPanelTheme.TextMuted;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private TextMeshProUGUI BuildIconButton(Transform parent, string name, string label,
        Vector2 aMin, Vector2 aMax, UnityAction onClick)
    {
        var go = MultiplayerPanelTheme.MakeGo(name, parent);
        MultiplayerPanelTheme.Anchor(go, aMin, aMax);
        var img = go.AddComponent<Image>();
        img.color         = new Color(0, 0, 0, 0);
        img.raycastTarget = true;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);

        var lbl = MultiplayerPanelTheme.Tmp(go.transform, "Lbl", label, _font, 12,
            MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.Center, FontStyles.Normal);
        return lbl;
    }

    /// <summary>
    /// Launch area: primary button visible to the host, passive message
    /// for clients waiting for the Steam lobby signal.
    /// </summary>
    private void BuildStartArea()
    {
        _startBtnRoot = MultiplayerPanelTheme.MakeGo("StartBtn", Root.transform);
        MultiplayerPanelTheme.Anchor(_startBtnRoot, new Vector2(0.18f, 0.075f), new Vector2(0.82f, 0.145f));

        var bg = _startBtnRoot.AddComponent<Image>();
        bg.color = MultiplayerPanelTheme.AccentSolid;
        bg.raycastTarget = true;

        _startBtn = _startBtnRoot.AddComponent<Button>();
        _startBtn.targetGraphic = bg;
        _startBtn.transition = Selectable.Transition.ColorTint;
        var colors = _startBtn.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1f, 1f, 1f, 0.92f);
        colors.pressedColor = new Color(1f, 1f, 1f, 0.78f);
        colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 1f);
        _startBtn.colors = colors;
        _startBtn.onClick.AddListener((UnityAction)(() => StartRequested?.Invoke()));

        _startBtnLabel = MultiplayerPanelTheme.Tmp(_startBtnRoot.transform, "Lbl", "START CLIMB", _font, 12,
            new Color(0.10f, 0.08f, 0.05f, 1f), TextAlignmentOptions.Center, FontStyles.Bold);
        _startBtnLabel.characterSpacing = 4f;

        _startHintTmp = MultiplayerPanelTheme.Tmp(Root.transform, "StartHint", "Waiting for host to start", _font, 11,
            MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.Center, FontStyles.Italic);
        MultiplayerPanelTheme.Anchor(_startHintTmp.gameObject, new Vector2(0.12f, 0.075f), new Vector2(0.88f, 0.145f));
        _startHintTmp.gameObject.SetActive(false);
    }

    /// <summary>
    /// Deliberately discreet leave button — thin danger border, red caps text,
    /// transparent background. No visual weight so it doesn't dominate the panel.
    /// </summary>
    private void BuildLeaveButton()
    {
        var go = MultiplayerPanelTheme.MakeGo("LeaveBtn", Root.transform);
        MultiplayerPanelTheme.Anchor(go, new Vector2(0.18f, 0.005f), new Vector2(0.82f, 0.06f));

        var bg = go.AddComponent<Image>();
        bg.color         = new Color(0f, 0f, 0f, 0f);
        bg.raycastTarget = true;

        var border = MultiplayerPanelTheme.MakeGo("Border", go.transform);
        MultiplayerPanelTheme.Anchor(border, Vector2.zero, Vector2.one);
        MultiplayerPanelTheme.DrawBorder(border.transform,
            new Color(MultiplayerPanelTheme.DangerText.r,
                      MultiplayerPanelTheme.DangerText.g,
                      MultiplayerPanelTheme.DangerText.b, 0.30f));

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = bg;
        btn.transition    = Selectable.Transition.ColorTint;
        var c = btn.colors;
        c.normalColor      = new Color(1f, 1f, 1f, 1f);
        c.highlightedColor = new Color(1f, 0.92f, 0.92f, 1f);
        c.pressedColor     = new Color(0.85f, 0.75f, 0.75f, 1f);
        c.fadeDuration     = 0.12f;
        btn.colors = c;
        btn.onClick.AddListener((UnityAction)(() => DisconnectRequested?.Invoke()));

        var lbl = MultiplayerPanelTheme.Tmp(go.transform, "Lbl", "leave lobby", _font, 11,
            MultiplayerPanelTheme.DangerText, TextAlignmentOptions.Center, FontStyles.Normal);
        lbl.characterSpacing = 6f;
    }

    private static string ColorHex(Color c)
    {
        var r = (int)(Mathf.Clamp01(c.r) * 255f);
        var g = (int)(Mathf.Clamp01(c.g) * 255f);
        var b = (int)(Mathf.Clamp01(c.b) * 255f);
        var a = (int)(Mathf.Clamp01(c.a) * 255f);
        return $"{r:X2}{g:X2}{b:X2}{a:X2}";
    }
}
