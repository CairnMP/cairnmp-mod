namespace CairnMultiplayerMod.Features;

internal sealed partial class ManualBelayFeature
{
    private bool _roleHudReady;

    private void RefreshRoleHud()
    {
        if (_active == null) { ClearRoleHud(); return; }
        if (!_roleHudReady)
        {
            if (_active.Climber == LocalPlayerId)
            {
                var status = Game.Belay.Status;
                _roleHudReady = status.Active && !status.Rebinding && status.Rope?.LengthApplied == true;
            }
            else
                _roleHudReady = _telemetry != null && !_telemetry.Rebinding && _telemetry.LengthApplied;
        }
        // Use a separate single-line message: the shared HUD gives each message
        // one row. Bilingual role names need no diagnostic-language preference.
        var role = !_roleHudReady ? "CONECTANDO / CONNECTING"
            : _active.Belayer == LocalPlayerId ? "SEGURADOR / BELAYER" : "ESCALADOR / CLIMBER";
        var partner = _active.Belayer == LocalPlayerId ? _active.Climber : _active.Belayer;
        var heading = _roleHudReady ? "VOCÊ / YOU: " + role : role;
        Game.Hud.ShowMessage("belay-role", heading + " | PARCEIRO / PARTNER: " + GetPlayerName(partner), 1.2f);
    }

    private void ClearRoleHud()
    {
        _roleHudReady = false;
        Game.Hud.HideMessage("belay-role");
    }
}
