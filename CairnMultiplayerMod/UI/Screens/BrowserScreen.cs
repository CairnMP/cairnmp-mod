using System;
using System.Collections.Generic;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CairnMultiplayerMod.UI.Screens;

/// <summary>
/// List of public lobbies. Until the Steam Relay layer is wired up, the list
/// stays empty; we show a dimmed "no public lobbies" state.
/// </summary>
internal sealed class BrowserScreen
{
    private readonly TMP_FontAsset _font;
    private readonly Transform _listContainer;
    private readonly TextMeshProUGUI _emptyState;
    private readonly TextMeshProUGUI _refreshLabel;

    private readonly List<GameObject> _rows = new();

    public GameObject Root { get; }
    public event Action          BackRequested;
    public event Action          RefreshRequested;
    public event Action<ulong>   JoinByLobbyIdRequested;

    public BrowserScreen(Transform parent, TMP_FontAsset font)
    {
        _font = font;
        Root  = MultiplayerPanelTheme.MakeGo("BrowserScreen", parent);
        MultiplayerPanelTheme.FullStretch(Root);

        // Header: Back + title + refresh
        BuildBackButton();
        var title = MultiplayerPanelTheme.Tmp(Root.transform, "Title", "Browse public lobbies",
            _font, 18, MultiplayerPanelTheme.TextPrimary, TextAlignmentOptions.Top, FontStyles.Normal);
        MultiplayerPanelTheme.Anchor(title.gameObject, new Vector2(0.15f, 0.91f), new Vector2(0.85f, 0.99f));

        _refreshLabel = BuildRefreshButton();

        // List container
        var listBg = MultiplayerPanelTheme.MakeGo("ListBg", Root.transform);
        MultiplayerPanelTheme.Anchor(listBg, new Vector2(0f, 0.08f), new Vector2(1f, 0.85f));
        MultiplayerPanelTheme.Fill(listBg, MultiplayerPanelTheme.SubBg);

        _listContainer = listBg.transform;

        // Empty state (centered)
        _emptyState = MultiplayerPanelTheme.Tmp(listBg.transform, "Empty",
            "No public lobbies right now.\nTry refreshing or host one yourself.",
            _font, 13, MultiplayerPanelTheme.TextDim, TextAlignmentOptions.Center, FontStyles.Italic);
        _emptyState.gameObject.SetActive(true);
    }

    public void SetLobbies(IReadOnlyList<LobbyEntry> lobbies)
    {
        // Clear old rows
        foreach (var go in _rows)
        {
            if (go != null) UnityEngine.Object.Destroy(go);
        }
        _rows.Clear();

        if (lobbies == null || lobbies.Count == 0)
        {
            _emptyState.gameObject.SetActive(true);
            return;
        }

        _emptyState.gameObject.SetActive(false);

        // Simple vertical layout: 56px per row from the top.
        const float rowHeightPx = 56f;
        for (int i = 0; i < lobbies.Count; i++)
        {
            int captured = i;
            var lobby = lobbies[i];
            var row = BuildRow(lobby, () => JoinByLobbyIdRequested?.Invoke(lobby.LobbyId));
            var rt = row.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(8f,  -((captured + 1) * (rowHeightPx + 6f)));
            rt.offsetMax = new Vector2(-8f, -(captured * (rowHeightPx + 6f) + 8f));
            _rows.Add(row);
        }
    }

    public void SetRefreshing(bool refreshing)
    {
        if (_refreshLabel != null)
            _refreshLabel.text = refreshing ? "…" : "⟳";
    }

    // ── Construction ─────────────────────────────────────────────────────────

    private void BuildBackButton()
    {
        var go = MultiplayerPanelTheme.MakeGo("BackBtn", Root.transform);
        MultiplayerPanelTheme.Anchor(go, new Vector2(0.0f, 0.91f), new Vector2(0.14f, 0.99f));
        var img = go.AddComponent<Image>();
        img.color         = new Color(0, 0, 0, 0);
        img.raycastTarget = true;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener((UnityAction)(() => BackRequested?.Invoke()));

        var lbl = MultiplayerPanelTheme.Tmp(go.transform, "Lbl", "← Back", _font, 12,
            MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.MidlineLeft, FontStyles.Normal);
        MultiplayerPanelTheme.Anchor(lbl.gameObject, Vector2.zero, Vector2.one, new Vector2(8f, 0f), Vector2.zero);
    }

    private TextMeshProUGUI BuildRefreshButton()
    {
        var go = MultiplayerPanelTheme.MakeGo("RefreshBtn", Root.transform);
        MultiplayerPanelTheme.Anchor(go, new Vector2(0.86f, 0.91f), new Vector2(1f, 0.99f));
        var img = go.AddComponent<Image>();
        img.color         = new Color(0, 0, 0, 0);
        img.raycastTarget = true;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener((UnityAction)(() => RefreshRequested?.Invoke()));

        var lbl = MultiplayerPanelTheme.Tmp(go.transform, "Lbl", "⟳", _font, 16,
            MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.Center, FontStyles.Bold);
        return lbl;
    }

    private GameObject BuildRow(LobbyEntry entry, Action onClick)
    {
        var row = MultiplayerPanelTheme.MakeGo("Row", _listContainer);
        var bg  = row.AddComponent<Image>();
        bg.color         = new Color(1f, 1f, 1f, 0.03f);
        bg.raycastTarget = true;

        var btn = row.AddComponent<Button>();
        btn.targetGraphic = bg;
        btn.onClick.AddListener((UnityAction)(() => onClick?.Invoke()));
        var colors = btn.colors;
        colors.normalColor      = Color.white;
        colors.highlightedColor = new Color(1f, 1f, 1f, 0.9f);
        btn.colors = colors;

        // Lobby name
        var name = MultiplayerPanelTheme.Tmp(row.transform, "Name", entry.Name, _font, 14,
            MultiplayerPanelTheme.TextPrimary, TextAlignmentOptions.MidlineLeft, FontStyles.Normal);
        MultiplayerPanelTheme.Anchor(name.gameObject, new Vector2(0f, 0.50f), new Vector2(0.65f, 1f),
            new Vector2(14f, 0f), Vector2.zero);

        // Host
        var host = MultiplayerPanelTheme.Tmp(row.transform, "Host",
            $"hosted by {entry.HostName}", _font, 11,
            MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.MidlineLeft, FontStyles.Normal);
        MultiplayerPanelTheme.Anchor(host.gameObject, new Vector2(0f, 0f), new Vector2(0.65f, 0.50f),
            new Vector2(14f, 0f), Vector2.zero);

        // Slots
        var slots = MultiplayerPanelTheme.Tmp(row.transform, "Slots",
            $"{entry.PlayerCount}/{entry.MaxPlayers}", _font, 14,
            MultiplayerPanelTheme.Accent, TextAlignmentOptions.MidlineRight, FontStyles.Bold);
        MultiplayerPanelTheme.Anchor(slots.gameObject, new Vector2(0.65f, 0f), new Vector2(0.92f, 1f));

        if (!string.IsNullOrEmpty(entry.Region))
        {
            var region = MultiplayerPanelTheme.Tmp(row.transform, "Region", entry.Region, _font, 10,
                MultiplayerPanelTheme.TextDim, TextAlignmentOptions.MidlineRight, FontStyles.Normal);
            MultiplayerPanelTheme.Anchor(region.gameObject, new Vector2(0.92f, 0f), new Vector2(1f, 1f),
                new Vector2(0f, 0f), new Vector2(-12f, 0f));
        }

        return row;
    }
}
