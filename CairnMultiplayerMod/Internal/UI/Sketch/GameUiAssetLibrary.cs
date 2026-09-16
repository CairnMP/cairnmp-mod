using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppTMPro;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.UI.Sketch;

/// <summary>
/// Runtime lookup avoids bundling copies of Cairn's UI assets; a solid fallback keeps the panel
/// usable if a game update renames one of them.
/// </summary>
internal static class GameUiAssetLibrary
{
    // Exact names captured via the runtime dump (UiAssetDump, F8).
    public const string Frame = "UI_Photomode_Contour";
    public const string PanelTitle = "UI_Photomode_Background_Title";
    public const string PanelBody = "UI_Photomode_Background_Body";
    public const string RowBg = "UI_Photomode_Background_CursorNavig";
    public const string SliderTrack = "UI_Photomode_Body_CursorBackground";
    public const string SliderKnob = "UI_Photomode_Body_Cursor";
    public const string ToggleOff = "UI_Photomode_Body_Coche";
    public const string ToggleOn = "UI_Photomode_Body_CocheFull";
    public const string Arrow = "UI_Photomode_Arrow";
    public const string IconCamera = "T_UI_PhotoModeIcons_camera";
    public const string IconLens = "T_UI_PhotoModeIcons_Lens";
    public const string IconMountain = "T_UI_PhotoModeIcons_mountain";
    public const string IconSilhouette = "T_UI_PhotoModeIcons_silhouette";
    public const string IconEffects = "T_UI_PhotoModeIcons_effects";
    public const string IconSave = "T_UI_PhotoModeIcons_save";
    public const string IconFriend = "FM_Preference_FriendGhost";
    public const string IconPlayer = "FM_Preference_PlayerGhost";
    public const string Button = "UI_Bivouac_Bouton";
    public const string White = "UI_White_1PX";

    private static readonly string[] Wanted =
    {
        Frame, PanelTitle, PanelBody, RowBg, SliderTrack, SliderKnob, ToggleOff, ToggleOn, Arrow,
        IconCamera, IconLens, IconMountain, IconSilhouette, IconEffects, IconSave,
        IconFriend, IconPlayer, Button, White,
    };

    private static readonly Dictionary<string, Sprite> _sprites = new();
    private static TMP_FontAsset _textFont;
    private static TMP_FontAsset _logoFont;
    private static Sprite _fallback;
    private static bool _harvested;

    public static void EnsureHarvested(bool force = false)
    {
        if (_harvested && !force && _sprites.TryGetValue(Frame, out var existing) && existing != null) return;

        _sprites.Clear();
        var wanted = new HashSet<string>(Wanted);

        var all = Resources.FindObjectsOfTypeAll<Sprite>();
        if (all != null)
        {
            for (int i = 0; i < all.Length; i++)
            {
                var s = all[i];
                if (s == null) continue;
                var n = s.name;
                if (string.IsNullOrEmpty(n) || _sprites.ContainsKey(n)) continue;
                if (wanted.Contains(n)) _sprites[n] = s;
            }
        }

        var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        if (fonts != null)
        {
            for (int i = 0; i < fonts.Length; i++)
            {
                var ft = fonts[i];
                if (ft == null || string.IsNullOrEmpty(ft.name)) continue;
                if (ft.name == "Cairn_TextFont") _textFont = ft;
                else if (ft.name == "Cairn_LogoFont") _logoFont = ft;
            }
        }

        _harvested = true;
        ModLog.Debug($"[Sketch] GameUiAssetLibrary harvested {_sprites.Count}/{Wanted.Length} sprites, " +
            $"textFont={(_textFont != null)} logoFont={(_logoFont != null)}");
    }

    public static Sprite Get(string name)
    {
        EnsureHarvested();
        if (_sprites.TryGetValue(name, out var s) && s != null) return s;
        return Fallback;
    }

    public static TMP_FontAsset TextFont { get { EnsureHarvested(); return _textFont; } }

    public static TMP_FontAsset LogoFont { get { EnsureHarvested(); return _logoFont != null ? _logoFont : _textFont; } }

    public static Sprite Fallback
    {
        get
        {
            if (_fallback != null) return _fallback;
            if (_sprites.TryGetValue(White, out var w) && w != null) { _fallback = w; return _fallback; }
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            _fallback = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            return _fallback;
        }
    }
}
