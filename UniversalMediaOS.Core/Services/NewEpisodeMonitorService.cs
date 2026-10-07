using System.Text;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    public sealed class NewEpisodeMonitorService
    {
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private readonly DomainHotSwapper _config;
        private readonly FavoriteMediaService _favorites;

        public NewEpisodeMonitorService(DomainHotSwapper config, FavoriteMediaService favorites)
        {
            _config = config;
            _favorites = favorites;
        }

        public async Task<IReadOnlyList<NewEpisodeAlert>> CheckAsync(CancellationToken token = default)
        {
            if (_config.GetSetting("NewEpisodeAlerts") == "false")
            {
                return Array.Empty<NewEpisodeAlert>();
            }

            int[] ids = _favorites.GetFavorites()
                .Where(item => item.AniListId > 0)
                .Select(item => item.AniListId)
                .Distinct()
                .ToArray();
            if (ids.Length == 0)
            {
                return Array.Empty<NewEpisodeAlert>();
            }

            string endpoint = _config.GetSetting("AniListUrl");
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                endpoint = "https://graphql.anilist.co";
            }

            var available = new Dictionary<int, FavoriteMediaAvailability>();
            foreach (int[] batch in ids.Chunk(50))
            {
                token.ThrowIfCancellationRequested();
                const string query = """
                    query ($ids: [Int]) {
                      Page(page: 1, perPage: 50) {
                        media(id_in: $ids, type: ANIME) {
                          id
                          episodes
                          status
                          nextAiringEpisode { episode }
                        }
                      }
                    }
                    """;
                string body = JsonSerializer.Serialize(new { query, variables = new { ids = batch } });
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                using var response = await HttpClient.SendAsync(request, token);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);

                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    (root.TryGetProperty("errors", out var errors) &&
                     (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() > 0)) ||
                    !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty("Page", out var page) || page.ValueKind != JsonValueKind.Object ||
                    !page.TryGetProperty("media", out var media) ||
                    media.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("AniList returned an incomplete episode availability response.");
                }

                foreach (var item in media.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("AniList returned an incomplete episode availability item.");
                    int id = ReadInt(item, "id");
                    if (id <= 0 || !batch.Contains(id)) continue;
                    string status = item.TryGetProperty("status", out var statusProperty) &&
                                    statusProperty.ValueKind == JsonValueKind.String
                        ? statusProperty.GetString() ?? string.Empty
                        : string.Empty;
                    int nextEpisode = item.TryGetProperty("nextAiringEpisode", out var nextAiring) &&
                                      nextAiring.ValueKind == JsonValueKind.Object
                        ? ReadInt(nextAiring, "episode")
                        : 0;
                    int totalEpisodes = ReadInt(item, "episodes");
                    int released = nextEpisode > 0
                        ? Math.Max(0, nextEpisode - 1)
                        : status.Equals("FINISHED", StringComparison.OrdinalIgnoreCase)
                            ? totalEpisodes
                            : 0;
                    available[id] = new FavoriteMediaAvailability(released, FormatStatus(status));
                }
            }

            token.ThrowIfCancellationRequested();
            return _favorites.UpdateAvailability(available);
        }

        public async Task<IReadOnlyList<NewEpisodeAlert>> CheckSafelyAsync(CancellationToken token = default)
        {
            try
            {
                return await CheckAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"New-episode check failed: {ex.Message}", "WARNING");
                return Array.Empty<NewEpisodeAlert>();
            }
        }

        private static int ReadInt(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) &&
                   property.ValueKind == JsonValueKind.Number &&
                   property.TryGetInt32(out int value)
                ? value
                : 0;
        }

        private static string FormatStatus(string status)
        {
            return status.Trim().ToUpperInvariant() switch
            {
                "RELEASING" => "Releasing",
                "FINISHED" => "Finished",
                "NOT_YET_RELEASED" => "Upcoming",
                "CANCELLED" => "Cancelled",
                "HIATUS" => "Hiatus",
                _ => string.Empty
            };
        }
    }
}
