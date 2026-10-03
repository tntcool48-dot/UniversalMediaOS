using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Services
{
    public class MangaSearchResult
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string CoverUrl { get; set; } = string.Empty;
        public string Year { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string GenreOne { get; set; } = string.Empty;
        public string GenreTwo { get; set; } = string.Empty;
        public string MetadataLine { get; set; } = string.Empty;
        public string GenreLine { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ContentRating { get; set; } = string.Empty;
        public List<string> AlternateTitles { get; set; } = new();
    }

    public class MangaChapter
    {
        public string Id { get; set; } = string.Empty;
        public string ChapterNumber { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int Pages { get; set; }
        public string ExternalUrl { get; set; } = string.Empty;
    }

    public class MangaService
    {
        private static readonly HttpClient _httpClient = CreateHttpClient();
        private readonly UniversalMediaOS.Core.Configuration.DomainHotSwapper? _config;
        private readonly string _mangaDexUrl;
        private readonly string _mangaDexCoversUrl;

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };

            // MangaDex is a JSON API, not a browser navigation. Impersonating a
            // specific Chrome release and attaching Origin/Sec-Fetch headers makes
            // the request look like an inconsistent CORS request and has caused
            // MangaDex's edge protection to reject otherwise valid calls.
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return client;
        }

        internal static HttpClient CreateHttpClientForTesting() => CreateHttpClient();

        public MangaService(UniversalMediaOS.Core.Configuration.DomainHotSwapper? config = null)
        {
            _config = config;
            _mangaDexUrl = config?.GetSetting("MangaDexUrl") ?? "https://api.mangadex.org";
            if (string.IsNullOrEmpty(_mangaDexUrl)) _mangaDexUrl = "https://api.mangadex.org";

            _mangaDexCoversUrl = config?.GetSetting("MangaDexCoversUrl") ?? "https://uploads.mangadex.org";
            if (string.IsNullOrEmpty(_mangaDexCoversUrl)) _mangaDexCoversUrl = "https://uploads.mangadex.org";
        }

        public virtual async Task<List<MangaSearchResult>> SearchMangaAsync(string query, CancellationToken token = default)
        {
            var results = new List<MangaSearchResult>();
            if (string.IsNullOrWhiteSpace(query)) return await GetRecommendedMangaAsync(token);

            try
            {
                string url = $"{_mangaDexUrl.TrimEnd('/')}/manga?title={Uri.EscapeDataString(query.Trim())}&limit=20&availableTranslatedLanguage[]=en&{BuildContentRatingQuery()}&includes[]=cover_art&order[relevance]=desc";
                using var response = await _httpClient.GetAsync(url, token);
                if (!response.IsSuccessStatusCode) return results;

                string json = await response.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(json);
                results = RankSearchResults(ParseMangaResults(doc), query);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Manga Search Error: {ex.Message}");
            }

            return results;
        }

        public virtual async Task<List<MangaSearchResult>> GetRecommendedMangaAsync(CancellationToken token = default)
        {
            var results = new List<MangaSearchResult>();

            try
            {
                string url = $"{_mangaDexUrl.TrimEnd('/')}/manga?limit=24&availableTranslatedLanguage[]=en&{BuildContentRatingQuery()}&includes[]=cover_art&hasAvailableChapters=true&order[followedCount]=desc";
                using var response = await _httpClient.GetAsync(url, token);
                if (!response.IsSuccessStatusCode) return results;

                string json = await response.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(json);
                results = ParseMangaResults(doc);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Manga Recommendations Error: {ex.Message}");
            }

            return results;
        }

        private List<MangaSearchResult> ParseMangaResults(JsonDocument doc)
        {
            var results = new List<MangaSearchResult>();

            if (!doc.RootElement.TryGetProperty("data", out var dataArray) || dataArray.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in dataArray.EnumerateArray())
            {
                string id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                string title = "Unknown";
                string year = string.Empty;
                string status = string.Empty;
                string description = string.Empty;
                string contentRating = string.Empty;
                var tags = new List<string>();
                var alternateTitles = new List<string>();

                if (item.TryGetProperty("attributes", out var attrs))
                {
                    if (attrs.TryGetProperty("title", out var titleObj))
                    {
                        title = GetLocalizedText(titleObj, title);
                    }

                    if (attrs.TryGetProperty("year", out var yearProp))
                    {
                        year = yearProp.ValueKind == JsonValueKind.Number
                            ? yearProp.GetInt32().ToString()
                            : yearProp.GetString() ?? string.Empty;
                    }

                    if (attrs.TryGetProperty("status", out var statusProp))
                    {
                        status = FormatMangaStatus(statusProp.GetString() ?? string.Empty);
                    }

                    if (attrs.TryGetProperty("description", out var descriptionObj))
                    {
                        description = GetLocalizedText(descriptionObj, string.Empty);
                    }

                    if (attrs.TryGetProperty("altTitles", out var altTitlesObj) &&
                        altTitlesObj.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var altTitle in altTitlesObj.EnumerateArray())
                        {
                            string alternateTitle = GetLocalizedText(altTitle, string.Empty);
                            if (!string.IsNullOrWhiteSpace(alternateTitle))
                            {
                                alternateTitles.Add(alternateTitle);
                            }
                        }
                    }

                    if (attrs.TryGetProperty("contentRating", out var contentRatingProp))
                    {
                        contentRating = contentRatingProp.GetString() ?? string.Empty;
                    }

                    if (!ShowAdultContent &&
                        (contentRating.Equals("erotica", StringComparison.OrdinalIgnoreCase) ||
                         contentRating.Equals("pornographic", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    tags = GetTags(attrs, 2);
                }

                string coverFileName = GetCoverFileName(item);
                string coverUrl = string.Empty;
                if (!string.IsNullOrEmpty(coverFileName) && !string.IsNullOrEmpty(id))
                {
                    coverUrl = $"{_mangaDexCoversUrl.TrimEnd('/')}/covers/{id}/{coverFileName}.512.jpg";
                }

                results.Add(new MangaSearchResult
                {
                    Id = id,
                    Title = title,
                    CoverUrl = coverUrl,
                    Year = year,
                    Status = status,
                    GenreOne = tags.ElementAtOrDefault(0) ?? string.Empty,
                    GenreTwo = tags.ElementAtOrDefault(1) ?? string.Empty,
                    MetadataLine = string.Join(" · ", new[] { status, year }.Where(value => !string.IsNullOrWhiteSpace(value))),
                    GenreLine = string.Join(" · ", tags.Where(value => !string.IsNullOrWhiteSpace(value))),
                    Description = description,
                    ContentRating = contentRating,
                    AlternateTitles = alternateTitles
                });
            }

            return results;
        }

        private static List<MangaSearchResult> RankSearchResults(List<MangaSearchResult> results, string query)
        {
            string normalizedQuery = NormalizeTitle(query);
            if (string.IsNullOrWhiteSpace(normalizedQuery))
            {
                return results;
            }

            return results
                .Select((result, index) => new
                {
                    Result = result,
                    Index = index,
                    Score = ScoreSearchResult(result, normalizedQuery)
                })
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Index)
                .Select(item => item.Result)
                .ToList();
        }

        internal static List<MangaSearchResult> RankSearchResultsForTesting(List<MangaSearchResult> results, string query) =>
            RankSearchResults(results, query);

        private static int ScoreSearchResult(MangaSearchResult result, string normalizedQuery)
        {
            var titles = new[] { result.Title }
                .Concat(result.AlternateTitles ?? Enumerable.Empty<string>())
                .Select(NormalizeTitle)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .ToList();

            if (titles.Any(title => title.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase)))
            {
                return 1000;
            }

            if (titles.Any(title => title.StartsWith(normalizedQuery + " ", StringComparison.OrdinalIgnoreCase)))
            {
                return 800;
            }

            if (titles.Any(title => title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(word => word.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase))))
            {
                return 650;
            }

            if (titles.Any(title => title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)))
            {
                return 500;
            }

            return 0;
        }

        private static string NormalizeTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return System.Text.RegularExpressions.Regex
                .Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ")
                .Trim();
        }

        private static string GetLocalizedText(JsonElement localized, string fallback)
        {
            if (localized.ValueKind != JsonValueKind.Object)
            {
                return fallback;
            }

            if (localized.TryGetProperty("en", out var enProp) && enProp.ValueKind == JsonValueKind.String)
            {
                return enProp.GetString() ?? fallback;
            }

            foreach (var prop in localized.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    return prop.Value.GetString() ?? fallback;
                }
            }

            return fallback;
        }

        private static string GetCoverFileName(JsonElement item)
        {
            if (!item.TryGetProperty("relationships", out var rels) || rels.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            foreach (var rel in rels.EnumerateArray())
            {
                if (rel.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "cover_art" &&
                    rel.TryGetProperty("attributes", out var coverAttrs) &&
                    coverAttrs.TryGetProperty("fileName", out var fileProp))
                {
                    return fileProp.GetString() ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static List<string> GetTags(JsonElement attrs, int maxCount)
        {
            var tags = new List<string>();

            if (!attrs.TryGetProperty("tags", out var tagArray) || tagArray.ValueKind != JsonValueKind.Array)
            {
                return tags;
            }

            foreach (var tag in tagArray.EnumerateArray())
            {
                if (!tag.TryGetProperty("attributes", out var tagAttrs) ||
                    !tagAttrs.TryGetProperty("name", out var nameObj))
                {
                    continue;
                }

                string name = GetLocalizedText(nameObj, string.Empty);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    tags.Add(name);
                }

                if (tags.Count >= maxCount)
                {
                    break;
                }
            }

            return tags;
        }

        private static string FormatMangaStatus(string status)
        {
            return status switch
            {
                "ongoing" => "Ongoing",
                "completed" => "Completed",
                "hiatus" => "Hiatus",
                "cancelled" => "Cancelled",
                _ => status
            };
        }

        private string BuildContentRatingQuery()
        {
            var ratings = ShowAdultContent
                ? new[] { "safe", "suggestive", "erotica", "pornographic" }
                : new[] { "safe", "suggestive" };

            return string.Join("&", ratings.Select(rating => $"contentRating[]={Uri.EscapeDataString(rating)}"));
        }

        private bool ShowAdultContent => _config?.GetSetting("ShowAdultContent") == "true";

        public virtual async Task<List<MangaChapter>> GetChaptersAsync(string mangaId, CancellationToken token = default)
        {
            var chapters = new List<MangaChapter>();
            if (string.IsNullOrEmpty(mangaId)) return chapters;

            try
            {
                int offset = 0;
                int total = 1;

                while (offset < total)
                {
                    token.ThrowIfCancellationRequested();

                    string url = $"{_mangaDexUrl.TrimEnd('/')}/manga/{mangaId}/feed?translatedLanguage[]=en&limit=100&offset={offset}&order[chapter]=asc";
                    using var response = await _httpClient.GetAsync(url, token);
                    if (!response.IsSuccessStatusCode) break;

                    string json = await response.Content.ReadAsStringAsync(token);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("total", out var totalProp) && totalProp.ValueKind == JsonValueKind.Number)
                    {
                        total = totalProp.GetInt32();
                    }

                    if (doc.RootElement.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
                    {
                        if (dataArray.GetArrayLength() == 0) break;

                        foreach (var item in dataArray.EnumerateArray())
                        {
                            string id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                            string chNum = "";
                            string chTitle = "";
                            string extUrl = "";
                            int pages = 0;

                            if (item.TryGetProperty("attributes", out var attrs))
                            {
                                chNum = attrs.TryGetProperty("chapter", out var numProp) ? numProp.GetString() ?? "" : "";
                                chTitle = attrs.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                                extUrl = attrs.TryGetProperty("externalUrl", out var extProp) && extProp.ValueKind == JsonValueKind.String
                                    ? NormalizeExternalReaderUrl(extProp.GetString())
                                    : string.Empty;
                                pages = attrs.TryGetProperty("pages", out var pProp) && pProp.ValueKind == JsonValueKind.Number ? pProp.GetInt32() : 0;
                            }

                            if (!string.IsNullOrEmpty(id))
                            {
                                chapters.Add(new MangaChapter
                                {
                                    Id = id,
                                    ChapterNumber = string.IsNullOrEmpty(chNum) ? "0" : chNum,
                                    Title = string.IsNullOrEmpty(chTitle) ? $"Chapter {chNum}" : chTitle,
                                    Pages = pages,
                                    ExternalUrl = extUrl
                                });
                            }
                        }
                    }
                    else
                    {
                        break;
                    }

                    // MangaDex rate limit: ~5 req/s. Pause between paginated requests to avoid HTTP 429.
                    if (offset + 100 < total)
                    {
                        await Task.Delay(250, token);
                    }
                    offset += 100;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Manga Chapters Error: {ex.Message}");
            }

            return chapters
                .GroupBy(
                    chapter => chapter.ChapterNumber == "0" ? $"id:{chapter.Id}" : chapter.ChapterNumber,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(chapter => chapter.Pages > 0)
                    .ThenByDescending(chapter => string.IsNullOrWhiteSpace(chapter.ExternalUrl))
                    .First())
                .OrderBy(chapter => ParseChapterNumber(chapter.ChapterNumber))
                .ThenBy(chapter => chapter.ChapterNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public virtual async Task<List<string>> GetPageUrlsAsync(string chapterId, CancellationToken token = default)
        {
            var pages = new List<string>();
            if (string.IsNullOrEmpty(chapterId)) return pages;

            try
            {
                string url = $"{_mangaDexUrl.TrimEnd('/')}/at-home/server/{chapterId}";
                using var response = await _httpClient.GetAsync(url, token);
                if (!response.IsSuccessStatusCode) return pages;

                string json = await response.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(json);

                var root = doc.RootElement;
                if (root.TryGetProperty("baseUrl", out var baseUrlProp) && 
                    root.TryGetProperty("chapter", out var chObj))
                {
                    string baseUrl = baseUrlProp.GetString() ?? "";
                    string hash = chObj.TryGetProperty("hash", out var hashProp) ? hashProp.GetString() ?? "" : "";
                    
                    if (chObj.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var page in dataArray.EnumerateArray())
                        {
                            string fileName = page.GetString() ?? "";
                            if (!string.IsNullOrEmpty(fileName))
                            {
                                pages.Add($"{baseUrl}/data/{hash}/{fileName}");
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Manga Pages Error: {ex.Message}");
            }

            return pages;
        }

        private static decimal ParseChapterNumber(string value)
        {
            return decimal.TryParse(
                value,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out decimal parsed)
                ? parsed
                : decimal.MaxValue;
        }

        internal static string NormalizeExternalReaderUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                return string.Empty;
            }

            return uri.AbsoluteUri;
        }
    }
}
