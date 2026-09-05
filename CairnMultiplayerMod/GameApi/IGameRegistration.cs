using System;

namespace CairnMultiplayerMod.GameApi;

/// <summary>
/// Owns an operation registered through <see cref="IGameApi"/>. Disposing it removes the
/// operation; disposing it more than once is safe.
/// </summary>
internal interface IGameRegistration : IDisposable
{
    string Id { get; }
    bool IsActive { get; }
}
