using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Networking;
using CairnMultiplayerMod.Internal.UI.Sketch;
using CairnMultiplayerMod.Internal.UI.Toolkit;

namespace CairnMultiplayerMod.Internal.UI;

internal static class MultiplayerPanelFactory
{
    public static IMultiplayerPanel Create(SteamLobbyManager lobby)
    {
        if (lobby == null) throw new ArgumentNullException(nameof(lobby));
        try
        {
            var panel = new SketchMultiplayerPanel(lobby);
            ModLog.Info("[CairnMP] Sketch (native-style) multiplayer panel enabled with UI Toolkit fallback.");
            return new ResilientMultiplayerPanel(panel, () => new UiToolkitMultiplayerPanel(lobby));
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[CairnMP] Sketch panel unavailable, using UI Toolkit: {ex.Message}");
            return new UiToolkitMultiplayerPanel(lobby);
        }
    }
}
