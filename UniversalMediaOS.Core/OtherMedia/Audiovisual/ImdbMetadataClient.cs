using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.OtherMedia
{
    public sealed class ImdbMetadataClient : IAudiovisualMetadataClient
    {
        private readonly HttpClient _httpClient;

        // Legacy suggestion feed has no reliable catalog paging contract.
        public AudiovisualCatalogCapabilities Capabilities => AudiovisualCatalogCapabilities.None;
        public Task<AudiovisualCatalogPage> GetPageAsync(AudiovisualCatalogRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            request.Normalize();
            return Task.FromResult(AudiovisualCatalogPage.Failure("imdb-suggestions", ProviderOutcomeStatus.Unsupported, "legacy_suggestions_not_paged"));
        }


        // IMDB Autocomplete format: https://v3.sg.media-imdb.com/suggestion/x/matrix.json
        private const string ImdbSuggestBaseUrl = "https://v3.sg.media-imdb.com/suggestion/x/";

        public ImdbMetadataClient(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            }
        }

        public async Task<IReadOnlyList<AudiovisualMediaItem>> SearchAsync(
            AudiovisualMediaKind kind,
            string query,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Array.Empty<AudiovisualMediaItem>();

            try
            {
                // IMDB requires the first letter of the query in the URL path
                string firstLetter = query.Trim().Substring(0, 1).ToLowerInvariant();
                if (!char.IsLetterOrDigit(firstLetter[0])) firstLetter = "a";

                string url = $"{ImdbSuggestBaseUrl}{firstLetter}/{Uri.EscapeDataString(query.Trim())}.json";
                
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
                
                if (!response.IsSuccessStatusCode)
                    return Array.Empty<AudiovisualMediaItem>();

                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("d", out JsonElement resultsArray) || resultsArray.ValueKind != JsonValueKind.Array)
                    return Array.Empty<AudiovisualMediaItem>();

                var items = new List<AudiovisualMediaItem>();

                foreach (var result in resultsArray.EnumerateArray())
                {
                    // Basic filters to match kind
                    string qid = result.TryGetProperty("qid", out var qidProp) ? qidProp.GetString() ?? "" : "";
                    
                    bool isMovie = qid.Equals("movie", StringComparison.OrdinalIgnoreCase) || qid.Equals("tvMovie", StringComparison.OrdinalIgnoreCase);
                    bool isTv = qid.Equals("tvSeries", StringComparison.OrdinalIgnoreCase) || qid.Equals("tvMiniSeries", StringComparison.OrdinalIgnoreCase);
                    
                    if (kind == AudiovisualMediaKind.Movie && !isMovie) continue;
                    if (kind == AudiovisualMediaKind.Television && !isTv) continue;
                    if (kind == AudiovisualMediaKind.Cartoon && !isTv && !isMovie) continue;

                    string id = result.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    if (!id.StartsWith("tt")) continue; // Only accept titles

                    string title = result.TryGetProperty("l", out var titleProp) ? titleProp.GetString() ?? "" : "";
                    
                    int year = 0;
                    if (result.TryGetProperty("y", out var yearProp) && yearProp.ValueKind == JsonValueKind.Number)
                    {
                        year = yearProp.GetInt32();
                    }

                    string posterUrl = "";
                    if (result.TryGetProperty("i", out var imageObj) && imageObj.TryGetProperty("imageUrl", out var imageUrlProp))
                    {
                        posterUrl = imageUrlProp.GetString() ?? "";
                    }

                    string actors = result.TryGetProperty("s", out var sProp) ? sProp.GetString() ?? "" : "";

                    var contentForm = isMovie ? AudiovisualContentForm.Feature : AudiovisualContentForm.Series;

                    items.Add(new AudiovisualMediaItem
                    {
                        Identity = new AudiovisualIdentity
                        {
                            Kind = kind,
                            ContentForm = contentForm,
                            Title = title,
                            OriginalTitle = title,
                            Year = year > 0 ? year : null,
                            ImdbId = id
                        },
                        Title = title,
                        Overview = !string.IsNullOrWhiteSpace(actors) ? $"Starring: {actors}" : "",
                        PosterUrl = posterUrl,
                        OriginalLanguage = "en",
                        Genres = Array.Empty<string>()
                    });
                }

                return items.DistinctBy(i => i.Identity.ImdbId).ToArray();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ImdbMetadataClient] Search error: {ex.Message}");
                return Array.Empty<AudiovisualMediaItem>();
            }
        }

        public async Task<IReadOnlyList<AudiovisualMediaItem>> GetPopularAsync(
            AudiovisualMediaKind kind,
            CancellationToken token = default)
        {
            // Scrape TMDB trending HTML page to bypass API key requirements.
            string targetUrl = kind switch
            {
                AudiovisualMediaKind.Movie => "https://www.themoviedb.org/movie",
                AudiovisualMediaKind.Television => "https://www.themoviedb.org/tv",
                AudiovisualMediaKind.Cartoon => "https://www.themoviedb.org/tv", // Cartoons just pull from TV
                _ => "https://www.themoviedb.org/movie"
            };

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
                
                if (!response.IsSuccessStatusCode)
                    return Array.Empty<AudiovisualMediaItem>();

                string html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                
                // Extremely simple regex HTML parsing for TMDB's cards
                // Example: <a class="image" href="/movie/533535" title="Deadpool & Wolverine">
                // <img loading="lazy" class="poster" src="/t/p/w220_and_h330_face/8cdWjvZQUExUUTzyp4t6EDMubfO.jpg"
                
                var items = new List<AudiovisualMediaItem>();
                
                var cardRegex = new Regex(@"href=""/(movie|tv)/(\d+)[^""]*""\s+title=""([^""]+)""[^>]*>\s*<img[^>]*src=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                var matches = cardRegex.Matches(html);

                foreach (Match match in matches)
                {
                    if (match.Groups.Count < 5) continue;
                    
                    string type = match.Groups[1].Value;
                    string idStr = match.Groups[2].Value;
                    string title = System.Net.WebUtility.HtmlDecode(match.Groups[3].Value);
                    string posterPath = match.Groups[4].Value;

                    if (!int.TryParse(idStr, out int tmdbId)) continue;
                    
                    // Filter by kind
                    if (kind == AudiovisualMediaKind.Movie && type != "movie") continue;
                    if (kind == AudiovisualMediaKind.Television && type != "tv") continue;

                    string fullPosterUrl = posterPath.StartsWith("http") 
                        ? posterPath 
                        : $"https://media.themoviedb.org{posterPath}";

                    var contentForm = type == "movie" ? AudiovisualContentForm.Feature : AudiovisualContentForm.Series;

                    items.Add(new AudiovisualMediaItem
                    {
                        Identity = new AudiovisualIdentity
                        {
                            Kind = kind,
                            ContentForm = contentForm,
                            Title = title,
                            OriginalTitle = title,
                            TmdbId = tmdbId
                        },
                        Title = title,
                        PosterUrl = fullPosterUrl,
                        Overview = "",
                        OriginalLanguage = "en",
                        Genres = Array.Empty<string>()
                    });
                }
                
                return items.DistinctBy(i => i.Identity.TmdbId).ToArray();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ImdbMetadataClient] GetPopular error: {ex.Message}");
                return Array.Empty<AudiovisualMediaItem>();
            }
        }
    }
}
