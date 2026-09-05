using System;
using System.Collections.Generic;
using CairnMultiplayer.Api;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Extensions;
using CairnMultiplayerMod.Internal.Networking;

namespace CairnMultiplayerMod.Framework;

/// <summary>
/// Runs the features. This is the only piece of the framework the mod core talks to: it
/// registers every feature at startup, ticks them in the right phase, and forwards session
/// and scene changes. Adding a feature therefore never touches Mod.
///
/// Features are registered under one privileged extension shared by the whole mod, so they
/// travel over the same authoritative machinery as third-party integrations without paying
/// the safeguards meant for untrusted code.
/// </summary>
internal sealed class FeatureHost : IDisposable
{
    /// <summary>Reserved id of the extension carrying every built-in feature.</summary>
    internal const string CoreExtensionId = "cairnmp.core";

    private readonly ExtensionRuntime _runtime;
    private readonly IGameApi _game;
    private readonly Func<NetworkManager> _networkProvider;
    private readonly FeatureStreamRouter _streams = new();
    private readonly List<Registered> _features = new();

    /// <summary>Uses the mod-wide runtime by default; tests pass their own.</summary>
    internal FeatureHost(
        ExtensionRuntime runtime = null,
        IGameApi game = null,
        Func<NetworkManager> networkProvider = null)
    {
        _runtime = runtime ?? MultiplayerApi.Runtime;
        _game = game ?? UnavailableGameApi.Instance;
        _networkProvider = networkProvider ?? (() => null);
    }

    /// <summary>Routes incoming real-time payloads to the features that declared them.</summary>
    internal void DispatchStream(int fromPlayerId, ushort channel, byte[] payload)
        => _streams.Dispatch(fromPlayerId, channel, payload);

    private sealed class Registered
    {
        public MultiplayerFeature Feature;
        public FeatureBuilder Builder;
    }

    /// <summary>
    /// Registers the given features, in a stable order (by id) so behaviour never depends on
    /// the order the generator happened to emit. Must run before joining or hosting.
    /// </summary>
    internal void RegisterAll(IEnumerable<MultiplayerFeature> features, Version modVersion)
    {
        var extension = _runtime.RegisterPrivileged(
            new ExtensionRegistration(CoreExtensionId, modVersion));

        var ordered = new List<MultiplayerFeature>(features ?? Array.Empty<MultiplayerFeature>());
        ordered.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in ordered)
        {
            if (feature == null) continue;
            if (!seen.Add(feature.Id))
                throw new InvalidOperationException(
                    $"Two features share the id '{feature.Id}'. Ids must be unique — they name the messages on the wire.");

            var builder = new FeatureBuilder(_runtime, extension, _streams,
                _networkProvider, _game, feature.Id);
            feature.Session = _runtime;
            feature.BindGame(builder.Game);
            try
            {
                feature.OnRegister(builder);
            }
            catch (Exception ex)
            {
                builder.Dispose();
                // A declaration error is a programming mistake: fail loudly at startup rather
                // than run a mod where one feature is silently absent.
                throw new InvalidOperationException(
                    $"Feature '{feature.Id}' failed to register: {ex.Message}", ex);
            }

            _features.Add(new Registered { Feature = feature, Builder = builder });
        }

        FeatureLog.Info($"[Features] {_features.Count} feature(s) registered.");
    }

    /// <summary>Runs the per-frame work declared for <paramref name="phase"/>.</summary>
    internal void Tick(FeaturePhase phase)
    {
        foreach (var registered in _features)
        {
            var ticks = registered.Builder.Ticks;
            for (int i = 0; i < ticks.Count; i++)
            {
                if (ticks[i].Phase != phase) continue;
                Run(registered.Feature.Id, "tick", ticks[i].Tick);
            }
        }
    }

    internal void NotifySessionStarted() => Dispatch(builder => builder.SessionStartedHandlers, "session-started");

    internal void NotifySessionEnded() => Dispatch(builder => builder.SessionEndedHandlers, "session-ended");

    internal void NotifySceneReset() => Dispatch(builder => builder.SceneResetHandlers, "scene-reset");

    /// <summary>Lets the features draw. Called from OnGUI.</summary>
    internal void DrawHud() => Dispatch(builder => builder.DrawHudHandlers, "draw-hud");

    private void Dispatch(Func<FeatureBuilder, IReadOnlyList<Action>> select, string what)
    {
        foreach (var registered in _features)
        {
            var handlers = select(registered.Builder);
            for (int i = 0; i < handlers.Count; i++)
                Run(registered.Feature.Id, what, handlers[i]);
        }
    }

    /// <summary>
    /// Calls one feature callback. A feature that throws is logged and left running: it must
    /// not be able to take the whole update loop — or the other features — down with it.
    /// </summary>
    private static void Run(string featureId, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            FeatureLog.ErrorThrottled($"{featureId}:{what}", $"[Feature:{featureId}] {what} failed: {ex}");
        }
    }

    public void Dispose()
    {
        for (var i = _features.Count - 1; i >= 0; i--)
            _features[i].Builder.Dispose();
        _features.Clear();
    }
}
