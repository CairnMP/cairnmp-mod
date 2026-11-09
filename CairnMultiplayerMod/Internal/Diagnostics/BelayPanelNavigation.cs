using System.Collections.Generic;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Internal.Diagnostics;

internal enum BelayPanelShortcut { Previous, Next, Offer, Request, Accept, Decline }

/// <summary>Keep keyboard selection bound to an identity across refreshed distance ordering.</summary>
internal sealed class BelayPanelNavigation
{
    private readonly Dictionary<BelayPanelShortcut, BelayPanelKeyGate> _keys = new();
    internal int SelectedPartner { get; private set; } = -1;

    internal void Update(BelayPanelView view)
    {
        if (view.Phase != BelayPanelPhase.Partners) return;
        foreach (var partner in view.Partners) if (partner.Id == SelectedPartner) return;
        SelectedPartner = view.Partners.Count > 0 ? view.Partners[0].Id : -1;
    }

    internal void Select(int id) => SelectedPartner = id;

    internal bool Observe(BelayPanelShortcut key, bool down, bool eligible, BelayPanelView view,
        out BelayPanelAction action, out int target)
    {
        action = default; target = -1;
        if (!_keys.TryGetValue(key, out var gate)) _keys[key] = gate = new BelayPanelKeyGate();
        if (!gate.Observe(down, eligible)) return false;
        Update(view);
        if (key is BelayPanelShortcut.Previous or BelayPanelShortcut.Next)
        {
            if (view.Phase != BelayPanelPhase.Partners || view.Partners.Count == 0) return false;
            var index = 0;
            for (var i = 0; i < view.Partners.Count; i++) if (view.Partners[i].Id == SelectedPartner) index = i;
            index = (index + (key == BelayPanelShortcut.Next ? 1 : view.Partners.Count - 1)) % view.Partners.Count;
            SelectedPartner = view.Partners[index].Id;
            return false;
        }
        if (key == BelayPanelShortcut.Accept && view.Phase == BelayPanelPhase.Invitation && view.CanAccept)
        { action = BelayPanelAction.Accept; return true; }
        if (key == BelayPanelShortcut.Accept && view.Phase is BelayPanelPhase.SwapInvitation or BelayPanelPhase.SwapRequest && view.CanAccept)
        { action = BelayPanelAction.AcceptSwap; return true; }
        if (key == BelayPanelShortcut.Decline)
        {
            if (view.Phase is BelayPanelPhase.SwapInvitation or BelayPanelPhase.SwapPreparing or BelayPanelPhase.SwapRequest)
            { action = BelayPanelAction.CancelSwap; return true; }
            if (view.Phase == BelayPanelPhase.Invitation) { action = BelayPanelAction.Decline; return true; }
            if (view.Phase == BelayPanelPhase.Waiting) { action = BelayPanelAction.Cancel; return true; }
            if (view.Phase == BelayPanelPhase.Recovering) { action = BelayPanelAction.Cancel; return true; }
            return false; // Declining an invitation must never end an active belay.
        }
        if (view.Phase != BelayPanelPhase.Partners) return false;
        foreach (var partner in view.Partners)
        {
            if (partner.Id != SelectedPartner) continue;
            if (key == BelayPanelShortcut.Offer && view.CanOffer && partner.CanOffer) action = BelayPanelAction.Offer;
            else if (key == BelayPanelShortcut.Request && view.CanRequest && partner.CanRequest) action = BelayPanelAction.Request;
            else return false;
            target = partner.Id;
            return true;
        }
        return false;
    }
}
