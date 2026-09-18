using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;

namespace CairnMultiplayerMod.Internal.Game.Life;

/// <summary>
/// Cairn's own death and revive plumbing, driven directly.
///
/// The game already separates "the climber died" from "the run is over": in a netplay room
/// it skips the death screen entirely and leaves the body lying where it fell, and
/// <c>PawnManager.Respawn(Revive)</c> resets the survival stats and hands control back on
/// the spot — no scene reload, no savegame. That is the whole feature; we only have to
/// reach it from our own session.
/// </summary>
internal static class LifeInterop
{
    /// <summary>A revived climber is never left on a sliver of health that kills them again.</summary>
    private const float MinimumReviveHealth = 1f;

    internal static bool IsLocalPlayerDead()
    {
        try
        {
            var data = GameDataManager.Instance;
            return data != null && data.IsDead;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("life.read-is-dead", exception);
            return false;
        }
    }

    /// <summary>
    /// Tires the local climber out, the way carrying weight does.
    ///
    /// The game's own flag is what keeps this fair: <c>onlyAboveWarning</c> stops the drain
    /// before it pushes anyone into a critical state, so hauling a fallen partner costs
    /// endurance without quietly killing the one doing the hauling.
    /// </summary>
    internal static void Exhaust(float amount)
    {
        if (amount <= 0f) return;
        try
        {
            GameDataManager.Instance?.Exhaust(amount, true);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("life.exhaust", exception);
        }
    }

    /// <summary>
    /// The endurance a fallen rope member costs per second, as the game itself sets it in its
    /// netplay tweakables. Falls back to a mild value when the table is not loaded, rather
    /// than inventing a number that contradicts the game's balance.
    /// </summary>
    internal static float StaminaCostPerFallenPartner(float fallback)
    {
        try
        {
            if (!TweakableBase<NetplayTweakables>.IsReady) return fallback;
            var settings = TweakableBase<NetplayTweakables>.Instance?.SharedRopeSettings;
            var cost = settings?.staminaMalusPerCorpse ?? 0f;
            return cost > 0f ? cost : fallback;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("life.read-corpse-malus", exception);
            return fallback;
        }
    }

    /// <summary>
    /// True once the game considers the climb finished at the top. Both flags matter: the
    /// summit sequence raises one, and the point of no return raises the other, and a race
    /// is over at whichever comes first.
    /// </summary>
    internal static bool HasReachedSummit()
    {
        try
        {
            var data = GameDataManager.Instance;
            return data != null && (data.InSummitEnd || data.IsGameFinishedNoGoingBack);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("life.read-summit", exception);
            return false;
        }
    }

    /// <summary>
    /// Stands the local climber back up where they fell. Respawn resets every survival stat,
    /// so the health penalty has to be applied afterwards, not before.
    /// </summary>
    internal static bool Revive(float healthRatio)
    {
        try
        {
            var pawns = PawnManager.Instance;
            if (pawns == null)
            {
                ModLog.Warning("[Life] Revive skipped: the game has no PawnManager right now.");
                return false;
            }

            pawns.Respawn(PawnManager.RespawnMode.Revive, null);
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[Life] Revive failed: {exception.Message}");
            return false;
        }

        ApplyReviveHealth(healthRatio);
        return true;
    }

    private static void ApplyReviveHealth(float healthRatio)
    {
        if (healthRatio >= 1f) return;
        try
        {
            var data = GameDataManager.Instance;
            if (data == null) return;
            var target = Math.Max(MinimumReviveHealth, data.CurrentMaxHp * Math.Max(0f, healthRatio));
            data.SetCurrentHp(target);
        }
        catch (Exception exception)
        {
            // The climber is already back on their feet; a full-health revive is a far
            // better outcome than an exception escaping into the caller's frame.
            ModLog.SuppressedException("life.apply-revive-health", exception);
        }
    }
}
