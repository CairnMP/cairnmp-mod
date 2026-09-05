using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppSteamworks;

namespace CairnMultiplayerMod.Internal.Networking
{
    internal sealed partial class SteamLobbyManager
    {
        private void OnLobbyCreatedCb(LobbyCreated_t evt)
        {
            if (evt.m_eResult != EResult.k_EResultOK)
            {
                ModLog.Error($"[SteamLobby] LobbyCreated failed: {evt.m_eResult}");
                FailCreate($"Steam error: {evt.m_eResult}");
                return;
            }

            var lobbyId = new CSteamID(evt.m_ulSteamIDLobby);
            IsHost = true;
            HostSteamId = SteamUser.GetSteamID().m_SteamID;

            // Shareable short code: we retry a few times in case of collision.
            // (Probability ~0 over 30^8 but retry is cheap.)
            var code = GenerateRoomCode();
            var cfg = _pendingHostConfig ?? new HostConfig();
            var hostName = string.IsNullOrWhiteSpace(cfg.PlayerName)
                ? SteamFriends.GetPersonaName()
                : cfg.PlayerName;
            var lobbyName = string.IsNullOrWhiteSpace(cfg.LobbyName) ? $"{hostName}'s lobby" : cfg.LobbyName;

            try
            {
                SteamMatchmaking.SetLobbyData(lobbyId, KeyCairnApp, "1");
                SteamMatchmaking.SetLobbyData(lobbyId, KeyCode, code);
                SteamMatchmaking.SetLobbyData(lobbyId, KeyName, lobbyName);
                SteamMatchmaking.SetLobbyData(lobbyId, KeyHostName, hostName);
                SteamMatchmaking.SetLobbyData(lobbyId, KeyModVersion, ModVersion());
                SteamMatchmaking.SetLobbyData(lobbyId, KeyProtocolVersion, Protocol.Version.ToString(CultureInfo.InvariantCulture));
                SteamMatchmaking.SetLobbyData(lobbyId, KeyVisibility, cfg.Visibility.ToString());
                SteamMatchmaking.SetLobbyData(lobbyId, KeyStartNonce, "");
                SteamMatchmaking.SetLobbyMemberLimit(lobbyId, Math.Max(2, Math.Min(cfg.MaxPlayers, 16)));
                SteamMatchmaking.SetLobbyJoinable(lobbyId, true);
            }
            catch (Exception ex)
            {
                ModLog.Error($"[SteamLobby] SetLobbyData failed: {ex}");
            }

            CurrentLobbyId = lobbyId;
            CurrentRoomCode = code;
            CurrentLobbyName = lobbyName;

            ModLog.Info($"[SteamLobby] Lobby created: id={lobbyId.m_SteamID} code={code} name='{lobbyName}'");

            // LobbyEnter_t will follow automatically (the creator enters their
            // own lobby). We resolve _createTcs there so _members is populated.
        }

        private void OnLobbyEnterCb(LobbyEnter_t evt)
        {
            if (evt.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                var reason = DescribeLobbyEnterFailure(evt.m_EChatRoomEnterResponse);
                ModLog.Error($"[SteamLobby] LobbyEnter failed: {reason} eventLobby={evt.m_ulSteamIDLobby} pendingLobby={_pendingJoinLobbyId.m_SteamID} appId={_activeAppId}");
                FailCreate(reason);
                FailJoin(reason);
                return;
            }

            var lobbyId = new CSteamID(evt.m_ulSteamIDLobby);
            CurrentLobbyId = lobbyId;
            HostSteamId = SteamMatchmaking.GetLobbyOwner(lobbyId).m_SteamID;
            IsHost = HostSteamId == SteamUser.GetSteamID().m_SteamID;

            if (!IsCompatibleLobby(lobbyId, out var incompatibilityReason))
            {
                ModLog.Warning($"[SteamLobby] Rejected incompatible lobby {lobbyId.m_SteamID}: {incompatibilityReason}");
                try { SteamMatchmaking.LeaveLobby(lobbyId); }
                catch (Exception exception) { ModLog.SuppressedException("steam-lobby.leave-incompatible-lobby", exception); }
                ResetLobbyState();
                FailCreate(incompatibilityReason);
                FailJoin(incompatibilityReason);
                return;
            }

            // For a join (not a create), we read the code and name from
            // the LobbyData already published by the host.
            if (string.IsNullOrEmpty(CurrentRoomCode))
                CurrentRoomCode = SteamMatchmaking.GetLobbyData(lobbyId, KeyCode);
            if (string.IsNullOrEmpty(CurrentLobbyName))
                CurrentLobbyName = SteamMatchmaking.GetLobbyData(lobbyId, KeyName);

            RebuildMembers();
            ModLog.Info($"[SteamLobby] Entered lobby {lobbyId.m_SteamID} (host? {IsHost}, members={_members.Count}).");

            OnLobbyEntered?.Invoke(lobbyId);
            OnMembersChanged?.Invoke();
            TryHandleStartSignal();

            ResolveCreate(true);
            ResolveJoin(true);
            _pendingJoinLobbyId = default;
        }

        private void OnLobbyMatchListCb(LobbyMatchList_t evt)
        {
            // Case 1: we're waiting on a JoinByCode → chain JoinLobby on the first
            // result (or fail if empty).
            if (_pendingJoinCode != null)
            {
                var context = _pendingJoinCodeFallbackScan ? "JoinByCodeFallback" : "JoinByCode";
                var maxResults = _pendingJoinCodeFallbackScan ? MaxLobbyBrowserResults : MaxJoinCodeResults;
                var results = ReadLobbyListResults(maxResults, context, evt.m_nLobbiesMatching);
                var count = results.Count;
                if (count == 0)
                {
                    if (TryStartJoinCodeFallbackSearch())
                        return;

                    var failedCode = _pendingJoinCode;
                    _pendingJoinCode = null;
                    _pendingJoinCodeFallbackScan = false;
                    ModLog.Warning($"[SteamLobby] Code '{failedCode}' did not match any lobby.");
                    FailJoin("Lobby not found. Ask the host to keep the lobby public or send a Steam invite.");
                    return;
                }

                CSteamID lobbyId = default;
                for (int i = 0; i < results.Count; i++)
                {
                    var candidate = results[i];
                    var candidateCode = SafeLobbyData(candidate, KeyCode);
                    var candidateName = SafeLobbyData(candidate, KeyName);
                    var candidateHost = SafeLobbyData(candidate, KeyHostName);
                    var isValid = HasUsableSteamId(candidate);
                    ModLog.Debug($"[SteamLobby] JoinByCode candidate index={i} id={candidate.m_SteamID} valid={isValid} code='{candidateCode}' name='{candidateName}' host='{candidateHost}' appId={_activeAppId}");

                    if (!isValid) continue;
                    var normalizedCandidateCode = NormalizeCode(candidateCode);
                    if (!string.Equals(normalizedCandidateCode, _pendingJoinCode, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_pendingJoinCodeFallbackScan && results.Count == 1 && string.IsNullOrEmpty(normalizedCandidateCode))
                        {
                            ModLog.Warning("[SteamLobby] Single code-filtered lobby candidate has no readable code metadata; joining by Steam result ID.");
                        }
                        else
                        {
                            continue;
                        }
                    }

                    lobbyId = candidate;
                    break;
                }

                if (!HasUsableSteamId(lobbyId))
                {
                    if (TryStartJoinCodeFallbackSearch())
                        return;

                    var failedCode = _pendingJoinCode;
                    _pendingJoinCode = null;
                    _pendingJoinCodeFallbackScan = false;
                    ModLog.Warning($"[SteamLobby] Code '{failedCode}' returned {count} lobby result(s), but none matched the requested code.");
                    FailJoin("Lobby not found. Ask the host to keep the lobby public or send a Steam invite.");
                    return;
                }

                _pendingJoinCode = null;
                _pendingJoinCodeFallbackScan = false;
                _pendingJoinLobbyId = lobbyId;
                try
                {
                    SteamMatchmaking.JoinLobby(lobbyId);
                    ModLog.Debug($"[SteamLobby] JoinLobby({lobbyId.m_SteamID}) requested from code search.");
                }
                catch (Exception ex) { FailJoin($"Steam error: {ex.Message}"); }
                return;
            }

            // Case 2: RequestLobbyList for the browser.
            if (_listTcs != null)
            {
                var results = ReadLobbyListResults(MaxLobbyBrowserResults, "RequestLobbyList", evt.m_nLobbiesMatching);
                var list = new List<LobbyEntry>(results.Count);
                for (int i = 0; i < results.Count; i++)
                {
                    var id = results[i];
                    if (!HasUsableSteamId(id)) continue;
                    var name = SteamMatchmaking.GetLobbyData(id, KeyName);
                    var host = SteamMatchmaking.GetLobbyData(id, KeyHostName);
                    var cap = SteamMatchmaking.GetLobbyMemberLimit(id);
                    var cnt = SteamMatchmaking.GetNumLobbyMembers(id);
                    list.Add(new LobbyEntry
                    {
                        LobbyId = id.m_SteamID,
                        Name = string.IsNullOrEmpty(name) ? "(unnamed)" : name,
                        HostName = host ?? "",
                        PlayerCount = cnt,
                        MaxPlayers = cap,
                        Region = "",
                    });
                }
                var tcs = _listTcs;
                _listTcs = null;
                ClearOperationIfIdle();
                tcs.TrySetResult(list);
            }
        }

        private static List<CSteamID> ReadLobbyListResults(int maxResults, string context, uint callbackCount)
        {
            // The IL2CPP wrapper for LobbyMatchList_t can report a corrupt count.
            // So we read the Steam slots in a bounded way and stop at the first
            // invalid ID once we've found at least one result.
            var reported = unchecked((int)callbackCount);
            if (reported < 0 || reported > maxResults)
                ModLog.Warning($"[SteamLobby] {context} callback count looked invalid ({reported}); scanning up to {maxResults}.");

            var scanLimit = reported >= 0 && reported <= maxResults ? reported : maxResults;
            var results = new List<CSteamID>(Math.Max(0, scanLimit));
            for (int i = 0; i < scanLimit; i++)
            {
                CSteamID id;
                try { id = SteamMatchmaking.GetLobbyByIndex(i); }
                catch (Exception ex)
                {
                    ModLog.Warning($"[SteamLobby] {context} GetLobbyByIndex({i}) failed: {ex.Message}");
                    break;
                }

                if (!HasUsableSteamId(id))
                {
                    if (results.Count > 0) break;
                    continue;
                }
                results.Add(id);
            }

            return results;
        }

        private void OnLobbyChatUpdateCb(LobbyChatUpdate_t evt)
        {
            if (!IsInLobby || evt.m_ulSteamIDLobby != CurrentLobbyId.m_SteamID) return;
            RebuildMembers();
            OnMembersChanged?.Invoke();
        }

        private void OnLobbyDataUpdateCb(LobbyDataUpdate_t evt)
        {
            if (!IsInLobby || evt.m_ulSteamIDLobby != CurrentLobbyId.m_SteamID) return;
            // When a member updates their LobbyMemberData (e.g. name changed) we
            // can refresh the list to reflect nickname changes.
            RebuildMembers();
            OnMembersChanged?.Invoke();
            TryHandleStartSignal();
        }

        private void OnGameLobbyJoinRequestedCb(GameLobbyJoinRequested_t evt)
        {
            // A friend clicks "Join Game" from the Steam overlay → direct join.
            ModLog.Info($"[SteamLobby] GameLobbyJoinRequested for {evt.m_steamIDLobby.m_SteamID}.");
            _ = JoinById(evt.m_steamIDLobby.m_SteamID);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private void TryHandleStartSignal()
        {
            if (!IsInLobby) return;

            var nonce = SafeLobbyData(CurrentLobbyId, KeyStartNonce);
            if (string.IsNullOrEmpty(nonce) || string.Equals(nonce, _lastStartNonce, StringComparison.Ordinal))
                return;

            var start = new ServerStartGame
            {
                Difficulty = ParseIntLobbyData(KeyStartDifficulty, (int)GameDifficulty.Explorer),
                SkipTutorials = ParseBoolLobbyData(KeyStartSkipTutorials, true),
                SkipPractice = ParseBoolLobbyData(KeyStartSkipPractice, true),
                AssistEnabled = ParseBoolLobbyData(KeyStartAssistEnabled, false),
            };
            RaiseStartSignal(nonce, start);
        }

        private void RaiseStartSignal(string nonce, ServerStartGame start)
        {
            if (string.IsNullOrEmpty(nonce) || string.Equals(nonce, _lastStartNonce, StringComparison.Ordinal))
                return;

            _lastStartNonce = nonce;
            ModLog.Info($"[SteamLobby] Start received nonce={nonce} difficulty={(GameDifficulty)start.Difficulty}.");
            OnStartRequested?.Invoke(start);
        }

        private int ParseIntLobbyData(string key, int fallback)
        {
            var raw = SafeLobbyData(CurrentLobbyId, key);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        private bool ParseBoolLobbyData(string key, bool fallback)
        {
            var raw = SafeLobbyData(CurrentLobbyId, key);
            return bool.TryParse(raw, out var value) ? value : fallback;
        }

        private void RequestJoinCodeLobbyList(bool exactCodeFilter)
        {
            SteamMatchmaking.AddRequestLobbyListStringFilter(KeyCairnApp, "1", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(KeyProtocolVersion, Protocol.Version.ToString(CultureInfo.InvariantCulture), ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(KeyModVersion, Protocol.GameVersion, ELobbyComparison.k_ELobbyComparisonEqual);
            if (exactCodeFilter)
                SteamMatchmaking.AddRequestLobbyListStringFilter(KeyCode, _pendingJoinCode, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(exactCodeFilter ? MaxJoinCodeResults : MaxLobbyBrowserResults);
            SteamMatchmaking.RequestLobbyList();
        }

        private bool TryStartJoinCodeFallbackSearch()
        {
            if (_pendingJoinCodeFallbackScan)
                return false;

            _pendingJoinCodeFallbackScan = true;
            try
            {
                RequestJoinCodeLobbyList(exactCodeFilter: false);
                ModLog.Debug($"[SteamLobby] JoinByCode fallback scan requested for '{_pendingJoinCode}'.");
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Error($"[SteamLobby] JoinByCode fallback scan failed: {ex}");
                return false;
            }
        }

        private bool HasPendingOperation() => _createTcs != null || _joinTcs != null || _listTcs != null;

        private Task<bool> BusyBoolTask(string message)
        {
            OnLobbyError?.Invoke(message);
            return Task.FromResult(false);
        }

        private void BeginOperation(string name)
        {
            _pendingOperationName = name;
            _pendingOperationElapsed = 0f;
        }

        private void ClearOperationIfIdle()
        {
            if (HasPendingOperation()) return;
            _pendingOperationName = "";
            _pendingOperationElapsed = 0f;
        }

        private void CheckOperationTimeouts(float dt)
        {
            if (!HasPendingOperation())
            {
                ClearOperationIfIdle();
                return;
            }

            _pendingOperationElapsed += Math.Max(0f, dt);
            if (_pendingOperationElapsed < LobbyOperationTimeoutSeconds)
                return;

            var name = string.IsNullOrEmpty(_pendingOperationName) ? "Steam lobby operation" : _pendingOperationName;
            var message = $"{name} timed out. Try again in a moment.";

            if (_createTcs != null)
                FailCreate(message);
            if (_joinTcs != null)
                FailJoin(message);
            if (_listTcs != null)
            {
                var tcs = _listTcs;
                _listTcs = null;
                ClearOperationIfIdle();
                OnLobbyError?.Invoke(message);
                tcs.TrySetResult(new List<LobbyEntry>());
            }
        }

        private static bool IsCompatibleLobby(CSteamID lobbyId, out string reason)
        {
            var protocolRaw = SafeLobbyData(lobbyId, KeyProtocolVersion);
            if (!int.TryParse(protocolRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lobbyProtocol)
                || lobbyProtocol != Protocol.Version)
            {
                reason = $"Lobby protocol {protocolRaw} is incompatible with this mod protocol {Protocol.Version}.";
                return false;
            }

            var modVersion = SafeLobbyData(lobbyId, KeyModVersion);
            if (!string.Equals(modVersion, Protocol.GameVersion, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Lobby mod version {modVersion} is incompatible with this mod version {Protocol.GameVersion}.";
                return false;
            }

            reason = "";
            return true;
        }

        private static bool HasUsableSteamId(CSteamID lobbyId)
        {
            // The IDs returned by SteamMatchmaking.GetLobbyByIndex are the authority.
            // On IL2CPP, CSteamID.IsLobby() can return false for an ID that is nonetheless joinable.
            return lobbyId.m_SteamID != 0;
        }

        private static string SafeLobbyData(CSteamID lobbyId, string key)
        {
            if (!HasUsableSteamId(lobbyId)) return "";
            try { return SteamMatchmaking.GetLobbyData(lobbyId, key) ?? ""; }
            catch (Exception exception)
            {
                ModLog.SuppressedException("steam-lobby.read-lobby-data", exception);
                return "";
            }
        }

        private static string DescribeLobbyEnterFailure(uint response)
        {
            if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist)
                return "Lobby is not visible to Steam. Ask the host to use Public visibility or send a Steam invite.";
            if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseNotAllowed)
                return "You are not allowed to join this lobby.";
            if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseFull)
                return "Lobby is full.";
            if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseError)
                return "Steam returned a lobby join error.";
            if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseBanned)
                return "You are banned from this lobby.";
            if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseLimited)
                return "Your Steam account is limited and cannot join this lobby.";
            if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseMemberBlockedYou)
                return "A lobby member has blocked you.";
            if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseYouBlockedMember)
                return "You have blocked a lobby member.";
            if (response == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseRatelimitExceeded)
                return "Steam rate limit exceeded. Try again in a moment.";
            return $"Steam lobby join failed (response {response}).";
        }
    }
}
