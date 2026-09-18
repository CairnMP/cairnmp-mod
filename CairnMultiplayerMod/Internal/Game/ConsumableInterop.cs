using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTheGameBakers.Cairn;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// What a climber eats, and what that does to a rope team.
///
/// The game announces every use through <c>GameEventManager.OnItemUsed</c>, and a consumable
/// knows how to apply its own effects to whoever is playing. Those two together are the whole
/// mechanism: one climber drinks, the others get what the drink was worth. Cairn's shared-rope
/// protocol has the same idea under the name ConsumeItemForRopeMembers.
/// </summary>
internal static class ConsumableInterop
{
    private static Il2CppSystem.Action<InventoryItemStringIdEnum, InventoryItemStringIdEnum> _usedDelegate;
    private static Action<int> _onUsed;
    private static bool _subscribed, _failed;

    /// <summary>
    /// Guards against a shared ration echoing around the rope: applying the effects here must
    /// never look like a fresh use to the listener.
    /// </summary>
    private static bool _applyingShared;

    internal static void Listen(Action<int> onUsed)
    {
        _onUsed = onUsed;
        EnsureSubscribed();
    }

    internal static void StopListening() => _onUsed = null;

    private static void EnsureSubscribed()
    {
        if (_subscribed || _failed) return;
        try
        {
            _usedDelegate = DelegateSupport.ConvertDelegate<
                Il2CppSystem.Action<InventoryItemStringIdEnum, InventoryItemStringIdEnum>>(
                (Action<InventoryItemStringIdEnum, InventoryItemStringIdEnum>)OnNativeItemUsed);
            GameEventManager.OnItemUsed += _usedDelegate;
            _subscribed = true;
        }
        catch (Exception exception)
        {
            _failed = true;
            ModLog.Warning($"[Rations] Cannot observe item use: {exception.Message}");
        }
    }

    private static void OnNativeItemUsed(InventoryItemStringIdEnum container, InventoryItemStringIdEnum item)
    {
        if (_applyingShared) return;
        var handler = _onUsed;
        if (handler == null) return;
        try { handler((int)item); }
        catch (Exception exception) { ModLog.SuppressedException("rations.item-used", exception); }
    }

    /// <summary>
    /// Applies a consumable's effects to the local climber without touching their bag: the
    /// climber who opened the ration is the one who paid for it.
    /// </summary>
    internal static bool ApplySharedEffects(int definitionId)
    {
        if (definitionId <= 0) return false;
        try
        {
            var item = InventoryManager.Instance?.GetItem((InventoryItemStringId)definitionId);
            var consumable = item?.TryCast<ConsumableItem>();
            if (consumable == null) return false;

            _applyingShared = true;
            try { consumable.ApplyEffects(false); }
            finally { _applyingShared = false; }
            return true;
        }
        catch (Exception exception)
        {
            _applyingShared = false;
            ModLog.Warning($"[Rations] Could not share item {definitionId}: {exception.Message}");
            return false;
        }
    }
}
