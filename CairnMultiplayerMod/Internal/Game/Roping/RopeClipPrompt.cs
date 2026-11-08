using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game;
using Il2Cpp;
using Il2CppTGBTools.InputImage;
using Il2CppTheGameBakers.Cairn;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Cairn's gameplay-prompt rows for our control tips. The words are written onto the row
/// because these actions have no loc key of their own. The keycap still comes from the
/// prompt HUD. A word such as Tap or Hold is written to the left of that keycap.
/// </summary>
internal static class RopeClipPrompt
{
    internal const string Label = "Clip in";
    internal const string CutLabel = "Cut rope";
    internal const string RopeId = "rope";

    private const string GamepadBinding = "<Gamepad>/rightStickPress";

    private sealed class Slot
    {
        internal string Text;
        internal string Cue;
        internal string Keyboard;
        internal string Gamepad;
        internal InputAction Action;
        internal GameplayPromptHandle Handle;
        internal GameplayPromptUI Ui;
        internal bool Shown;
        internal bool Wants;
        internal string Announced;
    }

    private static readonly Dictionary<string, Slot> Slots = new(StringComparer.Ordinal);
    private static InputActionAsset _asset;
    private static GameplayPromptHud _hud;
    private static bool _refreshBroken;
    private static bool _revealing;
    private static bool _inRow;
    private static bool _allowing;
    private static int _tutorialsFrame = -1;
    private static bool _tutorialsSkipped;

    internal static void Tick(bool show) => Tick(show, Label);

    internal static void Tick(bool show, string label)
        => Tick(RopeId, show, label, cue: null,
            "<Keyboard>/" + RopeCoupleController.ToggleKeyName.ToLowerInvariant(),
            GamepadBinding);

    internal static void Tick(string id, bool show, string label, string cue, string keyboard, string gamepad)
    {
        if (string.IsNullOrEmpty(id)) return;
        var slot = SlotFor(id);
        if (!string.IsNullOrEmpty(label)) slot.Text = label;
        slot.Cue = cue;
        if (!string.IsNullOrEmpty(keyboard)) slot.Keyboard = keyboard;
        slot.Gamepad = gamepad;
        slot.Wants = show;
        if (show) Show(slot);
        else Hide(slot);
    }

    internal static void Hide() => Hide(RopeId);

    internal static void Hide(string id)
    {
        if (id == null || !Slots.TryGetValue(id, out var slot)) return;
        Hide(slot);
    }

    internal static void HideAll()
    {
        if (Slots.Count == 0) return;
        var ids = new string[Slots.Count];
        Slots.Keys.CopyTo(ids, 0);
        foreach (var id in ids) Hide(id);
    }

    private static void Hide(Slot slot)
    {
        slot.Wants = false;
        if (!slot.Shown && !slot.Handle.registered && !slot.Handle.Valid) return;
        try
        {
            if (_hud != null && (slot.Handle.registered || slot.Handle.Valid))
                _hud.UnregisterPrompt(ref slot.Handle);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.hide", exception);
            _hud = null;
        }

        slot.Shown = false;
        slot.Ui = null;
        slot.Handle = default;
        DisableAction(slot);
    }

    private static void DisableAction(Slot slot)
    {
        try { slot.Action?.Disable(); }
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
    /// Lobby games leave the prompt canvas closed. Open it while any of our tips is up, and
    /// leave pause, cutscenes, and photo mode alone.
    /// </summary>
    internal static bool AllowDespiteSkippedTutorials(GameplayPromptHud hud)
    {
        if (_allowing || hud == null || !AnyWanted() || !TutorialsSkipped()) return false;
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
        if (_hud == null || !AnyWanted()) return;
        try
        {
            foreach (var slot in Slots.Values)
            {
                if (!slot.Shown) continue;
                BindRow(slot);
                if (slot.Ui == null) continue;
                WriteLabel(slot.Ui, slot);
                WriteCue(slot.Ui, slot);
            }

            RevealIfAllowed();
            HideForeignRows();
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.label", exception);
            foreach (var slot in Slots.Values) slot.Ui = null;
        }
    }

    internal static void AfterRowLateUpdate(GameplayPromptUI row)
    {
        if (_inRow || row == null || !AnyWanted()) return;
        _inRow = true;
        try
        {
            var slot = SlotForRow(row);
            if (slot != null)
            {
                slot.Ui = row;
                WriteLabel(row, slot);
                WriteCue(row, slot);
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

    private static bool AnyWanted()
    {
        foreach (var slot in Slots.Values)
            if (slot.Shown && slot.Wants) return true;
        return false;
    }

    private static Slot SlotFor(string id)
    {
        if (!Slots.TryGetValue(id, out var slot))
        {
            slot = new Slot();
            Slots[id] = slot;
        }

        return slot;
    }

    private static void Show(Slot slot)
    {
        if (_hud == null)
        {
            slot.Shown = false;
            slot.Handle = default;
            slot.Ui = null;
        }

        if (slot.Shown && _hud != null && (slot.Handle.registered || slot.Handle.Valid))
        {
            NoteShown(slot);
            ApplyLabel();
            return;
        }

        if (!TryResolveHud()) return;
        if (!EnsureAction(slot)) return;
        if (TryAdoptExisting(slot))
        {
            NoteShown(slot);
            ApplyLabel();
            return;
        }

        try
        {
            slot.Action.Enable();
            var data = new GameplayPromptData
            {
                // The row will not build a keycap without a loc key. The visible words are replaced.
                locKey = LocKeyStringIdEnum.GAMEPLAY_PROMPT_CLIMB,
                inputAction0 = slot.Action,
                canInteract = true,
            };
            slot.Handle = _hud.RegisterPrompt(data, false, 0f);
            slot.Ui = null;
            slot.Shown = slot.Handle.registered || slot.Handle.Valid;
            if (!slot.Shown)
            {
                DisableAction(slot);
                ModLog.SuppressedException("rope.clip-prompt.register",
                    new InvalidOperationException("GameplayPromptHud declined the control tip."));
                return;
            }

            NoteShown(slot);
            BindRow(slot);
            ApplyLabel();
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.register", exception);
            slot.Shown = false;
            slot.Ui = null;
            slot.Handle = default;
            DisableAction(slot);
        }
    }

    private static void NoteShown(Slot slot)
    {
        slot.Shown = true;
        if (slot.Announced == slot.Text) return;
        slot.Announced = slot.Text;
        ModLog.Info("[Control] Tip is showing: " + slot.Text);
    }

    private static void BindRow(Slot slot)
    {
        if (slot.Ui != null || _hud == null) return;
        slot.Ui = FindUi(slot, _hud.bigGameplayPromptSet) ?? FindUi(slot, _hud.smallGameplayPromptSet);
        if (slot.Ui != null || _refreshBroken) return;
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

        slot.Ui = FindUi(slot, _hud.bigGameplayPromptSet) ?? FindUi(slot, _hud.smallGameplayPromptSet);
    }

    private static void WriteLabel(GameplayPromptUI row, Slot slot)
    {
        var text = row?.label?.text;
        if (text == null || text.text == slot.Text) return;
        text.text = slot.Text;
        row.rebuidLayout = true;
    }

    /// <summary>
    /// The prompt's hold word sits to the left of the keycap. Clip in leaves it blank.
    /// </summary>
    private static void WriteCue(GameplayPromptUI row, Slot slot)
    {
        var hold = row?.inputActionUI0?.holdText;
        var tmp = hold?.text;
        if (tmp == null) return;
        var show = !string.IsNullOrEmpty(slot.Cue);
        var target = tmp.gameObject;
        if (target != null && target.activeSelf != show)
            target.SetActive(show);
        if (!show || tmp.text == slot.Cue) return;
        tmp.text = slot.Cue;
        row.rebuidLayout = true;
    }

    private static void HideForeignRows()
    {
        if (_hud == null || !AllowDespiteSkippedTutorials(_hud)) return;
        HideForeign(_hud.bigGameplayPromptSet);
        HideForeign(_hud.smallGameplayPromptSet);
        foreach (var slot in Slots.Values)
        {
            if (!slot.Shown || slot.Ui == null) continue;
            var ours = slot.Ui.gameObject;
            if (ours != null && !ours.activeSelf)
                ours.SetActive(true);
        }
    }

    private static void HideForeign(GameplayPromptHud.GameplayPromptSet set)
    {
        var rows = set?.promptUis;
        if (rows == null) return;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row == null || SlotForRow(row) != null) continue;
            var go = row.gameObject;
            if (go != null && go.activeSelf)
                go.SetActive(false);
        }
    }

    /// <summary>
    /// A prompt already filed under this action is reused. Registering again would stack rows.
    /// </summary>
    private static bool TryAdoptExisting(Slot slot)
    {
        return TryAdopt(slot, _hud.bigGameplayPromptSet) || TryAdopt(slot, _hud.smallGameplayPromptSet);
    }

    private static bool TryAdopt(Slot slot, GameplayPromptHud.GameplayPromptSet set)
    {
        if (!TryFind(slot, set, out var live, out var row)) return false;
        slot.Handle = live.handle;
        slot.Ui = row;
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

    private static bool EnsureAction(Slot slot)
    {
        if (slot.Action != null) return true;
        if (string.IsNullOrEmpty(slot.Keyboard)) return false;
        try
        {
            if (_asset == null)
            {
                _asset = ScriptableObject.CreateInstance<InputActionAsset>();
                _asset.name = "CairnMP control tips";
                _asset.AddActionMap("CairnMPTips");
            }

            var map = _asset.FindActionMap("CairnMPTips");
            slot.Action = map.AddAction(slot.Text ?? "Tip", InputActionType.Button, slot.Keyboard);
            // The same control the gameplay press reads. A chord such as View/Share is not a binding.
            if (!string.IsNullOrEmpty(slot.Gamepad))
                slot.Action.AddBinding(slot.Gamepad);
            return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.action", exception);
            slot.Action = null;
            return false;
        }
    }

    private static GameplayPromptUI FindUi(Slot slot, GameplayPromptHud.GameplayPromptSet set)
    {
        return TryFind(slot, set, out _, out var row) ? row : null;
    }

    private static bool TryFind(Slot slot, GameplayPromptHud.GameplayPromptSet set,
        out LiveGameplayPrompt live, out GameplayPromptUI row)
    {
        live = null;
        row = null;
        if (set == null || slot.Action == null) return false;
        var prompts = set.gameplayPrompts;
        var rows = set.promptUis;
        if (prompts == null || rows == null) return false;

        for (var i = 0; i < rows.Count; i++)
        {
            if (!RowShowsAction(rows[i], slot.Action)) continue;
            row = rows[i];
            live = MatchingLive(prompts, slot.Action);
            return live != null;
        }

        var count = Math.Min(prompts.Count, rows.Count);
        for (var i = 0; i < count; i++)
        {
            var candidate = prompts[i];
            var data = candidate?.data;
            if (data == null || data.inputAction0 != slot.Action || rows[i] == null) continue;
            live = candidate;
            row = rows[i];
            return true;
        }

        return false;
    }

    private static LiveGameplayPrompt MatchingLive(
        Il2CppSystem.Collections.Generic.List<LiveGameplayPrompt> prompts, InputAction action)
    {
        if (prompts == null || action == null) return null;
        for (var i = 0; i < prompts.Count; i++)
        {
            var candidate = prompts[i];
            var data = candidate?.data;
            if (data != null && data.inputAction0 == action) return candidate;
        }

        return null;
    }

    private static Slot SlotForRow(GameplayPromptUI row)
    {
        if (row == null) return null;
        foreach (var slot in Slots.Values)
        {
            if (!slot.Shown) continue;
            if (slot.Ui != null && row.Pointer == slot.Ui.Pointer) return slot;
            if (RowShowsAction(row, slot.Action)) return slot;
        }

        return null;
    }

    private static bool RowShowsAction(GameplayPromptUI row, InputAction action)
    {
        if (row == null || action == null) return false;
        try
        {
            InputImageStaticAction image = row.InputImage0;
            return image != null && image.InputAction == action;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clip-prompt.row-action", exception);
            return false;
        }
    }
}
