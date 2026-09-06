using System;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Safe, scene-independent access to Cairn's main menu.</summary>
internal interface IMainMenuApi
{
    /// <summary>
    /// Adds an action to the main menu. Registration does not require the native menu to
    /// exist yet: the internal adapter attaches it when the scene is ready and restores it
    /// after every scene recreation.
    /// </summary>
    /// <exception cref="ArgumentException">The id or label is empty.</exception>
    /// <exception cref="InvalidOperationException">The id is already registered.</exception>
    IGameRegistration AddButton(string id, string label, Action onClick);
}
