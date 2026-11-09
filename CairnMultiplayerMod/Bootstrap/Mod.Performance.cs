using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CairnMultiplayerMod.Internal;
using CairnMultiplayerMod.Internal.Diagnostics;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Bootstrap;

public partial class Mod
{
    private PerformanceCapture _performance;
    private Dictionary<string, string> _performanceMetadata;
    private Task<string> _performanceExport;
    private bool _performanceFailed;
    private (bool Multiplayer, bool Connected, bool Host, int Peers) _performanceSession;

    private PerformanceCapture.Measurement Measure(PerformanceArea area)
        => _performance?.Measure(area) ?? default;

    // Deliberately outside FeatureHost: diagnostics also run in native solo mode.
    private void TickPerformance()
    {
        if (_performanceFailed) return;
        try
        {
            if (_performanceExport?.IsCompleted == true)
            {
                var finished = _performanceExport;
                _performanceExport = null;
                LoggerInstance.Msg($"[Performance] Local reports: {finished.GetAwaiter().GetResult()}.json / .md");
            }
            if (ModConfig.PerformanceDiagnostics?.Value != true)
            {
                FinishPerformance("diagnostics-disabled");
                return;
            }
            var now = Stopwatch.GetTimestamp();
            var keyboard = Keyboard.current;
            var toggle = keyboard != null && keyboard.f8Key.wasPressedThisFrame &&
                (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed);
            var activeAtStart = _performance != null;
            if (_performance != null)
            {
                var session = PerformanceSession();
                if (session != _performanceSession)
                {
                    _performanceSession = session;
                    _performance.AddEvent(now, "session", SessionDescription(session));
                }
                _performance.Advance(now);
                if (_performance.IsComplete) FinishPerformance(null);
            }
            if (toggle)
            {
                if (activeAtStart) FinishPerformance("manual-stop");
                else if (_performanceExport == null) StartPerformance();
                else LoggerInstance.Msg("[Performance] Previous capture is still being exported.");
            }
        }
        catch (Exception ex)
        {
            _performance = null;
            _performanceFailed = true;
            LoggerInstance.Warning($"[Performance] Diagnostics stopped; gameplay remains active: {ex.Message}");
        }
    }

    private void StartPerformance()
    {
        // Query Unity only here, on the main thread. Never from the export worker.
        _performanceSession = PerformanceSession();
        _performanceMetadata = new Dictionary<string, string>
        {
            ["Scenario"] = (ModConfig.PerformanceScenario.Value ?? "")[..Math.Min(512, (ModConfig.PerformanceScenario.Value ?? "").Length)],
            ["GameVersion"] = Application.version,
            ["UnityVersion"] = Application.unityVersion,
            ["ModVersion"] = MelonInfoCache.Version,
            ["ModModuleId"] = typeof(Mod).Assembly.ManifestModule.ModuleVersionId.ToString(),
            ["LoaderVersion"] = typeof(MelonMod).Assembly.GetName().Version?.ToString() ?? "unknown",
            ["OS"] = SystemInfo.operatingSystem,
            ["CPU"] = SystemInfo.processorType,
            ["LogicalProcessors"] = SystemInfo.processorCount.ToString(),
            ["RAMMiB"] = SystemInfo.systemMemorySize.ToString(),
            ["GPU"] = SystemInfo.graphicsDeviceName,
            ["GPUDriverApi"] = SystemInfo.graphicsDeviceVersion,
            ["VRAMMiB"] = SystemInfo.graphicsMemorySize.ToString(),
            ["Resolution"] = $"{Screen.width}x{Screen.height}",
            ["FullScreenMode"] = Screen.fullScreenMode.ToString(),
            ["QualityLevelIndex"] = QualitySettings.GetQualityLevel().ToString(),
            ["VSyncCount"] = QualitySettings.vSyncCount.ToString(),
            ["TargetFrameRate"] = Application.targetFrameRate.ToString(),
            ["AntiAliasing"] = QualitySettings.antiAliasing.ToString(),
            ["Shadows"] = QualitySettings.shadows.ToString(),
            ["StartScene"] = CurrentScene ?? "unknown",
            ["StartSession"] = SessionDescription(_performanceSession),
            ["NativeGpuTiming"] = "unavailable; use the common external capture",
            ["GraphicsSettingsNote"] = "Engine settings are partial; attach the game's complete settings and upscaler state to the benchmark manifest."
        };
        LoggerInstance.Msg("[Performance] Capture armed: 30 s warmup, then 180 s sampling. Ctrl+F8 stops early. Reports stay local.");
        // Allocate at the trigger, before the 30-second warmup and any measured interval.
        _performance = new PerformanceCapture(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        _performanceMetadata["TriggerUtc"] = DateTimeOffset.UtcNow.ToString("O");
    }

    private (bool Multiplayer, bool Connected, bool Host, int Peers) PerformanceSession()
        => (_multiplayerModeActive, Network?.IsConnected == true, Lobby?.IsHost == true,
            Network?.RemotePlayers.Count ?? 0);

    private static string SessionDescription((bool Multiplayer, bool Connected, bool Host, int Peers) s)
        => $"{(s.Multiplayer ? "multiplayer" : "solo")}; connected={s.Connected}; host={s.Host}; remotePlayers={s.Peers}";

    private void MarkPerformanceScene(string kind, string scene)
        => _performance?.AddEvent(Stopwatch.GetTimestamp(), kind, scene);

    private void FinishPerformance(string reason)
    {
        if (_performance == null) return;
        var capture = _performance;
        _performance = null;
        capture.Stop(Stopwatch.GetTimestamp(), reason ?? "stopped");
        var metadata = _performanceMetadata;
        _performanceMetadata = null;
        metadata["EndScene"] = CurrentScene ?? "unknown";
        metadata["EndSession"] = SessionDescription(PerformanceSession());
        metadata["ExportQueuedUtc"] = DateTimeOffset.UtcNow.ToString("O");
        var gameRoot = Directory.GetParent(MelonEnvironment.MelonLoaderDirectory)?.FullName ?? AppContext.BaseDirectory;
        var directory = Path.Combine(gameRoot, "UserData", "CairnMultiplayer", "Performance");
        // The worker may only receive frozen arrays because Unity objects are main-thread-only.
        _performanceExport = Task.Run(() => PerformanceReport.Write(directory, capture, metadata));
    }

    private void StopPerformance()
    {
        FinishPerformance(CrashHandler.IsFatal ? "fatal-shutdown" : "shutdown");
        // Normal shutdown must not drop a pending local export when the runtime unloads.
        if (_performanceExport != null)
        {
            var path = _performanceExport.GetAwaiter().GetResult();
            _performanceExport = null;
            LoggerInstance.Msg($"[Performance] Local reports: {path}.json / .md");
        }
    }
}
