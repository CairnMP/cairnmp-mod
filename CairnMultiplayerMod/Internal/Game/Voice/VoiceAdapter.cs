using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Networking;
using Concentus;
using Concentus.Enums;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game.Voice;

internal sealed class VoiceAdapter : IVoiceApi, IDisposable
{
    internal const int SampleRate = 48000, FrameSamples = 960;
    internal const float MaxDistance = VoiceSpatialPolicy.NormalMaxDistance;
    private readonly Func<NetworkManager> _network;
    private readonly Func<bool> _inGame;
    private readonly VoiceActivityGate _gate = new();
    private readonly VoiceProcessor _processor = new();
    private readonly VoiceAcousticZones _acousticZones = new();
    private readonly VoiceOcclusionProbe _occlusion = new();
    private readonly Dictionary<int, VoicePlayback> _speakers = new();
    private readonly HashSet<int> _mutedPlayers = new();
    private readonly Queue<(uint Burst, uint Sequence, byte[] Data)> _outgoing = new();
    private readonly float[][] _preRoll = { new float[FrameSamples], new float[FrameSamples], new float[FrameSamples] };
    private readonly List<int> _speakerIds = new(8);
    private readonly float[] _frame = new float[FrameSamples];
    private readonly byte[] _encoded = new byte[400];
    private readonly short[] _pcm16 = new short[FrameSamples];
    private readonly VoiceSettingsIntegration _settings;
    private IOpusEncoder _encoder;
    private IVoiceCapture _microphone;
    private IVoiceOutput _output;
    private VoicePlayback _monitor;
    private string _requestedDevice, _defaultDevice = "";
    private Task<(VoiceDevice[] Devices, string DefaultId)> _deviceRefresh;
    private int _preRollStart, _preRollCount;
    private uint _sequence, _burst;
    private double _retryAt, _lastPoll, _refreshAt, _outputRetryAt;
    private bool _wasSending;
    internal bool TestMicrophone { get; set; }
    internal bool IsTransmitting { get; private set; }
    internal float LevelDb => _gate.LevelDb;
    internal float InputLevelDb => _processor.InputLevelDb;
    internal float ProcessedLevelDb => _processor.OutputLevelDb;
    internal float ProcessingGainDb => _processor.GainDb;
    internal bool IsCapturing => _microphone?.IsRunning == true;
    internal long CapturedSamples => _microphone?.CapturedSamples ?? 0;
    internal long RenderedSamples => _output?.RenderedSamples ?? 0;
    internal string ActiveMicrophoneId => _microphone?.DeviceId;
    internal IEnumerable<(int Id, string Name)> RemotePlayers => _network()?.RemotePlayers.Select(p => (p.Key, p.Value.Name)) ?? Enumerable.Empty<(int, string)>();
    internal bool IsMuted(int playerId) => _mutedPlayers.Contains(playerId);
    internal void SetMuted(int playerId, bool muted)
    {
        if (muted) { _mutedPlayers.Add(playerId); StopSpeaker(playerId); }
        else _mutedPlayers.Remove(playerId);
    }
    internal string Status { get; private set; } = "Microphone idle — enable the local test";
    internal VoiceDevice[] Devices { get; private set; } = Array.Empty<VoiceDevice>();
    internal bool SettingsOpen => _settings.IsOpen;

    internal VoiceAdapter(Func<NetworkManager> network, Func<bool> inGame)
    {
        _network = network;
        _inGame = inGame;
        _settings = new VoiceSettingsIntegration(this);
    }

    public void Tick(bool connected)
    {
        var now = Time.realtimeSinceStartupAsDouble;
        if (_deviceRefresh?.IsCompleted == true)
        {
            try
            {
                var result = _deviceRefresh.GetAwaiter().GetResult();
                Devices = result.Devices;
                _defaultDevice = result.DefaultId;
            }
            catch (Exception ex) { Devices = Array.Empty<VoiceDevice>(); Status = "Microphone unavailable: " + ex.Message; }
            _deviceRefresh = null;
        }
        if (_deviceRefresh == null && now >= _refreshAt)
        {
            _refreshAt = now + 5;
            // Native endpoint discovery may block on Windows, Linux or macOS. It
            // must never stall Unity's main thread.
            _deviceRefresh = Task.Run(() =>
            {
                var devices = VoiceAudioBackend.EnumerateCaptureDevices(out var defaultId);
                return (devices, defaultId);
            });
        }
        try { _settings.Tick(); }
        catch (Exception ex) { ModLog.SuppressedException("voice.settings-tick", ex); }
        var inGame = connected && IsNativeGameplayStable() && _inGame() && _network()?.IsHandshakeComplete == true;
        var wantsCapture = Application.isFocused && (TestMicrophone || (inGame && VoicePreferences.CurrentMode != VoiceMode.Muted));
        var requested = VoicePreferences.Microphone.Value ?? "";
        var desiredId = requested.Length == 0 ? _defaultDevice : requested;
        if (desiredId != _requestedDevice)
        {
            _requestedDevice = desiredId;
            StopCapture();
            _retryAt = 0;
        }
        IsTransmitting = false;
        try
        {
            if (!wantsCapture || (_microphone != null && !Devices.Any(d => d.Id == _microphone.DeviceId)))
                StopCapture();
            if (wantsCapture && _microphone == null && now >= _retryAt) StartCapture(desiredId, now);
            if (_microphone != null) Capture(inGame && !TestMicrophone && !SettingsOpen, now);
        }
        catch (Exception ex)
        {
            Status = "Microphone error: " + ex.Message;
            ModLog.Warning("[Voice] " + Status);
            StopCapture();
            _retryAt = now + 5;
        }
        if ((!TestMicrophone || !Application.isFocused) && _monitor != null)
        {
            _output?.Remove(_monitor); _monitor.Dispose(); _monitor = null;
        }
        UpdateSpeakers(inGame, now);
    }

    private void StartCapture(string id, double now)
    {
        _retryAt = now + 5;
        if (Devices.Length == 0) { Status = "No microphone detected"; return; }
        if (!Devices.Any(d => d.Id == id))
        {
            Status = "Selected microphone disconnected — choose another microphone";
            return;
        }
        _microphone = VoiceAudioBackend.CreateCapture(id);
        _lastPoll = now;
        _encoder ??= OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 32000;
        _encoder.Complexity = 8;
        _encoder.UseVBR = true;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        Status = "Microphone ready";
    }

    private void Capture(bool maySend, double now)
    {
        if (!_microphone.IsRunning) { StopCapture(); Status = "Microphone stopped — retrying"; return; }
        if (now - _lastPoll > .2 || _microphone.BufferedSamples > FrameSamples * 5)
        {
            // Slow frames and large driver batches can overflow the live window, so keep
            // the newest samples.
            _microphone.KeepLatest(FrameSamples * 5);
            ClearPreRoll();
            _gate.Reset();
            _wasSending = false;
        }
        _lastPoll = now;
        var ptt = Enum.TryParse<Key>(VoicePreferences.PushToTalkKey.Value, true, out var key)
            && Enum.IsDefined(typeof(Key), key) && key != Key.None && Keyboard.current != null
            && Keyboard.current[key].isPressed && !InputCaptureState.IsKeyboardCaptured;
        ptt |= !InputCaptureState.IsKeyboardCaptured
               && ModControllerInput.IsHeld(ControllerShortcut.PushToTalk);
        for (var frame = 0; frame < 5 && _microphone.TryRead(_frame); frame++)
        {
            _processor.Process(_frame, VoicePreferences.EnhanceMicrophone.Value);
            var active = _gate.ProcessLevel(_processor.DetectionLevelDb, VoicePreferences.SafeThreshold);
            var send = maySend && (VoicePreferences.CurrentMode == VoiceMode.VoiceActivity ? active : VoicePreferences.CurrentMode == VoiceMode.PushToTalk && ptt);
            if (TestMicrophone && EnsureOutput(now))
            {
                if (_monitor == null) { _monitor = new VoicePlayback { Volume = 1 }; _output.Add(_monitor); }
                _monitor.WriteLocal(_frame);
            }
            if (send)
            {
                if (!_wasSending)
                {
                    _encoder.ResetState();
                    _burst++;
                    if (VoicePreferences.CurrentMode == VoiceMode.VoiceActivity)
                    {
                        for (var i = 0; i < _preRollCount; i++) Encode(_preRoll[(_preRollStart + i) % _preRoll.Length]);
                        ClearPreRoll();
                    }
                }
                Encode(_frame);
                IsTransmitting = true;
            }
            _wasSending = send;
            if (!send && maySend && VoicePreferences.CurrentMode == VoiceMode.VoiceActivity)
            {
                var index = (_preRollStart + _preRollCount) % _preRoll.Length;
                Array.Copy(_frame, _preRoll[index], FrameSamples);
                if (_preRollCount < _preRoll.Length) _preRollCount++;
                else _preRollStart = (_preRollStart + 1) % _preRoll.Length;
            }
            else ClearPreRoll();
        }
        if (!maySend) { _outgoing.Clear(); ClearPreRoll(); _wasSending = false; }
    }

    private void ClearPreRoll() { _preRollStart = 0; _preRollCount = 0; }

    private void Encode(float[] samples)
    {
        for (var i = 0; i < FrameSamples; i++) _pcm16[i] = (short)Math.Clamp(samples[i] * 32768f, short.MinValue, short.MaxValue);
        var length = _encoder.Encode(_pcm16.AsSpan(), FrameSamples, _encoded.AsSpan(), _encoded.Length);
        _outgoing.Enqueue((_burst, _sequence++, _encoded.AsSpan(0, length).ToArray()));
        while (_outgoing.Count > 8) _outgoing.Dequeue();
    }
    public bool TryCapture(out uint burst, out uint sequence, out byte[] opus)
    {
        if (_outgoing.TryDequeue(out var frame)) { burst = frame.Burst; sequence = frame.Sequence; opus = frame.Data; return true; }
        burst = sequence = 0; opus = null; return false;
    }
    public void Receive(int playerId, uint burst, uint sequence, byte[] opus)
    {
        if (_mutedPlayers.Contains(playerId) || opus == null || opus.Length == 0 || opus.Length > 400 || VoicePreferences.SafeVolume <= 0 || !TryGetSpeaker(playerId, out var listener, out var position, out var distance)) return;
        var acoustics = _acousticZones.Resolve(position);
        if (distance >= acoustics.MaxDistance && !_speakers.ContainsKey(playerId)) return;
        var now = Time.realtimeSinceStartupAsDouble;
        if (!EnsureOutput(now)) return;
        if (!_speakers.TryGetValue(playerId, out var speaker))
        {
            if (_speakers.Count >= 8) return;
            speaker = new VoicePlayback(spatialized: true);
            _speakers.Add(playerId, speaker);
            _output.Add(speaker);
        }
        speaker.Receive(burst, sequence, opus, now);
    }
    private bool TryGetSpeaker(int id, out Vector3 listener, out Vector3 position, out float distance)
    {
        listener = default; position = default; distance = 0;
        var network = _network();
        if (!IsNativeGameplayStable() || !_inGame() || network?.IsHandshakeComplete != true || !network.RemotePlayers.TryGetValue(id, out var remote) || remote == null || remote.State != PlayerState.InGame ||
            !LocalPlayerInterop.TryGetPose(out listener, out _)) return false;
        // Audio only needs a known position, not the native climbing harness. A
        // fallback avatar or a temporarily missing harness must not mute a peer.
        if (!RemotePlayerManager.TryGetGhostHarnessAttachPosition(id, out position))
        {
            if (!VoiceSpatialPolicy.HasRecentPose(remote.LastUpdateTime,
                    DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond)) return false;
            position = new Vector3(remote.X, remote.Y, remote.Z);
        }
        distance = Vector3.Distance(listener, position);
        return float.IsFinite(distance);
    }

    /// <summary>
    /// Whether we are currently hearing this player. Read from the audio already received, so
    /// it costs no extra packet — and a muted or out-of-range player never gets a fresh stamp,
    /// which makes the indicator clear itself with no special case.
    /// </summary>
    public bool IsSpeaking(int playerId)
        => _speakers.TryGetValue(playerId, out var speaker)
           && VoiceIndicatorPolicy.IsSpeaking(speaker.LastReceived, Time.realtimeSinceStartupAsDouble);

    private static bool IsNativeGameplayStable()
        => GameLifecycleService.TryGetRawGameState(out var lifecycle)
           && PlayerStateBroadcaster.MapLifecycleForNetwork(
               lifecycle, MultiplayerPausePatch.IsPauseMenuActive) == PlayerState.InGame;
    private void UpdateSpeakers(bool inGame, double now)
    {
        if (_output != null && !_output.IsRunning) { ResetOutput(); _outputRetryAt = now + 2; }
        _speakerIds.Clear();
        foreach (var id in _speakers.Keys) _speakerIds.Add(id);
        foreach (var id in _speakerIds)
        {
            var speaker = _speakers[id];
            if (!inGame || VoicePreferences.SafeVolume <= 0 || now - speaker.LastReceived > 1 || !TryGetSpeaker(id, out var listener, out var position, out var distance))
            { StopSpeaker(id); continue; }
            var acoustics = _acousticZones.Resolve(position);
            if (!speaker.UpdateRange(distance < acoustics.MaxDistance, now)) { StopSpeaker(id); continue; }
            var occlusion = _occlusion.Resolve(id, listener, position, now);
            var directVolume = VoicePreferences.SafeVolume * VoiceSpatialPolicy.Attenuation(distance, acoustics.MaxDistance);
            var directCutoff = VoiceSpatialPolicy.Cutoff(distance, acoustics.MaxDistance);
            var directReverb = VoiceSpatialPolicy.Reverb(distance, acoustics.Reverb, acoustics.MaxDistance);
            speaker.Volume = VoiceSpatialPolicy.OccludedVolume(directVolume, occlusion);
            speaker.Cutoff = VoiceSpatialPolicy.OccludedCutoff(directCutoff, occlusion);
            speaker.Reverb = VoiceSpatialPolicy.OccludedReverb(directReverb, occlusion);
            var camera = Camera.main;
            speaker.Pan = camera == null ? 0 : Vector3.Dot(camera.transform.right, (position - camera.transform.position).normalized);
            speaker.Tick(now);
        }
        if (_output != null && _speakers.Count == 0 && _monitor == null) ResetOutput();
    }
    private bool EnsureOutput(double now)
    {
        if (_output?.IsRunning == true) return true;
        if (now < _outputRetryAt) return false;
        _outputRetryAt = now + 5;
        try { ResetOutput(); _output = VoiceAudioBackend.CreateOutput(); return true; }
        catch (Exception ex) { Status = "Audio output unavailable: " + ex.Message; ModLog.Warning("[Voice] " + Status); return false; }
    }
    private void StopCapture()
    {
        var capture = _microphone;
        _microphone = null;
        _gate.Reset();
        _processor.Reset();
        _outgoing.Clear(); ClearPreRoll(); _wasSending = false; IsTransmitting = false;
        capture?.Dispose();
    }
    public void RemovePlayer(int playerId)
    {
        _mutedPlayers.Remove(playerId);
        _occlusion.Remove(playerId);
        StopSpeaker(playerId);
    }
    private void StopSpeaker(int playerId)
    {
        if (!_speakers.Remove(playerId, out var speaker)) return;
        _output?.Remove(speaker);
        speaker.Dispose();
    }
    private void ResetOutput()
    {
        _output?.Dispose(); _output = null;
        _monitor?.Dispose(); _monitor = null;
        foreach (var speaker in _speakers.Values) speaker.Dispose();
        _speakers.Clear();
    }
    public void Reset()
    {
        ResetAudioState();
        _settings.ResetSession();
    }
    public void ResetScene()
    {
        ResetAudioState();
        _settings.ResetScene();
    }
    private void ResetAudioState()
    {
        TestMicrophone = false;
        StopCapture();
        ResetOutput();
        _mutedPlayers.Clear();
        _acousticZones.Reset();
        _occlusion.Reset();
    }
    public void Dispose() { Reset(); _encoder?.Dispose(); _settings.Dispose(); }
}
