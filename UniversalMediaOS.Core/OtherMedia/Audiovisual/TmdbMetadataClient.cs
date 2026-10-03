using System.Globalization;
using System.Text.Json;

namespace UniversalMediaOS.Core.OtherMedia;

/// <summary>
/// Reads movie, television, and animation metadata from the configured TMDB API.
/// The caller owns provider selection; a valid empty TMDB response remains empty.
/// </summary>
public sealed class TmdbMetadataClient : IAudiovisualMetadataClient
{
    private const int AnimationGenreId = 16;
    private static readonly IReadOnlyDictionary<int, string> GenreNames =
        new Dictionary<int, string>
        {
            [12] = "Adventure",
            [14] = "Fantasy",
            [16] = "Animation",
            [18] = "Drama",
            [27] = "Horror",
            [28] = "Action",
            [35] = "Comedy",
            [36] = "History",
            [37] = "Western",
            [53] = "Thriller",
            [80] = "Crime",
            [99] = "Documentary",
            [878] = "Science Fiction",
            [9648] = "Mystery",
            [10402] = "Music",
            [10749] = "Romance",
            [10751] = "Family",
            [10752] = "War",
            [10759] = "Action & Adventure",
            [10762] = "Kids",
            [10763] = "News",
            [10764] = "Reality",
            [10765] = "Sci-Fi & Fantasy",
            [10766] = "Soap",
            [10767] = "Talk",
            [10768] = "War & Politics",
            [10770] = "TV Movie"
        };

    private readonly AudiovisualOptions _options;
    private readonly ProviderRequestCoordinator _requests;
    private readonly AudiovisualProviderDefinition _provider;

    public TmdbMetadataClient(
        AudiovisualOptions options,
        ProviderRequestCoordinator requests)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _provider = new AudiovisualProviderDefinition
        {
            Id = "tmdb-metadata",
            Name = "TMDB",
            BaseUrl = _options.TmdbBaseUri.AbsoluteUri,
            SearchUrlTemplate = new Uri(_options.TmdbBaseUri, "search/multi?query={query}").AbsoluteUri,
            MediaKinds = Enum.GetValues<AudiovisualMediaKind>(),
            RequestsPerMinute = 240,
            CacheSeconds = 300,
            TimeoutSeconds = 15,
            MaxResponseBytes = 4 * 1024 * 1024
        };
    }

    public AudiovisualCatalogCapabilities Capabilities => AudiovisualCatalogCapabilities.Discover |
        AudiovisualCatalogCapabilities.Search | AudiovisualCatalogCapabilities.Continuation | AudiovisualCatalogCapabilities.Locales;

    // First-page compatibility adapters while view models migrate to GetPageAsync.
    public async Task<IReadOnlyList<AudiovisualMediaItem>> SearchAsync(AudiovisualMediaKind kind, string query, CancellationToken token = default) =>
        (await GetPageAsync(new(kind, AudiovisualCatalogMode.Search, query), token).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<AudiovisualMediaItem>> GetPopularAsync(AudiovisualMediaKind kind, CancellationToken token = default) =>
        (await GetPageAsync(new(kind), token).ConfigureAwait(false)).Items;

    public async Task<AudiovisualCatalogPage> GetPageAsync(AudiovisualCatalogRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        request = request.Normalize();
        request = request with { Locale = string.IsNullOrWhiteSpace(request.Locale) ? _options.TmdbLanguage : request.Locale };
        var cursor = CatalogContinuation.Read(_provider.Id, _options.TmdbBaseUri.AbsoluteUri + "|" + _options.TmdbApiKey, request);
        if (string.IsNullOrWhiteSpace(_options.TmdbApiKey))
            return AudiovisualCatalogPage.Failure(_provider.Id, ProviderOutcomeStatus.NotConfigured, "api_key_missing");
        if (cursor.Page > 500)
            return AudiovisualCatalogPage.Failure(_provider.Id, ProviderOutcomeStatus.Unsupported, "upstream_page_limit");
        if (request.Mode == AudiovisualCatalogMode.Search && request.Query.Length == 0)
            return new(Array.Empty<AudiovisualMediaItem>(), null, [new(_provider.Id, ProviderOutcomeStatus.Success, TimeSpan.Zero)]);

        string lane = request.Kind switch { AudiovisualMediaKind.Movie => "movie", AudiovisualMediaKind.Television => "tv", _ => request.Mode == AudiovisualCatalogMode.Search ? "multi" : "all" };
        string route = request.Mode == AudiovisualCatalogMode.Search ? "search/" + lane : "trending/" + lane + "/week";
        var parameters = new Dictionary<string, string>
        {
            ["page"] = cursor.Page.ToString(CultureInfo.InvariantCulture), ["include_adult"] = "false", ["language"] = request.Locale!
        };
        if (request.Mode == AudiovisualCatalogMode.Search) parameters["query"] = request.Query;
        var fetched = await _requests.FetchAsync(_provider, BuildUri(route, parameters), token).ConfigureAwait(false);
        if (fetched.Outcome.Status != ProviderOutcomeStatus.Success)
            return new(Array.Empty<AudiovisualMediaItem>(), null, [fetched.Outcome]);
        try
        {
            using var document = JsonDocument.Parse(fetched.ResponseText ?? "");
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new JsonException();
            var items = results.EnumerateArray().Select(result => ParseItem(result, request.Kind))
                .Where(item => item != null).Cast<AudiovisualMediaItem>()
                .DistinctBy(item => (item.Identity.ContentForm, item.Identity.TmdbId)).ToArray();
            int pages = GetInt32(document.RootElement, "total_pages") ?? cursor.Page;
            return CatalogContinuation.Slice(items, cursor, request.PageSize, cursor.Page < Math.Min(pages, 500), fetched.Outcome, fetched.IsStale);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new(Array.Empty<AudiovisualMediaItem>(), null, [fetched.Outcome with { Status = ProviderOutcomeStatus.InvalidResponse, DiagnosticCode = "invalid_catalog_payload" }]);
        }
    }

    private static AudiovisualMediaItem? ParseItem(
        JsonElement item,
        AudiovisualMediaKind requestedKind)
    {
        int? tmdbId = GetInt32(item, "id");
        if (tmdbId is not > 0)
        {
            return null;
        }

        string mediaType = GetString(item, "media_type").ToLowerInvariant();
        bool isMovie = mediaType == "movie" ||
                       mediaType.Length == 0 && item.TryGetProperty("title", out _);
        bool isTelevision = mediaType == "tv" ||
                            mediaType.Length == 0 && item.TryGetProperty("name", out _);
        if (requestedKind == AudiovisualMediaKind.Movie && !isMovie ||
            requestedKind == AudiovisualMediaKind.Television && !isTelevision ||
            requestedKind == AudiovisualMediaKind.Cartoon && !isMovie && !isTelevision)
        {
            return null;
        }

        int[] genreIds = GetInt32Array(item, "genre_ids");
        if (requestedKind == AudiovisualMediaKind.Cartoon &&
            !genreIds.Contains(AnimationGenreId))
        {
            return null;
        }

        string title = FirstNonEmpty(GetString(item, "title"), GetString(item, "name"));
        if (title.Length == 0)
        {
            return null;
        }

        string originalTitle = FirstNonEmpty(
            GetString(item, "original_title"),
            GetString(item, "original_name"),
            title);
        string date = FirstNonEmpty(
            GetString(item, "release_date"),
            GetString(item, "first_air_date"));
        int? year = date.Length >= 4 &&
                    int.TryParse(date.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedYear)
            ? parsedYear
            : null;
        string posterPath = GetString(item, "poster_path");
        string backdropPath = GetString(item, "backdrop_path");

        return new AudiovisualMediaItem
        {
            Identity = new AudiovisualIdentity
            {
                Kind = requestedKind,
                ContentForm = isTelevision
                    ? AudiovisualContentForm.Series
                    : AudiovisualContentForm.Feature,
                Title = title,
                OriginalTitle = originalTitle,
                AlternateTitles = originalTitle.Equals(title, StringComparison.OrdinalIgnoreCase)
                    ? Array.Empty<string>()
                    : [originalTitle],
                Year = year,
                PrimaryId = new("tmdb", isTelevision ? "tv" : "movie", tmdbId.Value.ToString(CultureInfo.InvariantCulture)),
                IsAnimated = genreIds.Contains(AnimationGenreId),
                TmdbId = tmdbId
            },
            Title = title,
            Overview = GetString(item, "overview"),
            PosterUrl = BuildImageUrl(posterPath, "w500"),
            BackdropUrl = BuildImageUrl(backdropPath, "w1280"),
            Genres = genreIds
                .Select(id => GenreNames.GetValueOrDefault(id))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Rating = GetDouble(item, "vote_average"),
            OriginalLanguage = GetString(item, "original_language")
        };
    }

    private Uri BuildUri(string route, IReadOnlyDictionary<string, string>? values = null)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["api_key"] = _options.TmdbApiKey,
            ["language"] = _options.TmdbLanguage
        };
        if (values != null)
        {
            foreach ((string key, string value) in values)
            {
                parameters[key] = value;
            }
        }

        string query = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri(_options.TmdbBaseUri, $"{route}?{query}");
    }

    private static string BuildImageUrl(string path, string size)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"https://image.tmdb.org/t/p/{size}/{path.TrimStart('/')}";
    }

    private static string GetString(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static int? GetInt32(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out int parsed)
            ? parsed
            : null;
    }

    private static int[] GetInt32Array(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<int>();
        }

        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _))
            .Select(value => value.GetInt32())
            .ToArray();
    }

    private static double GetDouble(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out double parsed)
            ? parsed
            : 0;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
