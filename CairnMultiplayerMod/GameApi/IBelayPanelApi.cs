using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.GameApi;

internal enum BelayPanelAction { Request, Offer, Accept, Decline, Cancel, ToggleBrake, Close, Toggle }
internal enum BelayPanelPhase { Partners, Waiting, Invitation, Active }
internal readonly record struct BelayPartnerRow(int Id, string Name, float Distance);

internal sealed class BelayPanelView
{
    internal BelayPanelPhase Phase { get; init; }
    internal string Description { get; init; } = "";
    internal string PartnerName { get; init; } = "";
    internal bool CanRequest { get; init; }
    internal bool CanOffer { get; init; }
    internal bool CanAccept { get; init; }
    internal bool LocalBelayer { get; init; }
    internal bool Braked { get; init; }
    internal IReadOnlyList<BelayPartnerRow> Partners { get; init; } = Array.Empty<BelayPartnerRow>();
}

/// <summary>Managed view model; native rendering/input capture stay behind the adapter.</summary>
internal interface IBelayPanelApi
{
    bool IsOpen { get; }
    IGameRegistration Register(Action<BelayPanelAction, int> onAction);
    void Show();
    void Close();
    void SetView(BelayPanelView view);
    void Draw();
}

internal sealed class UnavailableBelayPanelApi : IBelayPanelApi
{
    internal static readonly UnavailableBelayPanelApi Instance = new();
    public bool IsOpen => false;
    public IGameRegistration Register(Action<BelayPanelAction, int> onAction)
        => UnavailableGameApi.Instance.Chat.AddCommand("belay-panel", "", "", _ => { });
    public void Show() { }
    public void Close() { }
    public void SetView(BelayPanelView view) { }
    public void Draw() { }
}
