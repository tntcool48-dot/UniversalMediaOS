using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    public sealed class MalLibrarySyncService
    {
        private readonly DomainHotSwapper _config;
        private readonly MalPublicListFallbackService _publicFallback;
        private readonly HttpClient _httpClient;
        private readonly MalOAuthService? _oauthService;

        private static readonly string[] ApiStatuses =
        [
            "watching",
            "completed",
            "on_hold",
            "dropped",
            "plan_to_watch"
        ];

        public MalLibrarySyncService(DomainHotSwapper config, MalPublicListFallbackService publicFallback)
            : this(config, publicFallback, new HttpClient())
        {
        }

        public MalLibrarySyncService(
            DomainHotSwapper config,
            MalPublicListFallbackService publicFallback,
            MalOAuthService oauthService)
            : this(config, publicFallback, new HttpClient(), oauthService)
        {
        }

        internal MalLibrarySyncService(
            DomainHotSwapper config,
            MalPublicListFallbackService publicFallback,
            HttpClient httpClient,
            MalOAuthService? oauthService = null)
        {
            _config = config;
            _publicFallback = publicFallback;
            _httpClient = httpClient;
            _oauthService = oauthService;
        }

        public async Task<MalLibrarySyncResult> SyncOAuthAsync(CancellationToken token = default)
        {
            string accessToken = _oauthService != null
                ? await _oauthService.GetValidAccessTokenAsync(token: token)
                : _config.GetSetting("MalOAuthToken");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return new MalLibrarySyncResult(
                    0,
                    false,
                    "Connect MyAnimeList in Settings or use public fallback with a username.",
                    DateTime.UtcNow);
            }

            string apiRoot = _config.GetSetting("MalApiUrl");
            if (string.IsNullOrWhiteSpace(apiRoot))
            {
                apiRoot = "https://api.myanimelist.net";
            }

            var entries = new List<MalLibraryEntry>();
            foreach (string status in ApiStatuses)
            {
                int offset = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (_oauthService != null)
                    {
                        accessToken = await _oauthService.GetValidAccessTokenAsync(token: token);
                    }

                    string fields = Uri.EscapeDataString("list_status,num_episodes,alternative_titles,main_picture");
                    string url = $"{apiRoot.TrimEnd('/')}/v2/users/@me/animelist?status={status}&sort=list_updated_at&limit=100&offset={offset}&fields={fields}";
                    using var response = await SendAuthorizedAsync(url, accessToken, token);
                    string payload = await response.Content.ReadAsStringAsync(token);
                    if (!response.IsSuccessStatusCode)
                    {
                        AppLogger.Log($"MAL OAuth library sync failed for status {status}: {(int)response.StatusCode}", "WARNING");
                        throw new HttpRequestException($"MAL OAuth sync failed for {status}.", null, response.StatusCode);
                    }

                    using var doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                        !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidDataException("MAL returned an incomplete library response.");
                    }
                    if (data.GetArrayLength() == 0)
                    {
                        break;
                    }

                    foreach (var item in data.EnumerateArray())
                    {
                        if (!item.TryGetProperty("node", out var node))
                        {
                            continue;
                        }

                        int malId = TryGetInt(node, "id");
                        if (malId <= 0)
                        {
                            continue;
                        }

                        var statusElement = item.TryGetProperty("list_status", out var listStatus)
                            ? listStatus
                            : default;
                        var alternativeTitles = node.TryGetProperty("alternative_titles", out var alt)
                            ? alt
                            : default;
                        var picture = node.TryGetProperty("main_picture", out var mainPicture)
                            ? mainPicture
                            : default;

                        string title = TryGetString(node, "title");
                        string english = TryGetString(alternativeTitles, "en");
                        string native = TryGetString(alternativeTitles, "ja");

                        entries.Add(new MalLibraryEntry
                        {
                            MalId = malId,
                            DefaultTitle = title,
                            EnglishTitle = string.IsNullOrWhiteSpace(english) ? title : english,
                            NativeTitle = native,
                            ImageUrl = TryGetString(picture, "large", TryGetString(picture, "medium")),
                            ListStatus = NormalizeStatus(TryGetString(statusElement, "status", status)),
                            UserScore = TryGetInt(statusElement, "score"),
                            WatchedEpisodes = TryGetInt(statusElement, "num_episodes_watched"),
                            TotalEpisodes = TryGetInt(node, "num_episodes"),
                            LastSyncedUtc = DateTime.UtcNow
                        });
                    }

                    string? nextUrl = TryGetPagingNext(doc.RootElement);
                    if (string.IsNullOrWhiteSpace(nextUrl))
                    {
                        break;
                    }

                    offset += 100;
                }
            }

            int saved = await SaveEntriesAsync(entries, token);
            return new MalLibrarySyncResult(saved, false, string.Empty, DateTime.UtcNow);
        }

        public async Task<MalLibrarySyncResult> SyncPublicFallbackAsync(string username, CancellationToken token = default)
        {
            string name = string.IsNullOrWhiteSpace(username)
                ? _config.GetSetting("VaDetectPublicMalFallbackUsername")
                : username.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return new MalLibrarySyncResult(0, true, "Enter a MAL username before using public fallback.", DateTime.UtcNow);
            }

            var entries = await _publicFallback.FetchAsync(name, token);
            int saved = await SaveEntriesAsync(entries, token);
            _config.SetSetting("VaDetectPublicMalFallbackUsername", name);
            return new MalLibrarySyncResult(
                saved,
                true,
                "Public MAL fallback was used. It can fail for private lists, rate limits, or MAL page/API changes.",
                DateTime.UtcNow);
        }

        public async Task<IReadOnlyList<MalLibraryEntry>> GetLibraryAsync(CancellationToken token = default)
        {
            await using var db = new DatabaseContext();
            db.EnsureVaDetectSchema();
            return await db.MalLibraryEntries
                .OrderBy(entry => entry.DefaultTitle)
                .ToListAsync(token);
        }

        private static async Task<int> SaveEntriesAsync(IEnumerable<MalLibraryEntry> entries, CancellationToken token)
        {
            var distinctEntries = entries
                .Where(entry => entry.MalId > 0)
                .GroupBy(entry => entry.MalId)
                .Select(group => group.First())
                .ToArray();

            await using var db = new DatabaseContext();
            db.EnsureVaDetectSchema();
            foreach (var entry in distinctEntries)
            {
                var existing = await db.MalLibraryEntries.FirstOrDefaultAsync(item => item.MalId == entry.MalId, token);
                if (existing == null)
                {
                    await db.MalLibraryEntries.AddAsync(entry, token);
                    continue;
                }

                existing.AniListId = entry.AniListId > 0 ? entry.AniListId : existing.AniListId;
                existing.DefaultTitle = entry.DefaultTitle;
                existing.EnglishTitle = entry.EnglishTitle;
                existing.NativeTitle = entry.NativeTitle;
                existing.ImageUrl = entry.ImageUrl;
                existing.ListStatus = entry.ListStatus;
                existing.UserScore = entry.UserScore;
                existing.WatchedEpisodes = entry.WatchedEpisodes;
                existing.TotalEpisodes = entry.TotalEpisodes;
                existing.LastSyncedUtc = entry.LastSyncedUtc;
            }

            await db.SaveChangesAsync(token);
            return distinctEntries.Length;
        }

        private static string NormalizeStatus(string status)
        {
            return (status ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "completed" => "Completed",
                "watching" => "Watching",
                "on_hold" => "On Hold",
                "dropped" => "Dropped",
                "plan_to_watch" => "Plan to Watch",
                _ => string.IsNullOrWhiteSpace(status) ? "Unknown" : status.Replace('_', ' ')
            };
        }

        private static string? TryGetPagingNext(JsonElement root)
        {
            return root.TryGetProperty("paging", out var paging) &&
                   paging.ValueKind == JsonValueKind.Object &&
                   paging.TryGetProperty("next", out var next) &&
                   next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        }

        private async Task<HttpResponseMessage> SendAuthorizedAsync(string url, string accessToken, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await _httpClient.SendAsync(request, token);
            if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized || _oauthService == null)
            {
                return response;
            }

            response.Dispose();
            string refreshedToken = await _oauthService.GetValidAccessTokenAsync(forceRefresh: true, token);
            using var retryRequest = new HttpRequestMessage(HttpMethod.Get, url);
            retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken);
            return await _httpClient.SendAsync(retryRequest, token);
        }

        private static string TryGetString(JsonElement element, string propertyName, string fallback = "")
        {
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(propertyName, out var property) &&
                   property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? fallback
                : fallback;
        }

        private static int TryGetInt(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var property))
            {
                return 0;
            }

            return property.ValueKind switch
            {
                JsonValueKind.Number when property.TryGetInt32(out int value) => value,
                JsonValueKind.String when int.TryParse(property.GetString(), out int value) => value,
                _ => 0
            };
        }
    }
}
