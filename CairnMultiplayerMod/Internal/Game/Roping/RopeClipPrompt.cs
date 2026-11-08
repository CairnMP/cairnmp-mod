using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game;
using Il2Cpp;
using Il2CppTGBTools.InputImage;
using Il2CppTheGameBakers.Cairn;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Cairn's gameplay-prompt row for clipping in. The words are written onto the row because
/// this action has no loc key of its own. The keycap still comes from the prompt HUD.
/// </summary>
internal static class RopeClipPrompt
{
    internal const string Label = "Clip in";

    private const string GamepadBinding = "<Gamepad>/rightStickPress";

    private static InputActionAsset _asset;
    private static InputAction _action;
    private static GameplayPromptHud _hud;
    private static GameplayPromptHandle _handle;
    private static GameplayPromptUI _ui;
    private static bool _shown;
    private static bool _wantsVisible;
    private static bool _announced;
    private static bool _refreshBroken;
    private static bool _revealing;
    private static bool _inRow;
    private static bool _allowing;
    private static int _tutorialsFrame = -1;
    private static bool _tutorialsSkipped;

    internal static void Tick(bool show)
    {
        _wantsVisible = show;
        if (show) Show();
        else Hide();
    }

    internal static void Hide()
    {
        _wantsVisible = false;
        if (!_shown && !_handle.registered && !_handle.Valid) return;
        try
        {
            if (_hud != null && (_handle.registered || _handle.Valid))
                _hud.UnregisterPrompt(ref _handle);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.hide", exception);
            _hud = null;
        }

        _shown = false;
        _ui = null;
        _handle = default;
        DisableAction();
    }

    private static void DisableAction()
    {
        try { _action?.Disable(); }
        catch (Exception exception) { ModLog.SuppressedException("rope.clip-prompt.disable", exception); }
    }

    /// <summary>
    /// The save asked to skip tutorials. A missing setup does not count, so a tutorial card
    /// can still hide the prompt row.
    /// </summary>
    internal static bool TutorialsSkipped()
    {
        var frame = Time.frameCount;
        if (frame == _tutorialsFrame) return _tutorialsSkipped;
        _tutorialsFrame = frame;
        _tutorialsSkipped = ReadTutorialsSkipped();
        return _tutorialsSkipped;
    }

    private static bool ReadTutorialsSkipped()
    {
        try
        {
            var setup = GameSetup.GetCurrentGameSetup();
            return setup != null && setup.IgnoreTutorials;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.tutorials", exception);
            return false;
        }
    }

    /// <summary>
    /// Lobby games leave the prompt canvas closed. Open it for this row, and leave pause,
    /// cutscenes, and photo mode alone.
    /// </summary>
    internal static bool AllowDespiteSkippedTutorials(GameplayPromptHud hud)
    {
        if (_allowing || !_shown || !_wantsVisible || hud == null || !TutorialsSkipped()) return false;
        _allowing = true;
        try
        {
            // The interop surface is an int. Zero is an ordinary climb.
            if (hud.inCutscene != 0) return false;
            if (MultiplayerPausePatch.IsPauseMenuActive) return false;
            if (PhotoModeNamesRow.IsPhotoModeOpen()) return false;
            return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.allow", exception);
            return false;
        }
        finally
        {
            _allowing = false;
        }
    }

    internal static void RevealCanvas(GameplayPromptHud hud)
    {
        if (_revealing || hud == null) return;
        _revealing = true;
        try
        {
            var group = hud.canvasGroup;
            if (group != null && group.alpha < 1f)
                group.alpha = 1f;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.canvas", exception);
        }
        finally
        {
            _revealing = false;
        }
    }

    internal static void RevealIfAllowed()
    {
        if (_hud != null && AllowDespiteSkippedTutorials(_hud))
            RevealCanvas(_hud);
    }

    /// <summary>
    /// Called after gameplay UI updates so a loc refresh cannot leave the borrowed key on screen.
    /// </summary>
    internal static void ApplyLabel()
    {
        if (!_shown || _hud == null) return;
        try
        {
            BindRow();
            if (_ui != null) WriteLabel(_ui);
            RevealIfAllowed();
            HideForeignRows();
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.label", exception);
            _ui = null;
        }
    }

    internal static void AfterRowLateUpdate(GameplayPromptUI row)
    {
        if (_inRow || !_shown || row == null) return;
        _inRow = true;
        try
        {
            if (IsOurRow(row))
            {
                _ui = row;
                WriteLabel(row);
                return;
            }

            if (_hud == null || !AllowDespiteSkippedTutorials(_hud)) return;
            var other = row.gameObject;
            if (other != null && other.activeSelf)
                other.SetActive(false);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.row", exception);
        }
        finally
        {
            _inRow = false;
        }
    }

    private static void Show()
    {
        if (_hud == null)
        {
            _shown = false;
            _handle = default;
            _ui = null;
        }

        if (_shown && _hud != null && (_handle.registered || _handle.Valid))
        {
            ApplyLabel();
            return;
        }

        if (!TryResolveHud()) return;
        if (!EnsureAction()) return;
        if (TryAdoptExisting())
        {
            NoteShown();
            ApplyLabel();
            return;
        }

        try
        {
            _action.Enable();
            var data = new GameplayPromptData
            {
                // The row will not build a keycap without a loc key. The visible words are replaced.
                locKey = LocKeyStringIdEnum.GAMEPLAY_PROMPT_CLIMB,
                inputAction0 = _action,
                canInteract = true,
            };
            _handle = _hud.RegisterPrompt(data, false, 0f);
            _ui = null;
            _shown = _handle.registered || _handle.Valid;
            if (!_shown)
            {
                DisableAction();
                ModLog.SuppressedException("rope.clip-prompt.register",
                    new InvalidOperationException("GameplayPromptHud declined the clip-in prompt."));
                return;
            }

            NoteShown();
            BindRow();
            ApplyLabel();
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.register", exception);
            _shown = false;
            _ui = null;
            _handle = default;
            DisableAction();
        }
    }

    private static void NoteShown()
    {
        _shown = true;
        if (_announced) return;
        _announced = true;
        ModLog.Info("[Rope] Clip-in prompt is showing.");
    }

    private static void BindRow()
    {
        if (_ui != null || _hud == null) return;
        _ui = FindUi(_hud.bigGameplayPromptSet) ?? FindUi(_hud.smallGameplayPromptSet);
        if (_ui != null || _refreshBroken) return;
        try
        {
            _hud.UpdateDisplayedPrompts();
        }
        catch (Exception exception)
        {
            _refreshBroken = true;
            ModLog.SuppressedException("rope.clip-prompt.refresh", exception);
            return;
        }

        _ui = FindUi(_hud.bigGameplayPromptSet) ?? FindUi(_hud.smallGameplayPromptSet);
    }

    private static void WriteLabel(GameplayPromptUI row)
    {
        var text = row?.label?.text;
        if (text == null || text.text == Label) return;
        text.text = Label;
        row.rebuidLayout = true;
    }

    private static void HideForeignRows()
    {
        if (_ui == null || _hud == null || !AllowDespiteSkippedTutorials(_hud)) return;
        HideForeign(_hud.bigGameplayPromptSet);
        HideForeign(_hud.smallGameplayPromptSet);
        var ours = _ui.gameObject;
        if (ours != null && !ours.activeSelf)
            ours.SetActive(true);
    }

    private static void HideForeign(GameplayPromptHud.GameplayPromptSet set)
    {
        var rows = set?.promptUis;
        if (rows == null) return;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row == null || IsOurRow(row)) continue;
            var go = row.gameObject;
            if (go != null && go.activeSelf)
                go.SetActive(false);
        }
    }

    /// <summary>
    /// A prompt already filed under this action is reused. Registering again would stack rows.
    /// </summary>
    private static bool TryAdoptExisting()
    {
        return TryAdopt(_hud.bigGameplayPromptSet) || TryAdopt(_hud.smallGameplayPromptSet);
    }

    private static bool TryAdopt(GameplayPromptHud.GameplayPromptSet set)
    {
        if (!TryFind(set, out var live, out var row)) return false;
        _handle = live.handle;
        _ui = row;
        return true;
    }

    private static bool TryResolveHud()
    {
        if (_hud != null) return true;
        try
        {
            _hud = MoSingleton<GlobalUIs>.Instance?.gameplayPromptHud;
            return _hud != null;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.hud", exception);
            return false;
        }
    }

    private static bool EnsureAction()
    {
        if (_action != null) return true;
        try
        {
            _asset = ScriptableObject.CreateInstance<InputActionAsset>();
            _asset.name = "CairnMP clip prompt";
            var map = _asset.AddActionMap("CairnMPClip");
            _action = map.AddAction(
                "ClipIn",
                InputActionType.Button,
                "<Keyboard>/" + RopeCoupleController.ToggleKeyName.ToLowerInvariant());
            // The same click RightStickLinkPressed reads. View/Share still leaves the rope alone.
            _action.AddBinding(GamepadBinding);
            return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.action", exception);
            if (_asset != null) UnityEngine.Object.Destroy(_asset);
            _asset = null;
            _action = null;
            return false;
        }
    }

    private static GameplayPromptUI FindUi(GameplayPromptHud.GameplayPromptSet set)
    {
        return TryFind(set, out _, out var row) ? row : null;
    }

    private static bool TryFind(GameplayPromptHud.GameplayPromptSet set, out LiveGameplayPrompt live, out GameplayPromptUI row)
    {
        live = null;
        row = null;
        if (set == null || _action == null) return false;
        var prompts = set.gameplayPrompts;
        var rows = set.promptUis;
        if (prompts == null || rows == null) return false;

        for (var i = 0; i < rows.Count; i++)
        {
            if (!RowShowsAction(rows[i])) continue;
            row = rows[i];
            live = MatchingLive(prompts);
            return live != null;
        }

        var count = Math.Min(prompts.Count, rows.Count);
        for (var i = 0; i < count; i++)
        {
            var candidate = prompts[i];
            var data = candidate?.data;
            if (data == null || data.inputAction0 != _action || rows[i] == null) continue;
            live = candidate;
            row = rows[i];
            return true;
        }

        return false;
    }

    private static LiveGameplayPrompt MatchingLive(Il2CppSystem.Collections.Generic.List<LiveGameplayPrompt> prompts)
    {
        if (prompts == null) return null;
        for (var i = 0; i < prompts.Count; i++)
        {
            var candidate = prompts[i];
            var data = candidate?.data;
            if (data != null && data.inputAction0 == _action) return candidate;
        }

        return null;
    }

    private static bool IsOurRow(GameplayPromptUI row)
    {
        if (row == null) return false;
        if (RowShowsAction(row)) return true;
        return _ui != null && row.Pointer == _ui.Pointer;
    }

    private static bool RowShowsAction(GameplayPromptUI row)
    {
        if (row == null || _action == null) return false;
        try
        {
            InputImageStaticAction image = row.InputImage0;
            return image != null && image.InputAction == _action;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.row-action", exception);
            return false;
        }
    }
}
