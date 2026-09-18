using System;

namespace CairnMultiplayerMod.GameApi;

/// <summary>
/// The local climber's life: whether they are down, when the game is finally allowed to end,
/// and the prompt a teammate uses to bring them back. All of it is Cairn's own machinery —
/// the game already declines to show the death screen in a netplay room, and already knows
/// how to stand a climber back up where their body lies.
/// </summary>
internal interface ILifeApi
{
    /// <summary>True while Cairn considers the local climber dead.</summary>
    bool IsLocalPlayerDown { get; }

    /// <summary>
    /// Holds the death screen back for as long as <paramref name="keepHolding"/> answers
    /// true. <paramref name="onWentDown"/> fires at the exact moment the game decided the
    /// climber died, which is when the countdown starts.
    /// </summary>
    IGameRegistration HoldBackDeathScreen(Func<bool> keepHolding, Action onWentDown);

    /// <summary>
    /// Lets the death the game already decided on play out: the death screen opens and the
    /// run ends as it normally would. Does nothing if the climber is not down.
    /// </summary>
    void EndLocalPlayer();

    /// <summary>
    /// Stands the local climber back up where their body lies, with
    /// <paramref name="healthRatio"/> of their maximum health (0-1). Survival stats are
    /// reset by the game itself; no scene reload, no save is involved.
    /// </summary>
    bool ReviveLocalPlayer(float healthRatio);

    /// <summary>
    /// Puts the game's own "revive" prompt back on downed teammates' ghosts. Cairn ships the
    /// prompt on the ghost prefab but wires it to its dead matchmaking service; this points
    /// it at our session instead. <paramref name="canRevive"/> is asked per player id.
    /// </summary>
    IGameRegistration AddRevivePrompt(Func<int, bool> canRevive, Action<int> onRevive);

    /// <summary>
    /// Tires the local climber out by <paramref name="amount"/>. The game stops the drain
    /// before a critical state, so this costs endurance without being lethal on its own.
    /// </summary>
    void ExhaustLocalPlayer(float amount);

    /// <summary>The endurance per second a fallen rope member costs, as the game balances it.</summary>
    float FallenPartnerStaminaCost(float fallback);
}

/// <summary>
/// Null implementation for a mod that runs without the game's death hooks (framework-only
/// tests, or a game build where the hooks could not be installed). Nothing is held back and
/// nobody can be revived, which is exactly the vanilla behaviour.
/// </summary>
internal sealed class UnavailableLifeApi : ILifeApi
{
    internal static readonly UnavailableLifeApi Instance = new();

    private UnavailableLifeApi() { }

    public bool IsLocalPlayerDown => false;

    public IGameRegistration HoldBackDeathScreen(Func<bool> keepHolding, Action onWentDown)
    {
        if (keepHolding == null) throw new ArgumentNullException(nameof(keepHolding));
        return new InactiveRegistration("life.death-screen-hold");
    }

    public void EndLocalPlayer() { }

    public bool ReviveLocalPlayer(float healthRatio) => false;

    public void ExhaustLocalPlayer(float amount) { }

    public float FallenPartnerStaminaCost(float fallback) => fallback;

    public IGameRegistration AddRevivePrompt(Func<int, bool> canRevive, Action<int> onRevive)
    {
        if (canRevive == null) throw new ArgumentNullException(nameof(canRevive));
        if (onRevive == null) throw new ArgumentNullException(nameof(onRevive));
        return new InactiveRegistration("life.revive-prompt");
    }

    private sealed class InactiveRegistration : IGameRegistration
    {
        internal InactiveRegistration(string id) => Id = id;
        public string Id { get; }
        public bool IsActive => false;
        public void Dispose() { }
    }
}
