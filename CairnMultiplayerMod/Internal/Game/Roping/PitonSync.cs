using System;
using System.Collections.Generic;
using System.Numerics;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>A piton as the local Lifeline holds it this frame.</summary>
internal readonly struct PlacedPiton
{
    internal PlacedPiton(IntPtr pointer, Vector3 position)
    {
        Pointer = pointer;
        Position = position;
    }

    internal IntPtr Pointer { get; }
    internal Vector3 Position { get; }
}

/// <summary>What the native side has to carry out after a reconcile.</summary>
internal sealed class PitonActions
{
    /// <summary>New pitons of ours, to announce under a fresh local id.</summary>
    internal readonly List<(uint LocalId, IntPtr Pointer)> Announce = new();

    /// <summary>Pitons of ours that left the Lifeline: the host withdraws them for everyone.</summary>
    internal readonly List<uint> Removed = new();

    /// <summary>Someone else's piton that this climber took up, by the host's id.</summary>
    internal readonly List<uint> Recovered = new();

    /// <summary>Pitons the host knows of that this Lifeline is still missing.</summary>
    internal readonly List<ServerPitonPlaced> Spawn = new();

    /// <summary>Pitons to take off this Lifeline. Every pointer here is in it this frame.</summary>
    internal readonly List<IntPtr> Detach = new();

    internal bool IsEmpty => Announce.Count == 0 && Removed.Count == 0 && Recovered.Count == 0
                             && Spawn.Count == 0 && Detach.Count == 0;

    internal void Clear()
    {
        Announce.Clear();
        Removed.Clear();
        Recovered.Clear();
        Spawn.Clear();
        Detach.Clear();
    }
}

/// <summary>
/// Keeps the local Lifeline and the session's pitons in agreement, without touching either.
///
/// Every climber's world holds everyone's pitons: their own, which they announce, and the
/// others', which the host tells them about. The native side reports what the Lifeline holds
/// each frame; this decides what to announce, withdraw, create or take off, and the native
/// side carries it out. Two rules keep it safe: a pointer is only ever handed back while the
/// Lifeline still holds it, and a piton is told apart from another by where it sits once its
/// pointer can no longer be trusted — after the Lifeline was rebuilt by a reload.
/// </summary>
internal sealed class PitonSync
{
    /// <summary>Saved pitons come back in their exact place; anything further is another piton.</summary>
    internal const float SamePlaceMeters = 0.05f;

    /// <summary>A piton is only taken up by a climber standing next to it.</summary>
    internal const float RecoverReachMeters = 5f;

    /// <summary>
    /// A new Lifeline fills up from the save over a few frames, and the host's list may still
    /// be on its way. Until this has passed nothing is announced, withdrawn or created: every
    /// piton first gets the chance to be recognised.
    /// </summary>
    internal const double SettleSeconds = 3;

    /// <summary>A disappearance is only put down to this climber if it happened under their
    /// eyes, between two frames of play — not across a loading screen or a camp.</summary>
    internal const double AttributableGapSeconds = 1;

    internal const int MaxSpawnsPerReconcile = 8;
    internal const double SpawnRetrySeconds = 5;
    internal const int MaxRemotePitons = 1024;

    private enum RemoteState { Pending, Spawning, Live, Abandoned, Withdrawn }

    private sealed class Remote
    {
        internal ServerPitonPlaced Placed;
        internal RemoteState State;
        internal IntPtr Pointer;
        internal double RetryAt;
        internal Vector3 Position => new(Placed.PosX, Placed.PosY, Placed.PosZ);
    }

    private readonly Dictionary<IntPtr, uint> _ownByPointer = new();
    private readonly Dictionary<uint, Vector3> _ownPositions = new();
    private readonly HashSet<uint> _ownUnbound = new();
    private readonly HashSet<uint> _ownTakenByOthers = new();
    private readonly Dictionary<uint, Remote> _remote = new();
    private readonly Dictionary<IntPtr, uint> _remoteByPointer = new();
    private readonly HashSet<IntPtr> _foreign = new();
    private readonly PitonActions _actions = new();
    private readonly HashSet<IntPtr> _current = new();
    private readonly List<IntPtr> _gone = new();
    private readonly List<uint> _drop = new();
    private uint _nextLocalId = 1;
    private IntPtr _lifeline;
    private bool _settling, _afterReload;
    private double _settleUntil, _lastReconcileAt = double.NegativeInfinity;

    internal int OwnCount => _ownByPointer.Count;
    internal int RemoteCount => _remote.Count;

    /// <summary>
    /// The Lifeline just resolved. The same one as before keeps everything; a different one
    /// means the old one is gone with every pointer into it.
    /// </summary>
    internal void OnLifeline(IntPtr lifeline, double now)
    {
        if (lifeline == IntPtr.Zero || lifeline == _lifeline) return;

        var reload = _lifeline != IntPtr.Zero || _ownPositions.Count > 0;
        _lifeline = lifeline;

        foreach (var id in _ownByPointer.Values) _ownUnbound.Add(id);
        _ownByPointer.Clear();
        // Taken up by someone else before the reload: their saved copy must not come back.
        foreach (var id in _ownTakenByOthers)
        {
            _ownUnbound.Remove(id);
            _ownPositions.Remove(id);
        }
        _ownTakenByOthers.Clear();

        _drop.Clear();
        foreach (var pair in _remote)
        {
            var remote = pair.Value;
            remote.Pointer = IntPtr.Zero;
            if (remote.State == RemoteState.Withdrawn) _drop.Add(pair.Key);
            else if (remote.State is RemoteState.Live or RemoteState.Spawning) remote.State = RemoteState.Pending;
        }
        foreach (var id in _drop) _remote.Remove(id);
        _remoteByPointer.Clear();
        _foreign.Clear();

        _settling = true;
        _afterReload = reload;
        _settleUntil = now + SettleSeconds;
    }

    /// <summary>The host says this piton exists. Nothing native happens until a reconcile.</summary>
    internal void Know(ServerPitonPlaced placed)
    {
        if (_remote.TryGetValue(placed.PitonId, out var known))
        {
            known.Placed = placed;
            return;
        }
        if (_remote.Count >= MaxRemotePitons) return;
        _remote[placed.PitonId] = new Remote { Placed = placed, State = RemoteState.Pending };
    }

    /// <summary>The host says this piton is gone.</summary>
    internal void Forget(uint pitonId)
    {
        if (!_remote.TryGetValue(pitonId, out var remote)) return;
        switch (remote.State)
        {
            case RemoteState.Live:
            case RemoteState.Spawning:
                // Still on this Lifeline, or about to be: take it off once it can be found.
                remote.State = RemoteState.Withdrawn;
                break;
            case RemoteState.Withdrawn:
                break;
            default:
                _remote.Remove(pitonId);
                break;
        }
    }

    /// <summary>Another climber took up one of ours. It leaves this Lifeline without being
    /// announced as removed: the host has already withdrawn it.</summary>
    internal void TakenByOther(uint localId)
    {
        if (_ownUnbound.Remove(localId))
        {
            _ownPositions.Remove(localId);
            return;
        }
        if (_ownPositions.ContainsKey(localId)) _ownTakenByOthers.Add(localId);
    }

    internal PitonActions Reconcile(IReadOnlyList<PlacedPiton> placed, Vector3? localPosition, double now)
    {
        _actions.Clear();
        _current.Clear();
        foreach (var piton in placed)
            if (piton.Pointer != IntPtr.Zero) _current.Add(piton.Pointer);

        var attributable = now - _lastReconcileAt <= AttributableGapSeconds;
        _lastReconcileAt = now;

        NoticeDisappearances(localPosition, attributable);
        RecogniseNewcomers(placed, now);
        QueueSpawns(now);
        QueueDetaches();
        return _actions;
    }

    private void NoticeDisappearances(Vector3? localPosition, bool attributable)
    {
        _gone.Clear();
        foreach (var pointer in _ownByPointer.Keys)
            if (!_current.Contains(pointer)) _gone.Add(pointer);
        foreach (var pointer in _gone)
        {
            var id = _ownByPointer[pointer];
            _ownByPointer.Remove(pointer);
            _ownPositions.Remove(id);
            if (!_ownTakenByOthers.Remove(id)) _actions.Removed.Add(id);
        }

        _gone.Clear();
        foreach (var pointer in _remoteByPointer.Keys)
            if (!_current.Contains(pointer)) _gone.Add(pointer);
        foreach (var pointer in _gone)
        {
            var id = _remoteByPointer[pointer];
            _remoteByPointer.Remove(pointer);
            if (!_remote.TryGetValue(id, out var remote)) continue;

            if (remote.State == RemoteState.Withdrawn)
            {
                _remote.Remove(id);
            }
            else if (attributable && localPosition.HasValue
                     && Vector3.DistanceSquared(localPosition.Value, remote.Position)
                        <= RecoverReachMeters * RecoverReachMeters)
            {
                // Taken up by this climber: the piton is in their bag now, so it has to leave
                // everyone else's wall — otherwise its owner can take it up a second time.
                _actions.Recovered.Add(id);
                _remote.Remove(id);
            }
            else
            {
                // Gone for a reason that is not ours to announce. Never recreated, and never
                // touched through a pointer the Lifeline no longer holds.
                remote.State = RemoteState.Abandoned;
                remote.Pointer = IntPtr.Zero;
            }
        }

        _foreign.RemoveWhere(pointer => !_current.Contains(pointer));
    }

    private void RecogniseNewcomers(IReadOnlyList<PlacedPiton> placed, double now)
    {
        var settling = _settling && now < _settleUntil;
        foreach (var piton in placed)
        {
            var pointer = piton.Pointer;
            if (pointer == IntPtr.Zero || _ownByPointer.ContainsKey(pointer)
                || _remoteByPointer.ContainsKey(pointer) || _foreign.Contains(pointer))
                continue;

            if (TryRebindOwn(piton) || TryClaimRemote(piton)) continue;
            if (settling) continue;

            if (_settling && _afterReload)
            {
                // Still unexplained once the reload settled: a saved copy of a piton that no
                // longer exists. Announcing it would hand everyone a duplicate.
                _foreign.Add(pointer);
                continue;
            }

            var id = _nextLocalId++;
            _ownByPointer[pointer] = id;
            _ownPositions[id] = piton.Position;
            _actions.Announce.Add((id, pointer));
        }

        if (!_settling || settling) return;
        _settling = false;
        // Ours before the reload, and not back after it: gone for good.
        foreach (var id in _ownUnbound)
        {
            _ownPositions.Remove(id);
            _actions.Removed.Add(id);
        }
        _ownUnbound.Clear();
    }

    private bool TryRebindOwn(PlacedPiton piton)
    {
        foreach (var id in _ownUnbound)
        {
            if (!SamePlace(_ownPositions[id], piton.Position)) continue;
            _ownUnbound.Remove(id);
            _ownByPointer[piton.Pointer] = id;
            return true;
        }
        return false;
    }

    private bool TryClaimRemote(PlacedPiton piton)
    {
        foreach (var pair in _remote)
        {
            var remote = pair.Value;
            if (remote.Pointer != IntPtr.Zero || remote.State == RemoteState.Live) continue;
            if (!SamePlace(remote.Position, piton.Position)) continue;
            remote.Pointer = piton.Pointer;
            if (remote.State != RemoteState.Withdrawn) remote.State = RemoteState.Live;
            _remoteByPointer[piton.Pointer] = pair.Key;
            return true;
        }
        return false;
    }

    private void QueueSpawns(double now)
    {
        if (_settling) return;
        foreach (var remote in _remote.Values)
        {
            if (_actions.Spawn.Count >= MaxSpawnsPerReconcile) break;
            if (remote.State != RemoteState.Pending || now < remote.RetryAt) continue;
            remote.State = RemoteState.Spawning;
            _actions.Spawn.Add(remote.Placed);
        }
    }

    private void QueueDetaches()
    {
        foreach (var remote in _remote.Values)
            if (remote.State == RemoteState.Withdrawn && _current.Contains(remote.Pointer))
                _actions.Detach.Add(remote.Pointer);

        foreach (var pair in _ownByPointer)
            if (_ownTakenByOthers.Contains(pair.Value) && _current.Contains(pair.Key))
                _actions.Detach.Add(pair.Key);
    }

    /// <summary>The piton the native side just created for <paramref name="pitonId"/>, if it
    /// could tell which one it was. If not, the next reconcile finds it by its place.</summary>
    internal void Spawned(uint pitonId, IntPtr pointer)
    {
        if (!_remote.TryGetValue(pitonId, out var remote) || pointer == IntPtr.Zero) return;
        remote.Pointer = pointer;
        if (remote.State == RemoteState.Spawning) remote.State = RemoteState.Live;
        _remoteByPointer[pointer] = pitonId;
    }

    /// <summary>The native side made the piton but also something else it cannot place.</summary>
    internal void Ignore(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero) _foreign.Add(pointer);
    }

    internal void SpawnFailed(uint pitonId, double now)
    {
        if (!_remote.TryGetValue(pitonId, out var remote) || remote.Pointer != IntPtr.Zero) return;
        if (remote.State == RemoteState.Withdrawn)
        {
            _remote.Remove(pitonId);
            return;
        }
        if (remote.State != RemoteState.Spawning) return;
        remote.State = RemoteState.Pending;
        remote.RetryAt = now + SpawnRetrySeconds;
    }

    /// <summary>True for a pointer already accounted for, whose position need not be read.</summary>
    internal bool Tracks(IntPtr pointer)
        => _ownByPointer.ContainsKey(pointer) || _remoteByPointer.ContainsKey(pointer) || _foreign.Contains(pointer);

    /// <summary>Pointers of other climbers' pitons on this Lifeline, for leaving a session.</summary>
    internal IEnumerable<IntPtr> RemotePointers => _remoteByPointer.Keys;

    internal void ForgetSession()
    {
        _ownByPointer.Clear();
        _ownPositions.Clear();
        _ownUnbound.Clear();
        _ownTakenByOthers.Clear();
        _remote.Clear();
        _remoteByPointer.Clear();
        _foreign.Clear();
        _lifeline = IntPtr.Zero;
        _settling = false;
        _afterReload = false;
        _lastReconcileAt = double.NegativeInfinity;
        // Local ids keep counting: the host may still hold one of ours from before.
    }

    private static bool SamePlace(Vector3 a, Vector3 b)
        => Vector3.DistanceSquared(a, b) <= SamePlaceMeters * SamePlaceMeters;
}
