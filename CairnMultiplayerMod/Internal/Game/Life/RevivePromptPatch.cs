using System;
using System.Reflection;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2CppTheGameBakers.Cairn.Netplay;

namespace CairnMultiplayerMod.Internal.Game.Life;

/// <summary>
/// Re-points Cairn's own "revive" interaction at our session.
///
/// The ghost prefab we spawn already carries a <see cref="NetplayRemotePlayerInteractionProvider"/>
/// offering three interactions — clip rope, unclip rope, revive — each a trio of closures
/// built in its Awake. The revive pair asks Cairn's matchmaking whether we share a room and
/// sends the request through its dead relay, so both are inert for us. Patching just those
/// two closures keeps the prompt, its collider, its localised label and its cooldown exactly
/// as the game authored them, and only changes who answers.
/// </summary>
internal static class RevivePromptPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.RevivePrompt");

    // Il2CppInterop spells the compiler-generated closures '<Awake>b__13_6' as '_Awake_b__13_6'.
    private const string CanReviveClosure = "_Awake_b__13_6";
    private const string ReviveClosure = "_Awake_b__13_7";

    private static Func<int, bool> _canRevive;
    private static Action<int> _onRevive;
    private static bool _installed;
    private static bool _closureReachedLogged;

    internal static bool IsInstalled => _installed;

    public static void Install()
    {
        if (_installed) return;
        try
        {
            Patch(CanReviveClosure, nameof(BeforeCanRevive));
            Patch(ReviveClosure, nameof(BeforeRevive));
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[Life] Revive prompt hook unavailable: " + ex.Message);
        }
    }

    private static void Patch(string closure, string replacement)
    {
        var method = AccessTools.Method(typeof(NetplayRemotePlayerInteractionProvider), closure)
                     ?? throw new MissingMethodException(
                         nameof(NetplayRemotePlayerInteractionProvider), closure);
        Harmony.Patch(method, prefix: new HarmonyMethod(typeof(RevivePromptPatch), replacement));
    }

    public static void Uninstall()
    {
        Clear();
        Harmony.UnpatchSelf();
        _installed = false;
    }

    internal static void Configure(Func<int, bool> canRevive, Action<int> onRevive)
    {
        _canRevive = canRevive ?? throw new ArgumentNullException(nameof(canRevive));
        _onRevive = onRevive ?? throw new ArgumentNullException(nameof(onRevive));
    }

    internal static void Clear()
    {
        _canRevive = null;
        _onRevive = null;
    }

    private static bool BeforeCanRevive(NetplayRemotePlayerInteractionProvider __instance, ref bool __result)
    {
        __result = false;
        var canRevive = _canRevive;
        if (canRevive == null) return false;

        try
        {
            LogClosureReachedOnce();
            if (__instance.pawnState != NetFrame.PawnStateType.Dead) return false;
            __result = canRevive(__instance.netPlayerId);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("life.can-revive", exception);
            __result = false;
        }
        return false;
    }

    /// <summary>
    /// Proof that the prompt is alive on our ghosts: the game only asks this closure while a
    /// provider of ours is being evaluated against the player's reach. Without it, a silent
    /// failure here would be indistinguishable from nobody standing near a body.
    /// </summary>
    private static void LogClosureReachedOnce()
    {
        if (_closureReachedLogged) return;
        _closureReachedLogged = true;
        ModLog.Debug("[Life] The game's revive prompt is being evaluated on our ghosts");
    }

    private static bool BeforeRevive(NetplayRemotePlayerInteractionProvider __instance)
    {
        var onRevive = _onRevive;
        if (onRevive == null) return false;

        try { onRevive(__instance.netPlayerId); }
        catch (Exception exception) { ModLog.SuppressedException("life.revive-interact", exception); }
        return false;
    }
}
