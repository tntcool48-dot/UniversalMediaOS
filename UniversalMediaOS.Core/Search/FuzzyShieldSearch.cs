using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Configuration;

namespace UniversalMediaOS.Core.Search
{
    public class FuzzyShieldSearch
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        private static readonly System.Text.RegularExpressions.Regex _htmlRegex = 
            new System.Text.RegularExpressions.Regex("<[^>]*>", System.Text.RegularExpressions.RegexOptions.Compiled);

        private readonly string _aniListUrl;
        private readonly DomainHotSwapper? _config;
        private readonly HttpClient _requestClient = _httpClient;

        static FuzzyShieldSearch()
        {
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalMediaOS/1.0");
        }

        public FuzzyShieldSearch(DomainHotSwapper config)
        {
            _config = config;
            _aniListUrl = config.GetSetting("AniListUrl") ?? "";
            if (string.IsNullOrEmpty(_aniListUrl)) _aniListUrl = "https://graphql.anilist.co";
        }

        internal FuzzyShieldSearch(DomainHotSwapper config, HttpClient client) : this(config)
        {
            _requestClient = client;
        }

        public FuzzyShieldSearch()
        {
            try
            {
                string appData = UniversalMediaOS.Core.Helpers.AppDataPaths.RoamingBaseDirectory;
                string configPath = Path.Combine(appData, "UniversalMediaOS", "config.json");
                if (File.Exists(configPath))
                {
                    var config = new DomainHotSwapper(configPath);
                    _config = config;
                    _aniListUrl = config.GetSetting("AniListUrl") ?? "";
                }
            }
            catch {}
            
            if (string.IsNullOrEmpty(_aniListUrl))
            {
                _aniListUrl = "https://graphql.anilist.co";
            }
        }

        public virtual async Task<List<MediaResult>> SearchAnimeAsync(string query, System.Threading.CancellationToken token = default)
        {
            return (await SearchAnimePageAsync(query, 1, 24, null, token)).Results;
        }

        public Task<MediaSearchPage> SearchAnimePageAsync(
            string query,
            int page,
            int perPage,
            System.Threading.CancellationToken token)
        {
            return SearchAnimePageAsync(query, page, perPage, null, token);
        }

        public virtual async Task<MediaSearchPage> SearchAnimePageAsync(
            string query,
            int page = 1,
            int perPage = 24,
            AnimeSearchFilters? filters = null,
            System.Threading.CancellationToken token = default)
        {
            var results = new List<MediaResult>();
            page = Math.Max(1, page);
            perPage = Math.Clamp(perPage, 1, 50);
            filters ??= AnimeSearchFilters.Default;
            bool includeAdult = filters.IncludeAdult ?? (_config?.GetSetting("ShowAdultContent") == "true");
            bool isRecommendation = string.IsNullOrWhiteSpace(query);
            string[] sort = [MapSort(filters.Sort, isRecommendation)];
            string[]? genreIn = NormalizeFilterValues(filters.Genres);
            string[]? tagIn = NormalizeFilterValues(filters.Tags);
            string? statusFilter = MapStatus(filters.Status);
            bool? isAdult = includeAdult ? null : false;

            var variableDeclarations = new List<string> { "$page: Int", "$perPage: Int", "$sort: [MediaSort]" };
            var mediaArguments = new List<string> { "type: ANIME", "sort: $sort" };
            var variables = new Dictionary<string, object?>
            {
                ["page"] = page,
                ["perPage"] = perPage,
                ["sort"] = sort
            };

            if (!isRecommendation)
            {
                variableDeclarations.Insert(0, "$search: String");
                mediaArguments.Insert(0, "search: $search");
                variables["search"] = query.Trim();
            }

            if (genreIn is { Length: > 0 })
            {
                variableDeclarations.Add("$genreIn: [String]");
                mediaArguments.Add("genre_in: $genreIn");
                variables["genreIn"] = genreIn;
            }

            if (tagIn is { Length: > 0 })
            {
                variableDeclarations.Add("$tagIn: [String]");
                mediaArguments.Add("tag_in: $tagIn");
                variables["tagIn"] = tagIn;
            }

            if (!string.IsNullOrWhiteSpace(statusFilter))
            {
                variableDeclarations.Add("$status: MediaStatus");
                mediaArguments.Add("status: $status");
                variables["status"] = statusFilter;
            }

            if (isAdult.HasValue)
            {
                variableDeclarations.Add("$isAdult: Boolean");
                mediaArguments.Add("isAdult: $isAdult");
                variables["isAdult"] = isAdult.Value;
            }

            string gqlQuery = $@"
            query ({string.Join(", ", variableDeclarations)}) {{
                Page(page: $page, perPage: $perPage) {{
                    pageInfo {{ currentPage hasNextPage }}
                    media({string.Join(", ", mediaArguments)}) {{
                        id
                        idMal
                        format
                        episodes
                        nextAiringEpisode {{ episode airingAt }}
                        isAdult
                        title {{ romaji english native }}
                        synonyms
                        coverImage {{ extraLarge large }}
                        averageScore
                        status
                        startDate {{ year }}
                        genres
                        description(asHtml: false)
                    }}
                }}
            }}";

            object requestBody = new { query = gqlQuery, variables };

            string jsonBody = JsonSerializer.Serialize(requestBody);
            bool hasNextPage = false;

            try
            {
                using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                HttpResponseMessage? response = null;
                
                try
                {
                    response = await _requestClient.PostAsync(_aniListUrl, content, token);
                    
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        var retryAfter = response.Headers.RetryAfter;
                        TimeSpan delay = retryAfter?.Delta ?? (retryAfter?.Date is { } retryDate
                            ? retryDate - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(2));
                        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                        // Respect the server rather than retrying early. Long
                        // backoffs need an explicit later retry, not a spinner
                        // beyond the existing 20-second request budget.
                        if (delay > _httpClient.Timeout)
                            throw new HttpRequestException("AniList is rate limited; retry the catalog later.",
                                null, System.Net.HttpStatusCode.TooManyRequests);
                        
                        response.Dispose(); // Dispose rate-limited response
                        response = null;

                        await Task.Delay(delay, token);
                        
                        using var retryContent = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                        response = await _requestClient.PostAsync(_aniListUrl, retryContent, token);
                    }

                    string responseJson = await response.Content.ReadAsStringAsync(token);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(responseJson);
                        if (doc.RootElement.TryGetProperty("errors", out var errorsElement) &&
                            errorsElement.ValueKind == JsonValueKind.Array &&
                            errorsElement.GetArrayLength() > 0)
                        {
                            string errorText = ExtractGraphQlErrors(errorsElement);
                            UniversalMediaOS.Core.Helpers.AppLogger.Log($"AniList search returned GraphQL errors: {errorText}", "WARNING");
                            throw new Exception($"AniList GraphQL error: {errorText}");
                        }

                        if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object &&
                            dataElement.TryGetProperty("Page", out var pageElement) && 
                            pageElement.ValueKind == JsonValueKind.Object &&
                            pageElement.TryGetProperty("media", out var mediaArray) && mediaArray.ValueKind == JsonValueKind.Array)
                        {
                            if (pageElement.TryGetProperty("pageInfo", out var pageInfo) &&
                                pageInfo.TryGetProperty("hasNextPage", out var hasNextProp) &&
                                (hasNextProp.ValueKind == JsonValueKind.True || hasNextProp.ValueKind == JsonValueKind.False))
                            {
                                hasNextPage = hasNextProp.GetBoolean();
                            }

                            foreach (var item in mediaArray.EnumerateArray())
                            {
                                var titleElement = item.GetProperty("title");
                                string englishTitle = titleElement.TryGetProperty("english", out var e) && e.ValueKind != JsonValueKind.Null ? e.GetString() ?? "" : "";
                                string romajiTitle = titleElement.TryGetProperty("romaji", out var r) && r.ValueKind != JsonValueKind.Null ? r.GetString() ?? "" : "";
                                string nativeTitle = titleElement.TryGetProperty("native", out var n) && n.ValueKind != JsonValueKind.Null ? n.GetString() ?? "" : "";
                                
                                var coverElement = item.GetProperty("coverImage");
                                string coverUrl = coverElement.TryGetProperty("extraLarge", out var xl) && xl.ValueKind != JsonValueKind.Null ? xl.GetString() ?? "" : "";
                                if (string.IsNullOrEmpty(coverUrl))
                                    coverUrl = coverElement.TryGetProperty("large", out var l) && l.ValueKind != JsonValueKind.Null ? l.GetString() ?? "" : "";

                                string synopsis = item.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null ? desc.GetString() ?? "" : "";
                                string score = item.TryGetProperty("averageScore", out var avg) && avg.ValueKind == JsonValueKind.Number
                                    ? (avg.GetInt32() / 10.0).ToString("0.0")
                                    : "-";
                                string status = item.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String
                                    ? FormatStatus(statusProp.GetString() ?? "")
                                    : "Unknown";
                                string year = item.TryGetProperty("startDate", out var startDate) &&
                                              startDate.TryGetProperty("year", out var yearProp) &&
                                              yearProp.ValueKind == JsonValueKind.Number
                                    ? yearProp.GetInt32().ToString()
                                    : "AniList";
                                string genreOne = "Anime";
                                string genreTwo = "Series";
                                var genreValues = new List<string>();
                                if (item.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
                                {
                                    genreValues = genres.EnumerateArray()
                                        .Where(g => g.ValueKind == JsonValueKind.String)
                                        .Select(g => g.GetString() ?? "")
                                        .Where(g => !string.IsNullOrWhiteSpace(g))
                                        .ToList();
                                    if (genreValues.Count > 0) genreOne = genreValues[0];
                                    if (genreValues.Count > 1) genreTwo = genreValues[1];
                                }

                                var synonyms = new List<string>();
                                if (item.TryGetProperty("synonyms", out var synonymsElement) && synonymsElement.ValueKind == JsonValueKind.Array)
                                {
                                    synonyms = synonymsElement.EnumerateArray()
                                        .Where(s => s.ValueKind == JsonValueKind.String)
                                        .Select(s => s.GetString() ?? "")
                                        .Where(s => !string.IsNullOrWhiteSpace(s))
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .ToList();
                                }

                                int totalEpisodes = item.TryGetProperty("episodes", out var episodes) && episodes.ValueKind == JsonValueKind.Number
                                    ? episodes.GetInt32()
                                    : 0;
                                int nextAiringEpisode = item.TryGetProperty("nextAiringEpisode", out var nextAiring) &&
                                                        nextAiring.ValueKind == JsonValueKind.Object &&
                                                        nextAiring.TryGetProperty("episode", out var nextEpisodeProp) &&
                                                        nextEpisodeProp.ValueKind == JsonValueKind.Number
                                    ? nextEpisodeProp.GetInt32()
                                    : 0;
                                int releasedEpisodes = CalculateReleasedEpisodes(totalEpisodes, nextAiringEpisode, status);
                                bool itemIsAdult = item.TryGetProperty("isAdult", out var isAdultProp) &&
                                                   isAdultProp.ValueKind == JsonValueKind.True;

                                if (!includeAdult && itemIsAdult)
                                {
                                    continue;
                                }

                                results.Add(new MediaResult
                                {
                                    Id = item.GetProperty("id").GetInt32(),
                                    IdMal = item.TryGetProperty("idMal", out var idMal) && idMal.ValueKind == JsonValueKind.Number ? idMal.GetInt32() : 0,
                                    Format = item.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String ? format.GetString() ?? "" : "",
                                    TotalEpisodes = totalEpisodes,
                                    AvailableSubEpisodes = releasedEpisodes,
                                    AvailableDubEpisodes = 0,
                                    DubAvailabilityChecked = false,
                                    NextAiringEpisode = nextAiringEpisode,
                                    IsAdult = itemIsAdult,
                                    OfficialTitle = !string.IsNullOrEmpty(englishTitle) ? englishTitle :
                                        (!string.IsNullOrEmpty(romajiTitle) ? romajiTitle :
                                        (!string.IsNullOrEmpty(nativeTitle) ? nativeTitle : "Unknown Title")),
                                    EnglishTitle = englishTitle,
                                    RomajiTitle = romajiTitle,
                                    NativeTitle = nativeTitle,
                                    Synonyms = synonyms,
                                    CoverImageUrl = coverUrl,
                                    Synopsis = CleanHtml(synopsis),
                                    DisplayRating = score,
                                    DisplayStatus = status,
                                    DisplayYear = year,
                                    DisplayGenreOne = genreOne,
                                    DisplayGenreTwo = genreTwo,
                                    Genres = genreValues
                                });
                            }
                        }
                        else
                        {
                            UniversalMediaOS.Core.Helpers.AppLogger.Log($"AniList search response did not contain media. Response head: {TrimForLog(responseJson)}", "WARNING");
                            throw new InvalidDataException("AniList returned an incomplete catalog page.");
                        }
                    }
                    else
                    {
                        throw new Exception($"API Error: {response.StatusCode}\n{responseJson}");
                    }
                }
                finally
                {
                    response?.Dispose();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new Exception($"Search execution failed: {ex.Message}", ex);
            }

            return new MediaSearchPage(results, hasNextPage);
        }

        public virtual async Task<List<AnimeTagOption>> GetAnimeTagOptionsAsync(System.Threading.CancellationToken token = default)
        {
            const string gqlQuery = @"
            query {
                GenreCollection
                MediaTagCollection {
                    name
                    category
                    isAdult
                }
            }";

            var requestBody = new { query = gqlQuery };
            string jsonBody = JsonSerializer.Serialize(requestBody);
            var tags = new List<AnimeTagOption>();

            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var response = await _requestClient.PostAsync(_aniListUrl, content, token);
            string responseJson = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"AniList tag API error: {response.StatusCode}\n{responseJson}");
            }

            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("errors", out var errorsElement) &&
                errorsElement.ValueKind == JsonValueKind.Array &&
                errorsElement.GetArrayLength() > 0)
            {
                string errorText = ExtractGraphQlErrors(errorsElement);
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"AniList tag request returned GraphQL errors: {errorText}", "WARNING");
                throw new Exception($"AniList tag GraphQL error: {errorText}");
            }

            if (!doc.RootElement.TryGetProperty("data", out var dataElement))
            {
                throw new Exception($"AniList tag response did not contain data. Response head: {TrimForLog(responseJson)}");
            }

            if (dataElement.TryGetProperty("GenreCollection", out var genreArray) &&
                genreArray.ValueKind == JsonValueKind.Array)
            {
                tags.AddRange(genreArray.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? "")
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => new AnimeTagOption(name, "Genre", false, true)));
            }

            if (dataElement.TryGetProperty("MediaTagCollection", out var tagArray) &&
                tagArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tagArray.EnumerateArray())
                {
                    string name = item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                        ? nameProp.GetString() ?? ""
                        : "";
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    bool isAdultTag = item.TryGetProperty("isAdult", out var adultProp) &&
                                      adultProp.ValueKind == JsonValueKind.True;

                    string category = item.TryGetProperty("category", out var categoryProp) && categoryProp.ValueKind == JsonValueKind.String
                        ? categoryProp.GetString() ?? "Tag"
                        : "Tag";
                    tags.Add(new AnimeTagOption(name, category, isAdultTag, false));
                }
            }

            bool includeAdult = _config?.GetSetting("ShowAdultContent") == "true";
            return tags
                .Where(tag => includeAdult || (!tag.IsAdult &&
                    !tag.Name.Equals("Hentai", StringComparison.OrdinalIgnoreCase)))
                .GroupBy(tag => $"{tag.IsGenre}:{tag.Name}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(tag => tag.IsGenre)
                .ThenBy(tag => tag.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private string CleanHtml(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string withNewlines = input.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n");
            return _htmlRegex.Replace(withNewlines, string.Empty);
        }

        private static string FormatStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "Unknown";
            return string.Join(" ", status
                .Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
        }

        private static string MapSort(string? sort, bool isRecommendation)
        {
            if (string.IsNullOrWhiteSpace(sort) || sort.Equals("Any", StringComparison.OrdinalIgnoreCase))
            {
                return isRecommendation ? "TRENDING_DESC" : "SEARCH_MATCH";
            }

            return sort.Trim().ToLowerInvariant() switch
            {
                "popular" => "POPULARITY_DESC",
                "top rated" => "SCORE_DESC",
                "newest" => "START_DATE_DESC",
                "relevance" => isRecommendation ? "TRENDING_DESC" : "SEARCH_MATCH",
                "trending" => isRecommendation ? "TRENDING_DESC" : "SEARCH_MATCH",
                _ => isRecommendation ? "TRENDING_DESC" : "SEARCH_MATCH"
            };
        }

        private static string[]? NormalizeFilterValues(IEnumerable<string>? values)
        {
            if (values == null)
            {
                return null;
            }

            var normalized = values
                .Select(value => value?.Trim() ?? "")
                .Where(value => !string.IsNullOrWhiteSpace(value) &&
                                !value.Equals("Any", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return normalized.Length > 0 ? normalized : null;
        }

        private static string ExtractGraphQlErrors(JsonElement errorsElement)
        {
            var messages = new List<string>();
            foreach (var error in errorsElement.EnumerateArray())
            {
                if (error.TryGetProperty("message", out var messageProp) &&
                    messageProp.ValueKind == JsonValueKind.String)
                {
                    messages.Add(messageProp.GetString() ?? "Unknown GraphQL error");
                }
            }

            return messages.Count > 0 ? string.Join("; ", messages) : TrimForLog(errorsElement.ToString());
        }

        private static string TrimForLog(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            return value.Length <= 600 ? value : value[..600] + "...";
        }

        private static string? MapStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status) || status.Equals("Any", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return status.Trim().ToLowerInvariant() switch
            {
                "airing" => "RELEASING",
                "finished" => "FINISHED",
                "upcoming" => "NOT_YET_RELEASED",
                "cancelled" => "CANCELLED",
                "hiatus" => "HIATUS",
                _ => null
            };
        }

        private static int CalculateReleasedEpisodes(int totalEpisodes, int nextAiringEpisode, string displayStatus)
        {
            if (nextAiringEpisode > 1)
            {
                return nextAiringEpisode - 1;
            }

            if (displayStatus.Equals("Finished", StringComparison.OrdinalIgnoreCase))
            {
                return Math.Max(0, totalEpisodes);
            }

            if (displayStatus.Equals("Not Yet Released", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return Math.Max(0, totalEpisodes);
        }
    }

    public sealed record MediaSearchPage(List<MediaResult> Results, bool HasNextPage);

    public sealed record AnimeSearchFilters(
        IReadOnlyList<string>? Genres = null,
        IReadOnlyList<string>? Tags = null,
        string Status = "Any",
        string Sort = "Trending",
        string Audio = "Any",
        bool? IncludeAdult = null)
    {
        public static AnimeSearchFilters Default { get; } = new();
    }

    public sealed record AnimeTagOption(
        string Name,
        string Category,
        bool IsAdult,
        bool IsGenre);

    public enum MediaDubAvailabilityState
    {
        Unknown,
        Summary,
        Verified
    }

    public class MediaResult : INotifyPropertyChanged
    {
        private int _availableSubEpisodes;
        private int _availableDubEpisodes;
        private bool _dubAvailabilityChecked;
        private MediaDubAvailabilityState _dubAvailabilityState;
        private int _highestContiguousDubEpisode;
        private IReadOnlyList<decimal> _dubbedEpisodeNumbers = Array.Empty<decimal>();
        private bool _isFavorite;

        public event PropertyChangedEventHandler? PropertyChanged;

        public int Id { get; set; }
        public int IdMal { get; set; }
        public string Format { get; set; } = string.Empty;
        public int TotalEpisodes { get; set; }
        public int AvailableSubEpisodes
        {
            get => _availableSubEpisodes;
            set
            {
                if (_availableSubEpisodes == value) return;
                _availableSubEpisodes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EpisodeCountText));
                OnPropertyChanged(nameof(SubBadgeText));
            }
        }

        public int AvailableDubEpisodes
        {
            get => _availableDubEpisodes;
            set
            {
                if (_availableDubEpisodes == value) return;
                _availableDubEpisodes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DubBadgeText));
            }
        }

        public bool DubAvailabilityChecked
        {
            get => _dubAvailabilityChecked;
            set
            {
                if (_dubAvailabilityChecked == value) return;
                _dubAvailabilityChecked = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DubBadgeText));
            }
        }

        public MediaDubAvailabilityState DubAvailabilityState
        {
            get => _dubAvailabilityState;
            set
            {
                if (_dubAvailabilityState == value) return;
                _dubAvailabilityState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDubAvailabilityVerified));
                OnPropertyChanged(nameof(DubBadgeText));
            }
        }

        public bool IsDubAvailabilityVerified =>
            DubAvailabilityState == MediaDubAvailabilityState.Verified;

        public int HighestContiguousDubEpisode
        {
            get => _highestContiguousDubEpisode;
            set
            {
                int normalized = Math.Max(0, value);
                if (_highestContiguousDubEpisode == normalized) return;
                _highestContiguousDubEpisode = normalized;
                OnPropertyChanged();
            }
        }

        public IReadOnlyList<decimal> DubbedEpisodeNumbers
        {
            get => _dubbedEpisodeNumbers;
            set
            {
                IReadOnlyList<decimal> normalized = value ?? Array.Empty<decimal>();
                if (ReferenceEquals(_dubbedEpisodeNumbers, normalized)) return;
                _dubbedEpisodeNumbers = normalized;
                OnPropertyChanged();
            }
        }

        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite == value) return;
                _isFavorite = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FavoriteGlyph));
                OnPropertyChanged(nameof(FavoriteToolTip));
            }
        }

        public int NextAiringEpisode { get; set; }
        public bool IsAdult { get; set; }
        public string OfficialTitle { get; set; } = string.Empty;
        public string EnglishTitle { get; set; } = string.Empty;
        public string RomajiTitle { get; set; } = string.Empty;
        public string NativeTitle { get; set; } = string.Empty;
        public List<string> Synonyms { get; set; } = new();
        public string CoverImageUrl { get; set; } = string.Empty;
        public string Synopsis { get; set; } = string.Empty;
        public string TargetEpisode { get; set; } = "1";
        public string TargetProviderDomain { get; set; } = "https://animepahe.ru/anime/{query}";
        public string DisplayRating { get; set; } = "9.0";
        public string DisplayStatus { get; set; } = "Finished";
        public string DisplayYear { get; set; } = "AniList";
        public string DisplayGenreOne { get; set; } = "Action";
        public string DisplayGenreTwo { get; set; } = "Dark Fantasy";
        public List<string> Genres { get; set; } = new();
        public string EpisodeCountText => AvailableSubEpisodes > 0
            ? $"{AvailableSubEpisodes} aired"
            : TotalEpisodes > 0
                ? $"{TotalEpisodes} eps"
                : DisplayStatus.Equals("Releasing", StringComparison.OrdinalIgnoreCase)
                    ? "Airing"
                    : "Episodes TBA";
        public string SubBadgeText => $"Sub {AvailableSubEpisodes}";
        public string DubBadgeText => DubAvailabilityState switch
        {
            MediaDubAvailabilityState.Verified when AvailableDubEpisodes > 0 => $"Dub {AvailableDubEpisodes}",
            MediaDubAvailabilityState.Verified => "Dub none",
            MediaDubAvailabilityState.Summary when AvailableDubEpisodes > 0 => $"Dub ~{AvailableDubEpisodes}",
            MediaDubAvailabilityState.Summary => "Dub none?",
            _ => "Dub —"
        };
        public string FavoriteGlyph => IsFavorite ? "\uE735" : "\uE734";
        public string FavoriteToolTip => IsFavorite ? "Remove from My List favorites." : "Save to My List favorites.";

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
