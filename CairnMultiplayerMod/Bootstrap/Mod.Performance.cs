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
    private string _performanceStatus, _performanceLastReport, _performanceLastStop;
    private GUIStyle _performanceStatusStyle;
    private bool _performanceHudFailed;
    private long _nextPerformanceStatus;
    private bool _performanceFocused;
    private float _performanceTimeScale;

    private void SetPerformanceStatus(string english, string portuguese)
        => _performanceStatus = ModConfig.PerformanceHudLanguage?.Value?.StartsWith("pt", StringComparison.OrdinalIgnoreCase) == true
            ? portuguese : english;

    private void UpdatePerformanceStatus(long now)
    {
        if (now < _nextPerformanceStatus) return;
        _nextPerformanceStatus = now + Stopwatch.Frequency;
        if (_performance != null)
        {
            var focused = Application.isFocused;
            var timeScale = Time.timeScale;
            if (focused != _performanceFocused || timeScale != _performanceTimeScale)
            {
                _performanceFocused = focused; _performanceTimeScale = timeScale;
                _performance.AddEvent(now, "focus-timescale", FormattableString.Invariant($"focused={focused}; timeScale={timeScale}"));
            }
            var elapsed = Math.Max(0, (now - _performance.StartedTimestamp) / (double)_performance.Frequency);
            if (!_performance.MeasurementStartSeconds.HasValue)
            {
                var remaining = Math.Max(0, (int)Math.Ceiling(_performance.WarmupSeconds - elapsed));
                SetPerformanceStatus($"PERFORMANCE — WARMUP: {remaining} s\nPlay normally; recording starts automatically. Ctrl+F8: stop early.",
                    $"DESEMPENHO — AQUECENDO: {remaining} s\nJogue normalmente; a coleta começa sozinha. Ctrl+F8: encerrar antes.");
            }
            else
            {
                var remaining = Math.Max(0, (int)Math.Ceiling(_performance.RequestedSeconds - elapsed + _performance.MeasurementStartSeconds.Value));
                SetPerformanceStatus($"PERFORMANCE — RECORDING: {remaining} s remaining\nKeep playing; the result is saved automatically. Ctrl+F8: stop early (partial capture).",
                    $"DESEMPENHO — GRAVANDO: faltam {remaining} s\nContinue jogando; o resultado será salvo sozinho. Ctrl+F8: encerrar antes (coleta parcial).");
            }
        }
        else if (_performanceExport != null)
            SetPerformanceStatus("PERFORMANCE — SAVING\nFinishing the local report. You can keep playing.",
                "DESEMPENHO — SALVANDO\nFinalizando o relatório local. Pode continuar jogando.");
        else if (_performanceLastReport != null)
        {
            var complete = _performanceLastStop == "duration-complete";
            SetPerformanceStatus($"PERFORMANCE — SAVED ({(complete ? "complete" : "partial")})\nReport: UserData/CairnMultiplayer/Performance. Ctrl+F8: new capture.",
                $"DESEMPENHO — SALVO ({(complete ? "completo" : "parcial")})\nPode voltar ao chat. Relatório em UserData/CairnMultiplayer/Performance. Ctrl+F8: nova coleta.");
        }
        else
            SetPerformanceStatus("PERFORMANCE — Ctrl+F8: start\nEnter Story and play normally. Capture: 30 s warmup + 3 min recording.",
                "DESEMPENHO — Ctrl+F8: iniciar\nEntre em História e jogue normalmente. São 30 s de aquecimento + 3 min de coleta.");
    }

    private void DrawPerformanceStatus()
    {
        if (_performanceHudFailed || ModConfig.PerformanceDiagnostics?.Value != true || string.IsNullOrEmpty(_performanceStatus)) return;
        try
        {
            var skin = GUI.skin;
            if (skin == null) return;
            _performanceStatusStyle ??= new GUIStyle(skin.box) { alignment = TextAnchor.UpperLeft, wordWrap = true, fontSize = 14 };
            var width = Screen.width;
            GUI.Box(new Rect(Math.Max(20, width - 780), 20, Math.Max(0, Math.Min(760, width - 40)), 100),
                _performanceStatus, _performanceStatusStyle);
        }
        catch (Exception ex)
        {
            _performanceHudFailed = true;
            LoggerInstance.Warning($"[Performance] Status overlay stopped; recording is unaffected: {ex.Message}");
        }
    }

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
                _performanceLastReport = finished.GetAwaiter().GetResult();
                _nextPerformanceStatus = 0;
                LoggerInstance.Msg($"[Performance] Local reports: {_performanceLastReport}.json / .md");
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
            UpdatePerformanceStatus(now);
        }
        catch (Exception ex)
        {
            _performance = null;
            _performanceFailed = true;
            SetPerformanceStatus("PERFORMANCE — RECORDING FAILED\nGameplay remains active. Check the log before retrying.",
                "DESEMPENHO — FALHA NA COLETA\nO jogo continua ativo. Avise no chat para verificarmos o log.");
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
            ["TimingScopeRevision"] = "2: ModUpdate includes local belay, diagnostics and TickMod; Ropes includes local belay and the direct harness tether",
            ["TimingScopeLimitations"] = "Only instrumented OnUpdate/OnGUI work is timed. Native callbacks outside those scopes and GPU timings remain unmeasured.",
            ["ContextSampling"] = "Focus and time scale sampled once per second during capture; scene/session events retained separately.",
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
        _performanceLastReport = _performanceLastStop = null;
        _performanceHudFailed = false;
        _nextPerformanceStatus = 0;
        _performanceFocused = Application.isFocused;
        _performanceTimeScale = Time.timeScale;
        _performance.AddEvent(_performance.StartedTimestamp, "focus-timescale",
            FormattableString.Invariant($"focused={_performanceFocused}; timeScale={_performanceTimeScale}"));
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
        _performanceLastStop = capture.StopReason;
        _nextPerformanceStatus = 0;
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
