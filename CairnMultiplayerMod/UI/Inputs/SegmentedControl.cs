using System;
using System.Collections.Generic;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CairnMultiplayerMod.UI.Inputs;

/// <summary>
/// Toggle segmenté à N options ; un seul actif à la fois. Utilisé pour la
/// Visibility (Public / Friends / Private). Le segment actif reçoit un fond
/// en accent doré, les autres restent neutres.
/// </summary>
internal sealed class SegmentedControl
{
    private readonly List<Image> _segmentImgs = new();
    private readonly List<TextMeshProUGUI> _segmentTmps = new();
    private readonly List<Button> _segmentBtns = new();

    private int _selectedIndex;

    public GameObject Root { get; }
    public int SelectedIndex => _selectedIndex;
    public event Action<int> Changed;

    public SegmentedControl(Transform parent, TMP_FontAsset font, string[] labels, int initialIndex)
    {
        Root = MultiplayerPanelTheme.MakeGo("Segmented", parent);
        MultiplayerPanelTheme.Fill(Root, MultiplayerPanelTheme.Border);

        var inner = MultiplayerPanelTheme.MakeGo("Inner", Root.transform);
        MultiplayerPanelTheme.Anchor(inner, Vector2.zero, Vector2.one, new Vector2(1f, 1f), new Vector2(-1f, -1f));
        MultiplayerPanelTheme.Fill(inner, MultiplayerPanelTheme.SubBg);

        _selectedIndex = Mathf.Clamp(initialIndex, 0, labels.Length - 1);

        float w = 1f / labels.Length;
        for (int i = 0; i < labels.Length; i++)
        {
            int captured = i;
            float xMin = i * w;
            float xMax = (i + 1) * w;

            var seg = MultiplayerPanelTheme.MakeGo($"Seg{i}", inner.transform);
            MultiplayerPanelTheme.Anchor(seg, new Vector2(xMin, 0f), new Vector2(xMax, 1f));

            var bg = seg.AddComponent<Image>();
            bg.color         = i == _selectedIndex ? MultiplayerPanelTheme.AccentBg : new Color(0, 0, 0, 0);
            bg.raycastTarget = true;
            _segmentImgs.Add(bg);

            var btn = seg.AddComponent<Button>();
            btn.targetGraphic = bg;
            btn.onClick.AddListener((UnityAction)(() => Select(captured)));
            _segmentBtns.Add(btn);

            var tmp = MultiplayerPanelTheme.Tmp(seg.transform, "Label", labels[i], font, 12,
                i == _selectedIndex ? MultiplayerPanelTheme.Accent : MultiplayerPanelTheme.TextMuted,
                TextAlignmentOptions.Center,
                FontStyles.Normal);
            _segmentTmps.Add(tmp);
        }
    }

    public void SetInteractable(bool value)
    {
        foreach (var b in _segmentBtns) b.interactable = value;
    }

    public void SelectWithoutNotify(int index)
    {
        var next = Mathf.Clamp(index, 0, _segmentImgs.Count - 1);
        if (next == _selectedIndex) return;
        _selectedIndex = next;
        Refresh();
    }

    private void Select(int index)
    {
        if (index == _selectedIndex) return;
        _selectedIndex = index;
        Refresh();
        Changed?.Invoke(index);
    }

    private void Refresh()
    {
        for (int i = 0; i < _segmentImgs.Count; i++)
        {
            _segmentImgs[i].color = i == _selectedIndex ? MultiplayerPanelTheme.AccentBg : new Color(0, 0, 0, 0);
            _segmentTmps[i].color = i == _selectedIndex ? MultiplayerPanelTheme.Accent   : MultiplayerPanelTheme.TextMuted;
        }
    }
}
