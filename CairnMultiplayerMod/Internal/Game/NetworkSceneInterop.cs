using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>Belay shares a native world, not the last asynchronously loaded zone.</summary>
internal static class NetworkSceneInterop
{
    private static string _lastBelayWorld;

    internal static string Resolve(RuntimeState state)
    {
        if (!state.ModeRules.UsesPartnerBelay || state.LocalPlayerState != PlayerState.InGame)
            return SceneRoles.ResolveNetworkScene(state.CurrentScene, state.LastGameplayScene);

        try
        {
            var world = MoSingleton<StreamingManager>.Instance?.World;
            var name = world?.WorldId.ToString();
            if (name == "Invalid" || string.IsNullOrEmpty(name)) return "";
            var scene = SceneRoles.ResolveBelayWorld(state.CurrentScene, state.LastGameplayScene, name);
            if (scene != _lastBelayWorld)
            {
                _lastBelayWorld = scene;
                ModLog.Info($"[BelayPartner] Native world context: {scene}; loaded={state.CurrentScene}; last={state.LastGameplayScene}.");
            }
            return scene;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("belay.read-world-context", exception);
            return "";
        }
    }
}
