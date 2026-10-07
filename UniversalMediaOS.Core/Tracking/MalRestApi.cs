using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Services;

namespace UniversalMediaOS.Core.Tracking
{
    public class MalRestApi
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly SemaphoreSlim AutomaticProgressGate = new(1, 1);
        private readonly string _accessToken;
        private readonly string _malApiUrl;
        private readonly MalOAuthService? _oauthService;

        public MalRestApi(string accessToken, DomainHotSwapper? config = null)
        {
            _accessToken = accessToken ?? string.Empty;
            _malApiUrl = config?.GetSetting("MalApiUrl") ?? "https://api.myanimelist.net";
        }

        public MalRestApi(MalOAuthService oauthService, DomainHotSwapper? config = null)
        {
            _oauthService = oauthService ?? throw new ArgumentNullException(nameof(oauthService));
            _accessToken = string.Empty;
            _malApiUrl = config?.GetSetting("MalApiUrl") ?? "https://api.myanimelist.net";
        }

        public async Task<MalAnimeStatus?> GetAnimeStatusAsync(int animeId, CancellationToken token = default)
        {
            string accessToken = await ResolveAccessTokenAsync(forceRefresh: false, token);
            if (string.IsNullOrEmpty(accessToken) || animeId <= 0)
            {
                return null;
            }

            try
            {
                string fields = Uri.EscapeDataString("num_episodes,my_list_status{status,num_episodes_watched,is_rewatching,num_times_rewatched,rewatch_value}");
                using var response = await SendAuthenticatedAsync(
                    currentToken =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, $"{_malApiUrl.TrimEnd('/')}/v2/anime/{animeId}?fields={fields}");
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", currentToken);
                        return request;
                    },
                    token);
                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"MAL status fetch failed: {response.StatusCode}");
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !TryReadNonNegativeInt(root, "num_episodes", out int total))
                    return null;
                if (root.TryGetProperty("id", out _) &&
                    (!TryReadNonNegativeInt(root, "id", out int returnedId) || returnedId != animeId)) return null;

                var status = new MalAnimeStatus
                {
                    AnimeId = animeId,
                    TotalEpisodes = total
                };

                if (root.TryGetProperty("my_list_status", out var listStatus) && listStatus.ValueKind != JsonValueKind.Null)
                {
                    if (listStatus.ValueKind != JsonValueKind.Object ||
                        (!TryReadNonNegativeInt(listStatus, "num_episodes_watched", out int watched) &&
                         !TryReadNonNegativeInt(listStatus, "num_watched_episodes", out watched))) return null;
                    status.Status = TryGetString(listStatus, "status");
                    status.WatchedEpisodes = watched;
                    status.IsRewatching = TryGetBool(listStatus, "is_rewatching");
                    status.NumTimesRewatched = TryGetInt(listStatus, "num_times_rewatched");
                    status.RewatchValue = TryGetInt(listStatus, "rewatch_value");
                }

                return status;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MAL status fetch failed: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> UpdateProgressAsync(int animeId, int numWatchedEpisodes, CancellationToken token = default,
            bool preserveHigherProgress = false)
        {
            if (!preserveHigherProgress)
                return await UpdateProgressCoreAsync(animeId, numWatchedEpisodes, false, token);

            // Automatic updates from independent players must read and write in
            // order so an older episode cannot overwrite a newer accepted value.
            await AutomaticProgressGate.WaitAsync(token);
            try { return await UpdateProgressCoreAsync(animeId, numWatchedEpisodes, true, token); }
            finally { AutomaticProgressGate.Release(); }
        }

        private async Task<bool> UpdateProgressCoreAsync(int animeId, int numWatchedEpisodes, bool preserveHigherProgress,
            CancellationToken token)
        {
            string accessToken = await ResolveAccessTokenAsync(forceRefresh: false, token);
            if (string.IsNullOrEmpty(accessToken))
            {
                System.Diagnostics.Debug.WriteLine("MAL Update failed: Access token is null or empty.");
                return false;
            }

            try
            {
                var currentStatus = await GetAnimeStatusAsync(animeId, token);
                if (preserveHigherProgress)
                {
                    if (currentStatus == null) return false;
                    if (!currentStatus.IsRewatching && currentStatus.WatchedEpisodes >= numWatchedEpisodes) return true;
                }
                string? nextStatus = ResolveStatusForProgressUpdate(currentStatus);

                var form = new List<KeyValuePair<string, string>>
                {
                    new("num_watched_episodes", Math.Max(0, numWatchedEpisodes).ToString(CultureInfo.InvariantCulture))
                };

                if (!string.IsNullOrWhiteSpace(nextStatus))
                {
                    form.Add(new KeyValuePair<string, string>("status", nextStatus));
                }

                if (currentStatus?.IsRewatching == true)
                {
                    form.Add(new KeyValuePair<string, string>("is_rewatching", "true"));
                }

                using var response = await SendAuthenticatedAsync(
                    currentToken =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Put, $"{_malApiUrl.TrimEnd('/')}/v2/anime/{animeId}/my_list_status");
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", currentToken);
                        request.Content = new FormUrlEncodedContent(form);
                        return request;
                    },
                    token);
                return response.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MAL Update failed: {ex.Message}");
                return false;
            }
        }

        private async Task<HttpResponseMessage> SendAuthenticatedAsync(
            Func<string, HttpRequestMessage> createRequest,
            CancellationToken token)
        {
            string accessToken = await ResolveAccessTokenAsync(forceRefresh: false, token);
            using var request = createRequest(accessToken);
            var response = await _httpClient.SendAsync(request, token);
            if (response.StatusCode != HttpStatusCode.Unauthorized || _oauthService == null)
            {
                return response;
            }

            response.Dispose();
            string refreshedToken = await ResolveAccessTokenAsync(forceRefresh: true, token);
            using var retryRequest = createRequest(refreshedToken);
            return await _httpClient.SendAsync(retryRequest, token);
        }

        private async Task<string> ResolveAccessTokenAsync(bool forceRefresh, CancellationToken token)
        {
            return _oauthService != null
                ? await _oauthService.GetValidAccessTokenAsync(forceRefresh, token)
                : _accessToken;
        }

        private static string? ResolveStatusForProgressUpdate(MalAnimeStatus? currentStatus)
        {
            if (currentStatus == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(currentStatus.Status) ||
                string.Equals(currentStatus.Status, "plan_to_watch", StringComparison.OrdinalIgnoreCase))
            {
                return "watching";
            }

            // Preserve completed/rewatching/on-hold list state. Rewatching is carried as its own flag,
            // so forcing "watching" here can unintentionally damage a user's MAL list.
            return currentStatus.Status;
        }

        private static bool TryReadNonNegativeInt(JsonElement element, string name, out int value)
        {
            value = 0;
            if (!element.TryGetProperty(name, out var property)) return false;
            bool parsed = property.ValueKind == JsonValueKind.Number ? property.TryGetInt32(out value)
                : property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            return parsed && value >= 0;
        }

        private static int TryGetInt(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return 0;
            }

            return property.ValueKind switch
            {
                JsonValueKind.Number when property.TryGetInt32(out int value) => value,
                JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) => value,
                _ => 0
            };
        }

        private static string TryGetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        private static bool TryGetBool(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            return property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(property.GetString(), out bool value) => value,
                _ => false
            };
        }
    }

    public class MalAnimeStatus
    {
        public int AnimeId { get; set; }
        public int TotalEpisodes { get; set; }
        public int WatchedEpisodes { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool IsRewatching { get; set; }
        public int NumTimesRewatched { get; set; }
        public int RewatchValue { get; set; }
    }
}
