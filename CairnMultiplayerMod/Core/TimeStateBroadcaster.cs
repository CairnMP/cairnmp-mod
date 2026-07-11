using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

public partial class Mod
{
    private float _timeStateTimer;
    private bool _hasLastSentSleepState;
    private bool _lastSentSleepState;

    // Heure du jour autoritaire gelee par l'hote quand il dort sans consensus.
    private float _heldDayTime01;
    private bool _hasHeldDayTime01;

    // Derniere heure recue de l'hote (cote client) a appliquer chaque frame.
    private ServerTimeState _remoteTimeState;
    private bool _hasRemoteTimeState;

    /// <summary>
    /// Synchronise l'heure du jour (NightDayCycle.dayTime01) entre joueurs et gere
    /// le fast-forward au bivouac : il ne se produit que quand TOUS dorment.
    ///
    /// Doit tourner AVANT la suspension bivouac (Mod.OnUpdate court-circuite tout
    /// le sync en bivouac, or c'est justement la qu'on dort). Tourne tant que le
    /// handshake est complet pour garder l'heure synchro meme en escalade.
    /// </summary>
    private void TickTimeSync()
    {
        if (_network == null || !_network.IsHandshakeComplete)
            return;

        if (LocalState == PlayerState.InGame)
            CairnGameApi.DumpTimeApi();

        ReportLocalSleepState();

        if (_lobby?.IsHost == true)
            TickHostTime();
        else
            TickClientTime();
    }

    /// <summary>Reporte l'etat de sommeil local a l'hote, uniquement sur changement.</summary>
    private void ReportLocalSleepState()
    {
        if (!CairnGameApi.TryIsLocalAsleep(out var asleep))
            return;

        if (_hasLastSentSleepState && _lastSentSleepState == asleep)
            return;

        _hasLastSentSleepState = true;
        _lastSentSleepState = asleep;
        _network.SendSleepState(asleep);
    }

    private void TickHostTime()
    {
        bool hostAsleep = CairnGameApi.TryIsLocalAsleep(out var a) && a;
        bool allAsleep = hostAsleep && AllRemoteInGameAsleep();

        float authoritative;
        if (hostAsleep && !allAsleep)
        {
            // L'hote dort sans consensus : on GELE le cycle a la derniere heure normale
            // captee avant le sommeil (sinon l'acceleration native du bivouac ferait
            // avancer le temps pour tout le monde). Le gel natif persiste, contrairement
            // a l'ancienne ecriture directe de dayTime01 qui etait recalculee chaque frame.
            if (!_hasHeldDayTime01 && CairnGameApi.TryGetDayTime01(out var cur))
            {
                _heldDayTime01 = cur;
                _hasHeldDayTime01 = true;
            }
            authoritative = _heldDayTime01;
            CairnGameApi.FreezeDayCycle(authoritative);
        }
        else
        {
            // Pas de gel : on libere notre gel et on suit l'heure naturelle (normale, ou
            // fast-forward quand tous dorment) en gardant la baseline a jour.
            CairnGameApi.UnfreezeDayCycle();
            if (CairnGameApi.TryGetDayTime01(out var cur))
            {
                _heldDayTime01 = cur;
                _hasHeldDayTime01 = true;
                authoritative = cur;
            }
            else
            {
                authoritative = _heldDayTime01;
            }
        }

        var interval = allAsleep
            ? Protocol.TimeStateFastForwardIntervalSeconds
            : Protocol.TimeStateUpdateIntervalSeconds;

        _timeStateTimer += Time.unscaledDeltaTime;
        if (_timeStateTimer >= interval)
        {
            _timeStateTimer = 0f;
            _network.SendTimeState(new ServerTimeState
            {
                DayTime01 = authoritative,
                AllAsleep = allAsleep,
            });
        }
    }

    private void TickClientTime()
    {
        // Gele le cycle jour/nuit sur l'heure de l'hote chaque frame : neutralise
        // l'acceleration d'un dormeur isole (son fast-forward local est ecrase) et
        // surtout cale le visuel jour/nuit sur l'hote — le gel natif PERSISTE, la ou
        // l'ancienne ecriture directe de dayTime01 etait recalculee chaque frame.
        if (_hasRemoteTimeState)
            CairnGameApi.FreezeDayCycle(_remoteTimeState.DayTime01);
    }

    /// <summary>
    /// Vrai si tous les joueurs distants InGame dorment. Les joueurs non InGame
    /// (chargement, menu) ne bloquent pas le consensus.
    /// </summary>
    private bool AllRemoteInGameAsleep()
    {
        foreach (var kv in _network.RemotePlayers)
        {
            var rp = kv.Value;
            if (rp == null || rp.State != PlayerState.InGame)
                continue;
            if (!rp.HasSleepState || !rp.IsAsleep)
                return false;
        }
        return true;
    }

    private void ResetTimeSyncState()
    {
        _timeStateTimer = 0f;
        _hasLastSentSleepState = false;
        _lastSentSleepState = false;
        _heldDayTime01 = 0f;
        _hasHeldDayTime01 = false;
        _hasRemoteTimeState = false;
        _remoteTimeState = default;
        CairnGameApi.ResetTimeSyncCache();
    }
}
