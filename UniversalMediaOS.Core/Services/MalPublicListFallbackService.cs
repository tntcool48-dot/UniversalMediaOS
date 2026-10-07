using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    public sealed class MalPublicListFallbackService
    {
        private const string DesktopUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";

        private readonly HttpClient _httpClient;

        private static readonly IReadOnlyDictionary<int, string> StatusMap = new Dictionary<int, string>
        {
            [1] = "Watching",
            [2] = "Completed",
            [3] = "On Hold",
            [4] = "Dropped",
            [6] = "Plan to Watch"
        };

        public MalPublicListFallbackService()
            : this(new HttpClient())
        {
        }

        internal MalPublicListFallbackService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<IReadOnlyList<MalLibraryEntry>> FetchAsync(string username, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return Array.Empty<MalLibraryEntry>();
            }

            var entries = new List<MalLibraryEntry>();
            foreach (var (statusCode, statusName) in StatusMap)
            {
                int offset = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    string url = $"https://myanimelist.net/animelist/{Uri.EscapeDataString(username.Trim())}/load.json?status={statusCode}&offset={offset}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.UserAgent.ParseAdd(DesktopUserAgent);
                    using var response = await _httpClient.SendAsync(request, token);
                    if (!response.IsSuccessStatusCode)
                    {
                        AppLogger.Log($"MAL public fallback failed for status {statusName}: {(int)response.StatusCode}", "WARNING");
                        throw new HttpRequestException("MAL public list could not be read.", null, response.StatusCode);
                    }

                    string json = await response.Content.ReadAsStringAsync(token);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidDataException("MAL returned an incomplete public list response.");
                    }
                    if (doc.RootElement.GetArrayLength() == 0)
                    {
                        break;
                    }

                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        int malId = TryGetInt(item, "anime_id");
                        if (malId <= 0)
                        {
                            continue;
                        }

                        string defaultTitle = TryGetString(item, "anime_title");
                        string englishTitle = TryGetString(item, "anime_title_eng");
                        entries.Add(new MalLibraryEntry
                        {
                            MalId = malId,
                            DefaultTitle = defaultTitle,
                            EnglishTitle = string.IsNullOrWhiteSpace(englishTitle) ? defaultTitle : englishTitle,
                            NativeTitle = string.Empty,
                            ImageUrl = TryGetString(item, "anime_image_path"),
                            ListStatus = statusName,
                            UserScore = TryGetInt(item, "score"),
                            WatchedEpisodes = Math.Max(TryGetInt(item, "num_watched_episodes"), TryGetInt(item, "watched_episodes")),
                            TotalEpisodes = TryGetInt(item, "anime_num_episodes"),
                            LastSyncedUtc = DateTime.UtcNow
                        });
                    }

                    offset += 300;
                    await Task.Delay(TimeSpan.FromMilliseconds(750), token);
                }
            }

            return entries;
        }

        private static string TryGetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
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
                JsonValueKind.String when int.TryParse(property.GetString(), out int value) => value,
                _ => 0
            };
        }
    }
}
