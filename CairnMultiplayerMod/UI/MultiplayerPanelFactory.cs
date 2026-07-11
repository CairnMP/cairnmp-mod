using System;
using CairnMultiplayerMod.Core;
using CairnMultiplayerMod.UI.Sketch;
using CairnMultiplayerMod.UI.Toolkit;

namespace CairnMultiplayerMod.UI;

public static class MultiplayerPanelFactory
{
    public static IMultiplayerPanel Create()
    {
        try
        {
            var panel = new SketchMultiplayerPanel();
            Mod.Log.Msg("[CairnMP] Sketch (native-style) multiplayer panel enabled with UI Toolkit fallback.");
            return new ResilientMultiplayerPanel(panel, () => new UiToolkitMultiplayerPanel());
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnMP] Sketch panel unavailable, using UI Toolkit: {ex.Message}");
            return new UiToolkitMultiplayerPanel();
        }
    }
}
