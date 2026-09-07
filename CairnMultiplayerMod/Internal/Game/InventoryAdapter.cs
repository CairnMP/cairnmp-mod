using System;
using System.Collections.Generic;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Narrow bridge to Cairn's inventory. Only ordinary consumables without per-unit state or
/// contained items are eligible. This keeps quest progression, equipment durability,
/// containers and unique collectibles out of the multiplayer transfer protocol.
/// </summary>
internal sealed class InventoryAdapter : IInventoryApi
{
    private const int MaxTransferCount = 10;
    private readonly List<ShareableItem> _items = new();
    private readonly Dictionary<uint, GroundVisual> _groundVisuals = new();
    private ShareActionsRegistration _shareActions;
    private BagInventorySection _section;
    private InventoryInputPrompt _givePrompt;
    private InventoryInputPrompt _dropPrompt;
    private InputAction _giveInput;
    private InputAction _dropInput;
    private InputActionAsset _shareInputAsset;
    private Il2CppSystem.Action _giveDelegate;
    private Il2CppSystem.Action _dropDelegate;
    private int _lastSectionSearchFrame;
    private bool _creationErrorReported;

    internal void Tick()
    {
        TickNativeActions();
    }

    /// <summary>
    /// The shareable consumables of the bag, identical stacks aggregated. Internal to the
    /// adapter: a feature reads the selected stack (<see cref="TryGetSelectedShareableItem"/>),
    /// it never browses the bag - giving and dropping start from the native backpack actions.
    /// </summary>
    private IReadOnlyList<ShareableItem> GetShareableItems()
    {
        _items.Clear();
        try
        {
            var bag = InventoryManager.Instance?.BagInventoryData;
            if (bag == null) return _items;

            var totals = new Dictionary<int, (ushort UniqueId, string Name, int Count)>();
            for (var index = 0; index < bag.Count; index++)
            {
                var data = bag.GetBagItemData(index)?.ItemData;
                if (!IsShareable(data)) continue;

                var definitionId = data.itemId.value;
                var count = Math.Max(0, data.Count);
                if (totals.TryGetValue(definitionId, out var current))
                    totals[definitionId] = (current.UniqueId, current.Name, current.Count + count);
                else
                    totals.Add(definitionId, (data.UniqueID, DisplayName(definitionId), count));
            }

            foreach (var pair in totals)
                _items.Add(new ShareableItem(pair.Value.UniqueId, pair.Key,
                    pair.Value.Name, pair.Value.Count));
            _items.Sort((left, right) => string.Compare(left.Name, right.Name,
                StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("inventory.list-shareable", exception);
        }
        return _items;
    }

    public bool TryGetSelectedShareableItem(out ShareableItem item)
    {
        item = default;
        try
        {
            var data = _section?.GetSelectedItem();
            if (!IsShareable(data)) return false;
            item = new ShareableItem(data.UniqueID, data.itemId.value,
                DisplayName(data.itemId.value), data.Count);
            return data.Count > 0;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("inventory.selected-shareable", exception);
            return false;
        }
    }

    public IGameRegistration AddShareActions(Func<bool> canGive, Action<ShareableItem> give,
        Func<bool> canDrop, Action<ShareableItem> drop)
    {
        _shareActions?.Dispose();
        _shareActions = new ShareActionsRegistration(this, canGive, give, canDrop, drop);
        return _shareActions;
    }

    public void SetGroundItems(IReadOnlyList<GroundItem> items)
    {
        var keep = new HashSet<uint>();
        if (items != null)
            foreach (var item in items)
            {
                if (!IsSceneLoaded(item.Scene)) continue;
                keep.Add(item.Id);
                if (!_groundVisuals.ContainsKey(item.Id)) CreateGroundVisual(item);
            }

        if (_groundVisuals.Count == keep.Count) return;
        var removed = new List<uint>();
        foreach (var pair in _groundVisuals)
            if (!keep.Contains(pair.Key)) removed.Add(pair.Key);
        foreach (var id in removed) DestroyGroundVisual(id);
    }

    private static bool IsSceneLoaded(string sceneName)
    {
        // SceneManager.GetSceneByName(string) goes through an Il2Cpp ReadOnlySpan wrapper
        // that is missing at runtime in Cairn's generated assemblies. Indexed lookup keeps
        // the same semantics without crossing that broken string-marshalling path.
        for (var index = 0; index < SceneManager.sceneCount; index++)
        {
            var scene = SceneManager.GetSceneAt(index);
            if (scene.isLoaded && string.Equals(scene.name, sceneName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private readonly List<GroundItem> _pickupItems = new();
    private uint _selectedGroundId;
    private GUIStyle _groundLabel;
    private GUIStyle _groundKey;
    private Texture2D _groundKeyTexture;
    private int _pickupFrame;

    public uint SelectGroundItem(IReadOnlyList<GroundItem> nearbyItems)
    {
        _pickupItems.Clear();
        // E is also used by native inventory actions; never offer world pickup over it.
        if (_section != null && _section.isActiveAndEnabled
            && _section.CanvasGroup != null && _section.CanvasGroup.alpha > .01f)
            return 0;
        _pickupItems.AddRange(nearbyItems);
        // Replicated ground state can arrive while Cairn is still streaming the gameplay
        // scene. Retry visual creation here, once the player is actually close enough to
        // interact, instead of leaving the carousel with permanent question marks.
        foreach (var item in _pickupItems)
            if (!_groundVisuals.ContainsKey(item.Id)) CreateGroundVisual(item);
        _pickupFrame = Time.frameCount;
        if (_pickupItems.Count == 0) { _selectedGroundId = 0; return 0; }
        var index = _pickupItems.FindIndex(item => item.Id == _selectedGroundId);
        if (index < 0) index = 0;
        var scroll = Mouse.current?.scroll.ReadValue().y ?? 0f;
        var keyboard = Keyboard.current;
        var direction = scroll > 0 || keyboard?.rightArrowKey.wasPressedThisFrame == true ? 1
            : scroll < 0 || keyboard?.leftArrowKey.wasPressedThisFrame == true ? -1 : 0;
        index = (index + direction + _pickupItems.Count) % _pickupItems.Count;
        _selectedGroundId = _pickupItems[index].Id;
        return _selectedGroundId;
    }

    /// <summary>World-anchored pickup legend inspired by Cairn's native interaction UI.</summary>
    public void DrawGroundItems()
    {
        if (_pickupItems.Count == 0 || Time.frameCount - _pickupFrame > 1
            || Event.current?.type != EventType.Repaint) return;
        var camera = Camera.main;
        if (camera == null) return;
        var index = _pickupItems.FindIndex(item => item.Id == _selectedGroundId);
        if (index < 0) return;
        var selected = _pickupItems[index];
        var anchor = Vector3.zero;
        foreach (var item in _pickupItems) anchor += new Vector3(item.X, item.Y + .35f, item.Z);
        anchor /= _pickupItems.Count;
        var screen = camera.WorldToScreenPoint(anchor);
        if (screen.z <= 0f || screen.x < 0 || screen.x > Screen.width
            || screen.y < 0 || screen.y > Screen.height) return;

        var scale = Mathf.Clamp(Screen.height / 1080f, .65f, 1.6f);
        _groundLabel ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.BoldAndItalic,
            richText = false
        };
        _groundKey ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold,
            richText = false
        };
        _groundLabel.fontSize = Mathf.RoundToInt(22f * scale);
        _groundLabel.normal.textColor = new Color(.98f, .97f, .88f);
        _groundKey.fontSize = Mathf.RoundToInt(22f * scale);
        _groundKey.normal.textColor = new Color(.15f, .16f, .12f);
        _groundKeyTexture ??= BuildRoundedRectTexture(64, 14f);
        var previousColor = GUI.color;
        try
        {
            var x = Mathf.Clamp(screen.x, 220f * scale, Screen.width - 220f * scale);
            var y = Mathf.Clamp(Screen.height - screen.y - 155f * scale,
                110f * scale, Screen.height - 175f * scale);
            GUI.color = Color.white;
            GroundLabel(x, y - 60f * scale, selected.Name, 360f * scale, scale);
            if (_pickupItems.Count == 1)
            {
                GroundKey(x, y, scale);
                GroundLine(new Vector2(x, y + 23f * scale),
                    new Vector2(screen.x, Screen.height - screen.y), scale);
                return;
            }

            var visible = Math.Min(5, _pickupItems.Count);
            var first = Math.Max(0, Math.Min(index - visible / 2, _pickupItems.Count - visible));
            var left = x - (visible - 1) * 36f * scale;
            for (var slot = 0; slot < visible; slot++)
            {
                var itemIndex = first + slot;
                var item = _pickupItems[itemIndex];
                var iconX = left + slot * 72f * scale;
                var active = itemIndex == index;
                var size = (active ? 66f : 50f) * scale;
                var iconY = y - (active ? 10f : 0f) * scale;
                if (_groundVisuals.TryGetValue(item.Id, out var visual) && visual.Icon != null)
                    DrawGroundIcon(visual.Icon,
                        new Rect(iconX - size / 2f, iconY - size / 2f, size, size));
                else GroundLabel(iconX, iconY, "?", size, scale);
                if (active) GroundLabel(iconX, y + 50f * scale, "▼", 35f * scale, scale);
            }
            GroundLabel(left - 45f * scale, y, "◀", 40f * scale, scale);
            GroundLabel(left + (visible - 1) * 72f * scale + 45f * scale, y, "▶", 40f * scale, scale);
            GroundLine(new Vector2(left - 25f * scale, y + 38f * scale),
                new Vector2(left + (visible - 1) * 72f * scale + 25f * scale, y + 41f * scale), scale);
            GroundLabel(x, y + 88f * scale, "Scroll / ← →   Navigate", 360f * scale, scale);
            GroundKey(x - 65f * scale, y + 126f * scale, scale);
            GroundLabel(x + 25f * scale, y + 126f * scale, "Pick up", 140f * scale, scale);
        }
        finally { GUI.color = previousColor; }
    }

    private void GroundLabel(float x, float y, string text, float width, float scale)
        => GUI.Label(new Rect(x - width / 2f, y - 18f * scale, width, 36f * scale), text, _groundLabel);

    private void GroundKey(float x, float y, float scale)
    {
        var rect = new Rect(x - 19f * scale, y - 19f * scale, 38f * scale, 38f * scale);
        GUI.color = new Color(.98f, .97f, .88f);
        GUI.DrawTexture(rect, _groundKeyTexture);
        GUI.color = Color.white;
        GUI.Label(rect, "E", _groundKey);
    }

    private static Texture2D BuildRoundedRectTexture(int size, float radius)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "CairnMP rounded input key",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var px = x + .5f;
            var py = y + .5f;
            var dx = Mathf.Max(radius - px, 0f, px - (size - radius));
            var dy = Mathf.Max(radius - py, 0f, py - (size - radius));
            var alpha = Mathf.Clamp01(radius + .75f - Mathf.Sqrt(dx * dx + dy * dy));
            texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
        }
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return texture;
    }

    private static void GroundLine(Vector2 from, Vector2 to, float scale)
    {
        var matrix = GUI.matrix;
        try
        {
            GUIUtility.RotateAroundPivot(Mathf.Atan2(to.y - from.y, to.x - from.x) * Mathf.Rad2Deg, from);
            GUI.DrawTexture(new Rect(from.x, from.y, Vector2.Distance(from, to), 2f * scale), Texture2D.whiteTexture);
        }
        finally { GUI.matrix = matrix; }
    }

    private static void DrawGroundIcon(Texture2D icon, Rect rect)
    {
        // Preserve each icon's proportions, including tall bottles.
        var ratio = icon.width / (float)icon.height;
        if (ratio < 1f) { rect.x += rect.width * (1f - ratio) / 2f; rect.width *= ratio; }
        else { rect.y += rect.height * (1f - 1f / ratio) / 2f; rect.height /= ratio; }
        GUI.DrawTexture(rect, icon);
    }

    public bool IsShareableDefinition(int definitionId)
    {
        try { return IsShareable(InventoryManager.Instance?.GetItem((InventoryItemStringId)definitionId)); }
        catch (Exception exception)
        { ModLog.SuppressedException("inventory.shareable-definition", exception); return false; }
    }

    public bool CanAccept(int definitionId, int count, out string reason)
    {
        reason = ValidateRequest(definitionId, count);
        if (reason != null) return false;
        try
        {
            var manager = InventoryManager.Instance;
            var item = manager?.GetItem((InventoryItemStringId)definitionId);
            if (!IsShareable(item)) { reason = "That item cannot be shared."; return false; }

            var request = NewRequest(definitionId, count);
            if (manager.CanPutItemParts(request)) return true;
            reason = "Not enough room in the recipient's backpack.";
            return false;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("inventory.can-accept", exception);
            reason = "Inventory is not ready.";
            return false;
        }
    }

    public bool TryRemove(ushort uniqueId, int definitionId, int count, out string reason)
    {
        reason = ValidateRequest(definitionId, count);
        if (reason != null) return false;
        try
        {
            var manager = InventoryManager.Instance;
            if (manager == null || !manager.GetItemData(uniqueId, out var data))
            { reason = "Item no longer found."; return false; }
            if (data.itemId.value != definitionId || !IsShareable(data) || data.Count < count)
            { reason = "Not enough matching items in that stack."; return false; }

            var removed = manager.RemoveItem(data, count, allowLeftovers: false);
            if (removed == count) return true;
            if (removed > 0) TryAdd(definitionId, removed, out _);
            reason = "The game refused to remove the whole amount.";
            return false;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("inventory.remove-shareable", exception);
            reason = "Could not remove the item.";
            return false;
        }
    }

    public bool TryRemoveAny(int definitionId, int count, out string reason)
    {
        reason = ValidateRequest(definitionId, count);
        if (reason != null) return false;

        var candidates = new List<ShareableItem>();
        foreach (var item in GetShareableItems())
            if (item.DefinitionId == definitionId) candidates.Add(item);
        if (candidates.Count == 0 || candidates[0].Count < count)
        { reason = "You no longer have enough of that item."; return false; }

        // GetShareableItems aggregates identical stacks, so remove through Cairn's item
        // definition API. This preserves its own stack bookkeeping and notifications.
        try
        {
            var manager = InventoryManager.Instance;
            var item = manager?.GetItem((InventoryItemStringId)definitionId);
            if (!IsShareable(item)) { reason = "That item cannot be shared."; return false; }
            var removed = manager.BagInventoryData.Remove(item, count);
            if (removed == count) return true;
            if (removed > 0) TryAdd(definitionId, removed, out _);
            reason = "The game refused to remove the whole amount.";
            return false;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("inventory.remove-any-shareable", exception);
            reason = "Could not remove the item.";
            return false;
        }
    }

    public bool TryAdd(int definitionId, int count, out string reason)
    {
        if (!CanAccept(definitionId, count, out reason)) return false;
        try
        {
            var response = InventoryManager.Instance.PutItemParts(NewRequest(definitionId, count));
            if (response.CompleteSuccess) return true;

            // A check succeeded immediately before the mutation, so a partial result is
            // unexpected. Remove whatever was created to keep the transfer atomic locally.
            if (response.createdPartsCount > 0)
                TryRemoveAny(definitionId, response.createdPartsCount, out _);
            reason = "The item no longer fits in the backpack.";
            return false;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("inventory.add-shareable", exception);
            reason = "Could not store the item.";
            return false;
        }
    }

    private static InventoryStorageRequest NewRequest(int definitionId, int count)
        => new((InventoryItemStringId)definitionId, count,
            contentItemId: default, warmForSeconds: 0, lootTarget: LootTarget.Aava,
            bagMode: BagSearchMode.BottomUp, printErrors: false, displayToasts: false);

    private static bool IsShareable(InventoryItemData data)
        => data != null && data.containedItemId.value == 0 && IsShareable(data.InventoryItem);

    private static bool IsShareable(InventoryItem item)
        => item != null && item.Type == InventoryItemType.Consumable && !item.NeedsExtraData;

    private static string ValidateRequest(int definitionId, int count)
    {
        if (definitionId <= 0) return "Invalid item.";
        if (count <= 0 || count > MaxTransferCount)
            return $"Quantity must be between 1 and {MaxTransferCount}.";
        return null;
    }

    internal static string DisplayName(int definitionId)
    {
        var name = Enum.GetName(typeof(InventoryItemStringIdEnum), definitionId)
                   ?? $"item {definitionId}";
        if (name.StartsWith("ITEM_", StringComparison.Ordinal)) name = name.Substring(5);
        return name.Replace('_', ' ').ToLowerInvariant();
    }

    private void TickNativeActions()
    {
        if (_shareActions == null || !_shareActions.IsActive)
        { DestroyNativeActions(); return; }

        if (_section == null)
        {
            if (Time.frameCount - _lastSectionSearchFrame < 20) return;
            _lastSectionSearchFrame = Time.frameCount;
            // Several bag sections can coexist (normal inventory, cooking, etc.).
            // Bind to the visible one, not an arbitrary inactive section.
            foreach (var section in UnityEngine.Object.FindObjectsOfType<BagInventorySection>(true))
                if (section != null && section.isActiveAndEnabled
                    && section.CanvasGroup != null && section.CanvasGroup.alpha > .01f)
                { _section = section; break; }
            if (_section == null) return;
            CreateNativeActions();
        }

        if (_givePrompt == null || _dropPrompt == null)
        { DestroyNativeActions(); return; }

        var hasItem = TryGetSelectedShareableItem(out _);
        var inventoryOpen = IsInventoryInteractive();
        SetPromptVisible(_givePrompt, _giveInput,
            inventoryOpen && hasItem, SafeCan(_shareActions.CanGive));
        SetPromptVisible(_dropPrompt, _dropInput,
            inventoryOpen && hasItem, SafeCan(_shareActions.CanDrop));

        if (!_section.isActiveAndEnabled || _section.CanvasGroup.alpha <= .01f)
            DestroyNativeActions();
    }

    private bool IsInventoryInteractive()
        => _section != null && _section.isActiveAndEnabled
           && _section.CanvasGroup != null && _section.CanvasGroup.alpha > .01f
           && _section.InventoryUIActions.Get().enabled
           && _section.Allowed && !_section.IsBusy && !_section.IsMovingShape;

    private void CreateNativeActions()
    {
        try
        {
            var template = _section.sharedInputs?.UseItemInput;
            if (template == null) return;
            // InputActionReference.Create rejects standalone actions. Keep both actions
            // in our own asset; Cairn's resolver falls back to that asset's action when
            // its map is not present in the game's generated PlayerInputActions.
            _shareInputAsset = ScriptableObject.CreateInstance<InputActionAsset>();
            _shareInputAsset.name = "CairnMP inventory actions";
            var map = _shareInputAsset.AddActionMap("CairnMPInventory");
            _giveInput = map.AddAction("GiveNearest", InputActionType.Button, "<Keyboard>/g");
            _dropInput = map.AddAction("DropItem", InputActionType.Button, "<Keyboard>/x");
            _givePrompt = ClonePrompt(template, "CairnMP Give nearest", "Give nearest",
                _giveInput, out _giveDelegate, InvokeGive);
            _dropPrompt = ClonePrompt(template, "CairnMP Drop item", "Drop",
                _dropInput, out _dropDelegate, InvokeDrop);
            _creationErrorReported = false;
        }
        catch (Exception exception)
        {
            if (!_creationErrorReported)
                ModLog.Warning($"[Inventory] Could not create native share actions: {exception}");
            _creationErrorReported = true;
            DestroyNativeActions();
        }
    }

    private static InventoryInputPrompt ClonePrompt(InventoryInputPrompt template, string objectName,
        string label, InputAction action, out Il2CppSystem.Action callback,
        Action managedCallback)
    {
        var clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
        try
        {
            clone.name = objectName;
            clone.SetActive(false);
            clone.transform.SetAsLastSibling();
            var prompt = clone.GetComponent<InventoryInputPrompt>();
            var listener = prompt.inputPrompt;
            // Awake may already have subscribed the clone to the template's action.
            // Use the property to unsubscribe, then give the clone its own wrapper.
            listener.InputAction = null;
            listener.inputActionReferenceWrapper = new InputActionReferenceWrapper();
            listener.OnInputDetected = null;
            listener.OnInputReleased = null;
            listener.OnInputStarted = null;
            listener.OnInputActionChanged = null;
            listener.onInputDetected = new UnityEngine.Events.UnityEvent();
            listener.isInitialized = false;
            // Unlike setting wrapper.InputAction directly, this setter wires the native
            // started/performed/canceled callbacks. Initialize then builds the glyph.
            listener.InputAction = action;
            listener.Initialize();
            listener.ToggleHoldToConfirm(false);
            listener.Interactable = false;
            callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(managedCallback);
            listener.add_OnInputDetected(callback);

            if (prompt.text != null)
            {
                prompt.text.enabled = false;
                var text = prompt.text.gameObject.GetComponent<TMP_Text>();
                if (text != null) text.text = label;
            }
            return prompt;
        }
        catch
        {
            UnityEngine.Object.Destroy(clone);
            throw;
        }
    }

    private void InvokeGive()
    {
        if (_shareActions?.IsActive == true && IsInventoryInteractive()
            && SafeCan(_shareActions.CanGive) && TryGetSelectedShareableItem(out var item))
            _shareActions.Give(item);
    }

    private void InvokeDrop()
    {
        if (_shareActions?.IsActive == true && IsInventoryInteractive()
            && SafeCan(_shareActions.CanDrop) && TryGetSelectedShareableItem(out var item))
            _shareActions.Drop(item);
    }

    private static void SetPromptVisible(InventoryInputPrompt prompt, InputAction action,
        bool visible, bool available)
    {
        if (prompt.gameObject.activeSelf != visible) prompt.gameObject.SetActive(visible);
        if (visible && available) prompt.visual.Enable();
        else if (visible) prompt.visual.EnableButDisallow();
        else prompt.visual.Disable();
        prompt.inputPrompt.Interactable = visible && available;
        if (visible && available) action?.Enable(); else action?.Disable();
    }

    private static bool SafeCan(Func<bool> predicate)
    {
        try { return predicate?.Invoke() == true; }
        catch (Exception exception)
        { ModLog.SuppressedException("inventory.share-action-availability", exception); return false; }
    }

    private void DestroyNativeActions()
    {
        try { _giveInput?.Disable(); _dropInput?.Disable(); }
        catch { }
        // Detach native listeners before disposing the asset they reference.
        if (_givePrompt != null) _givePrompt.inputPrompt.InputAction = null;
        if (_dropPrompt != null) _dropPrompt.inputPrompt.InputAction = null;
        if (_givePrompt != null) UnityEngine.Object.Destroy(_givePrompt.gameObject);
        if (_dropPrompt != null) UnityEngine.Object.Destroy(_dropPrompt.gameObject);
        _givePrompt = null; _dropPrompt = null; _giveInput = null; _dropInput = null;
        _giveDelegate = null; _dropDelegate = null; _section = null;
        if (_shareInputAsset != null) UnityEngine.Object.Destroy(_shareInputAsset);
        _shareInputAsset = null;
    }

    private void CreateGroundVisual(GroundItem item)
    {
        try
        {
            var definition = InventoryManager.Instance?.GetItem((InventoryItemStringId)item.DefinitionId);
            var sprite = definition?.GetIcon(forBackpack: true);
            if (sprite == null) return;
            var icon = CopySpriteTexture(sprite);
            if (icon != null) _groundVisuals[item.Id] = new GroundVisual(icon);
        }
        catch (Exception exception)
        { ModLog.SuppressedException("inventory.ground-item-create", exception); }
    }

    private static Texture2D CopySpriteTexture(Sprite sprite)
    {
        var source = sprite.texture;
        var sourceRect = sprite.textureRect;
        var width = Math.Max(1, Mathf.RoundToInt(sourceRect.width));
        var height = Math.Max(1, Mathf.RoundToInt(sourceRect.height));
        var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        try
        {
            var scale = new Vector2(sourceRect.width / source.width,
                sourceRect.height / source.height);
            var offset = new Vector2(sourceRect.x / source.width, sourceRect.y / source.height);
            Graphics.Blit(source, target, scale, offset);
            RenderTexture.active = target;
            var icon = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = $"CairnMP item icon {sprite.name}",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            icon.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
            icon.Apply(false, true);
            return icon;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
        }
    }

    private void DestroyGroundVisual(uint id)
    {
        if (!_groundVisuals.Remove(id, out var visual)) return;
        if (visual.Icon != null) UnityEngine.Object.Destroy(visual.Icon);
    }

    private sealed class GroundVisual
    {
        internal GroundVisual(Texture2D icon) => Icon = icon;
        internal Texture2D Icon { get; }
    }

    private sealed class ShareActionsRegistration : IGameRegistration
    {
        private InventoryAdapter _owner;
        internal ShareActionsRegistration(InventoryAdapter owner, Func<bool> canGive,
            Action<ShareableItem> give, Func<bool> canDrop, Action<ShareableItem> drop)
        {
            _owner = owner;
            CanGive = canGive ?? throw new ArgumentNullException(nameof(canGive));
            Give = give ?? throw new ArgumentNullException(nameof(give));
            CanDrop = canDrop ?? throw new ArgumentNullException(nameof(canDrop));
            Drop = drop ?? throw new ArgumentNullException(nameof(drop));
        }

        public string Id => "inventory.share-actions";
        public bool IsActive => _owner != null;
        internal Func<bool> CanGive { get; }
        internal Action<ShareableItem> Give { get; }
        internal Func<bool> CanDrop { get; }
        internal Action<ShareableItem> Drop { get; }

        public void Dispose()
        {
            var owner = _owner;
            if (owner == null) return;
            _owner = null;
            if (ReferenceEquals(owner._shareActions, this)) owner._shareActions = null;
            owner.DestroyNativeActions();
        }
    }
}
