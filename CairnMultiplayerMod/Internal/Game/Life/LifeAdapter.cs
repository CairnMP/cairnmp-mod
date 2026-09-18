using System;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Internal.Game.Life;

/// <summary>
/// Exposes Cairn's death and revive mechanics to features. The two Harmony hooks it drives
/// are installed once at startup and stay dormant until a feature registers here.
/// </summary>
internal sealed class LifeAdapter : ILifeApi
{
    private DeathScreenHold _hold;
    private RevivePromptRegistration _prompt;

    public bool IsLocalPlayerDown => LifeInterop.IsLocalPlayerDead();

    public IGameRegistration HoldBackDeathScreen(Func<bool> keepHolding, Action onWentDown)
    {
        if (keepHolding == null) throw new ArgumentNullException(nameof(keepHolding));

        _hold?.Dispose();
        DeathScreenPatch.Hold(keepHolding, onWentDown);
        _hold = new DeathScreenHold(this);
        return _hold;
    }

    public void EndLocalPlayer()
    {
        if (!IsLocalPlayerDown) return;
        DeathScreenPatch.OpenDeathScreenNow();
    }

    public bool ReviveLocalPlayer(float healthRatio) => LifeInterop.Revive(healthRatio);

    public void ExhaustLocalPlayer(float amount) => LifeInterop.Exhaust(amount);

    public float FallenPartnerStaminaCost(float fallback)
        => LifeInterop.StaminaCostPerFallenPartner(fallback);

    public IGameRegistration AddRevivePrompt(Func<int, bool> canRevive, Action<int> onRevive)
    {
        if (canRevive == null) throw new ArgumentNullException(nameof(canRevive));
        if (onRevive == null) throw new ArgumentNullException(nameof(onRevive));

        _prompt?.Dispose();
        RevivePromptPatch.Configure(canRevive, onRevive);
        _prompt = new RevivePromptRegistration(this);
        return _prompt;
    }

    private sealed class DeathScreenHold : IGameRegistration
    {
        private LifeAdapter _owner;
        internal DeathScreenHold(LifeAdapter owner) => _owner = owner;

        public string Id => "life.death-screen-hold";
        public bool IsActive => _owner != null;

        public void Dispose()
        {
            var owner = _owner;
            if (owner == null) return;
            _owner = null;
            if (!ReferenceEquals(owner._hold, this)) return;
            owner._hold = null;
            DeathScreenPatch.Release();
        }
    }

    private sealed class RevivePromptRegistration : IGameRegistration
    {
        private LifeAdapter _owner;
        internal RevivePromptRegistration(LifeAdapter owner) => _owner = owner;

        public string Id => "life.revive-prompt";
        public bool IsActive => _owner != null;

        public void Dispose()
        {
            var owner = _owner;
            if (owner == null) return;
            _owner = null;
            if (!ReferenceEquals(owner._prompt, this)) return;
            owner._prompt = null;
            RevivePromptPatch.Clear();
        }
    }
}
