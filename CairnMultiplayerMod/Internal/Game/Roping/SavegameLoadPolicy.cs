using System;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// A rope-team save made while the climber was walking restores Climbot off the belay.
/// <c>RobotPawnController.GoToState</c> then asks <c>StartCoroutine</c> for a transition
/// that is not there. That <c>NullReferenceException</c> ("routine is null") leaves
/// <c>PawnControllerSwitcher.ReadFromSavegame</c>, the save iterator stops, and the
/// loading screen stays on the dots it has already shown. A save made while climbing
/// does not ask for that transition.
/// </summary>
internal static class SavegameLoadPolicy
{
    internal const string MissingCoroutineMessage = "routine is null";

    internal static bool IsMissingStateCoroutine(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is NullReferenceException
                && current.Message != null
                && current.Message.IndexOf(MissingCoroutineMessage, StringComparison.Ordinal) >= 0)
                return true;
        }

        return false;
    }
}
