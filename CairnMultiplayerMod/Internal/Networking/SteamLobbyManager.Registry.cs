using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;

namespace CairnMultiplayerMod.Internal.Networking
{
    internal sealed partial class SteamLobbyManager
    {
        private const float RegistryHeartbeatSeconds = 30f;
        private static readonly HttpClient RegistryHttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };

        private string _registryToken = "";
        private string _registeredLobbyId = "";
        private float _registryHeartbeatElapsed;
        private bool _registryRequestInFlight;

        private sealed class LobbyRegistryRequest
        {
            [JsonPropertyName("lobby_id")] public string LobbyId { get; init; } = "";
            [JsonPropertyName("name")] public string Name { get; init; } = "";
            [JsonPropertyName("host_name")] public string HostName { get; init; } = "";
            [JsonPropertyName("player_count")] public int PlayerCount { get; init; }
            [JsonPropertyName("max_players")] public int MaxPlayers { get; init; }
            [JsonPropertyName("mod_version")] public string ModVersion { get; init; } = "";
            [JsonPropertyName("protocol_version")] public int ProtocolVersion { get; init; }
            [JsonPropertyName("visibility")] public string Visibility { get; init; } = "";
            [JsonPropertyName("registration_token")] public string RegistrationToken { get; init; } = "";
        }

        private sealed class LobbyRegistryResponse
        {
            [JsonPropertyName("registration_token")] public string RegistrationToken { get; init; } = "";
        }

        private static string RegistryEndpoint()
        {
            var baseUrl = Environment.GetEnvironmentVariable("CAIRNMP_API_URL");
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = "https://api.cairnmultiplayer.com";
            return $"{baseUrl.TrimEnd('/')}/v1/lobbies";
        }

        private void StartLobbyRegistry()
        {
            if (!IsHost || !IsInLobby) return;
            _registeredLobbyId = CurrentLobbyId.m_SteamID.ToString(CultureInfo.InvariantCulture);
            _registryToken = "";
            _registryHeartbeatElapsed = RegistryHeartbeatSeconds;
            RequestLobbyHeartbeat();
        }

        private void PumpLobbyRegistry(float dt)
        {
            if (!IsHost || !IsInLobby || string.IsNullOrEmpty(_registeredLobbyId)) return;
            _registryHeartbeatElapsed += Math.Max(0f, dt);
            if (_registryHeartbeatElapsed >= RegistryHeartbeatSeconds)
                RequestLobbyHeartbeat();
        }

        private void RequestLobbyHeartbeat()
        {
            if (_registryRequestInFlight || !IsHost || !IsInLobby) return;

            var visibility = SafeLobbyData(CurrentLobbyId, KeyVisibility) switch
            {
                "Public" => "public",
                "FriendsOnly" => "friends",
                _ => "private",
            };
            var request = new LobbyRegistryRequest
            {
                LobbyId = CurrentLobbyId.m_SteamID.ToString(CultureInfo.InvariantCulture),
                Name = string.IsNullOrWhiteSpace(CurrentLobbyName) ? "CairnMP lobby" : CurrentLobbyName,
                HostName = string.IsNullOrWhiteSpace(LocalPersonaName) ? "Player" : LocalPersonaName,
                PlayerCount = Math.Max(1, _members.Count),
                MaxPlayers = Math.Max(2, Math.Min(MaxMembers, 16)),
                ModVersion = ModVersion(),
                ProtocolVersion = Protocol.Version,
                Visibility = visibility,
                RegistrationToken = _registryToken,
            };
            _registryHeartbeatElapsed = 0f;
            _registryRequestInFlight = true;
            _ = SendLobbyHeartbeatAsync(request);
        }

        private async Task SendLobbyHeartbeatAsync(LobbyRegistryRequest request)
        {
            try
            {
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await RegistryHttpClient.PostAsync(RegistryEndpoint(), content).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var result = JsonSerializer.Deserialize<LobbyRegistryResponse>(body);
                    if (!string.IsNullOrWhiteSpace(result?.RegistrationToken))
                    {
                        if (string.Equals(_registeredLobbyId, request.LobbyId, StringComparison.Ordinal))
                            _registryToken = result.RegistrationToken;
                        else
                            _ = DeleteLobbyRegistrationAsync(request.LobbyId, result.RegistrationToken);
                    }
                    return;
                }

                if (response.StatusCode != HttpStatusCode.Conflict)
                    ModLog.Debug($"[LobbyRegistry] Heartbeat rejected ({(int)response.StatusCode}).");
            }
            catch (Exception exception)
            {
                ModLog.Debug($"[LobbyRegistry] Heartbeat unavailable: {exception.Message}");
            }
            finally
            {
                _registryRequestInFlight = false;
            }
        }

        private void StopLobbyRegistry()
        {
            var lobbyId = _registeredLobbyId;
            var token = _registryToken;
            _registeredLobbyId = "";
            _registryToken = "";
            _registryHeartbeatElapsed = 0f;
            if (!string.IsNullOrEmpty(lobbyId) && !string.IsNullOrEmpty(token))
                _ = DeleteLobbyRegistrationAsync(lobbyId, token);
        }

        private static async Task DeleteLobbyRegistrationAsync(string lobbyId, string token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"{RegistryEndpoint()}/{lobbyId}");
                request.Headers.TryAddWithoutValidation("X-Lobby-Token", token);
                using var response = await RegistryHttpClient.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Unauthorized)
                    ModLog.Debug($"[LobbyRegistry] Delete rejected ({(int)response.StatusCode}).");
            }
            catch (Exception exception)
            {
                ModLog.Debug($"[LobbyRegistry] Delete unavailable: {exception.Message}");
            }
        }
    }
}
