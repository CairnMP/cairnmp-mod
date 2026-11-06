using MelonLoader;

namespace CairnMultiplayerMod.Internal.Game;

internal static class ControllerPreferences
{
    internal static MelonPreferences_Entry<bool> RadialMenu;

    /// <summary>Radial menu (the default), or the original View/Share + button combos.</summary>
    internal static bool UseRadialMenu => RadialMenu?.Value ?? true;

    internal static void Register()
    {
        var category = MelonPreferences.CreateCategory("CairnMP.Controller");
        RadialMenu = category.CreateEntry("RadialMenu", true);
    }

    internal static void Save() => MelonPreferences.Save();
}
