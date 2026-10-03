using System.Globalization;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;

namespace UniversalMediaOS.Core.OtherMedia;

internal sealed class AudiovisualCatalogMetadataRouter : IAudiovisualMetadataClient
{
    private readonly DomainHotSwapper? _config;
    private readonly ProviderRequestCoordinator _requests;
    private readonly IAudiovisualMetadataClient? _legacyFallback;
    private readonly TvmazeMetadataClient _series;
    private readonly WikidataMetadataClient _films;

    public AudiovisualCatalogMetadataRouter(
        DomainHotSwapper? config,
        ProviderRequestCoordinator requests,
        IAudiovisualMetadataClient? legacyFallback = null)
    {
        _config = config;
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _legacyFallback = legacyFallback;
        _series = new TvmazeMetadataClient(_requests);
        _films = new WikidataMetadataClient(_requests, includePosters: true);
    }

    public Task<IReadOnlyList<AudiovisualMediaItem>> SearchAsync(
        AudiovisualMediaKind kind,
        string query,
        CancellationToken token = default)
    {
        return SelectClient(kind).SearchAsync(kind, query, token);
    }

    public Task<IReadOnlyList<AudiovisualMediaItem>> GetPopularAsync(
        AudiovisualMediaKind kind,
        CancellationToken token = default)
    {
        return SelectClient(kind).GetPopularAsync(kind, token);
    }

    public AudiovisualCatalogCapabilities Capabilities => _series.Capabilities | SelectClient(AudiovisualMediaKind.Movie).Capabilities;
    public AudiovisualCatalogCapabilities GetCapabilities(AudiovisualMediaKind kind) => SelectClient(kind).Capabilities;
    public Task<AudiovisualUnitsResult> GetUnitsAsync(AudiovisualIdentity identity,
        int? season = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        // Saved provider identity owns episode lookup, even after metadata settings change.
        var client = AudiovisualIdentityKeys.GetIds(identity).Any(id => id.Provider == "tvmaze" && id.Namespace == "show")
            ? _series : SelectClient(identity.Kind);
        return client.GetUnitsAsync(identity, season, token);
    }
    public Task<AudiovisualCatalogPage> GetPageAsync(AudiovisualCatalogRequest request, CancellationToken token = default) =>
        SelectClient(request.Kind).GetPageAsync(request, token);

    private IAudiovisualMetadataClient SelectClient(AudiovisualMediaKind kind)
    {
        if (kind == AudiovisualMediaKind.Television) return _series;
        if (kind == AudiovisualMediaKind.Movie) return _films;
        if (_config == null)
        {
            return _legacyFallback ?? EmptyAudiovisualMetadataClient.Instance;
        }

        AudiovisualOptions options = AudiovisualOptions.FromConfiguration(_config);
        if (!string.IsNullOrWhiteSpace(options.TmdbApiKey))
        {
            return new TmdbMetadataClient(options, _requests);
        }

        return options.EnableInternetArchive
            ? new InternetArchiveMetadataClient(options, _requests)
            : EmptyAudiovisualMetadataClient.Instance;
    }
}

internal sealed class EmptyAudiovisualMetadataClient : IAudiovisualMetadataClient
{
    public static EmptyAudiovisualMetadataClient Instance { get; } = new();
    public AudiovisualCatalogCapabilities Capabilities => AudiovisualCatalogCapabilities.None;
    public Task<AudiovisualCatalogPage> GetPageAsync(AudiovisualCatalogRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        request.Normalize();
        return Task.FromResult(AudiovisualCatalogPage.Failure("metadata", ProviderOutcomeStatus.NotConfigured, "no_metadata_provider"));
    }


    public Task<IReadOnlyList<AudiovisualMediaItem>> SearchAsync(
        AudiovisualMediaKind kind,
        string query,
        CancellationToken token = default)
    {
        return Task.FromResult<IReadOnlyList<AudiovisualMediaItem>>(
            Array.Empty<AudiovisualMediaItem>());
    }

    public Task<IReadOnlyList<AudiovisualMediaItem>> GetPopularAsync(
        AudiovisualMediaKind kind,
        CancellationToken token = default)
    {
        return Task.FromResult<IReadOnlyList<AudiovisualMediaItem>>(
            Array.Empty<AudiovisualMediaItem>());
    }
}

internal sealed class InternetArchiveMetadataClient : IAudiovisualMetadataClient
{
    private static readonly string[] Fields =
    [
        "identifier",
        "title",
        "year",
        "date",
        "description",
        "licenseurl",
        "rights",
        "language",
        "subject",
        "season",
        "episode",
        "season_number",
        "episode_number",
        "tmdb_id"
    ];

    private readonly AudiovisualOptions _options;
    private readonly ProviderRequestCoordinator _requests;
    private readonly AudiovisualProviderDefinition _provider;

    public InternetArchiveMetadataClient(
        AudiovisualOptions options,
        ProviderRequestCoordinator requests)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _provider = new AudiovisualProviderDefinition
        {
            Id = "internet-archive-catalog",
            Name = "Internet Archive",
            BaseUrl = options.InternetArchiveBaseUri.AbsoluteUri,
            SearchUrlTemplate = new Uri(
                options.InternetArchiveBaseUri,
                "advancedsearch.php?q={query}").AbsoluteUri,
            MediaKinds = Enum.GetValues<AudiovisualMediaKind>(),
            RequestsPerMinute = 180,
            CacheSeconds = 600,
            TimeoutSeconds = 15,
            MaxResponseBytes = 4 * 1024 * 1024
        };
    }

    public AudiovisualCatalogCapabilities Capabilities => AudiovisualCatalogCapabilities.Discover |
        AudiovisualCatalogCapabilities.Search | AudiovisualCatalogCapabilities.Continuation;

    public async Task<IReadOnlyList<AudiovisualMediaItem>> SearchAsync(AudiovisualMediaKind kind, string query, CancellationToken token = default) =>
        (await GetPageAsync(new(kind, AudiovisualCatalogMode.Search, query, PageSize: 50), token).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<AudiovisualMediaItem>> GetPopularAsync(AudiovisualMediaKind kind, CancellationToken token = default) =>
        (await GetPageAsync(new(kind, PageSize: 50), token).ConfigureAwait(false)).Items;

    public async Task<AudiovisualCatalogPage> GetPageAsync(AudiovisualCatalogRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        request = request.Normalize();
        var cursor = CatalogContinuation.Read(_provider.Id, _options.InternetArchiveBaseUri.AbsoluteUri, request);
        if (!_options.EnableInternetArchive)
            return AudiovisualCatalogPage.Failure(_provider.Id, ProviderOutcomeStatus.Disabled, "archive_disabled");
        if (!string.IsNullOrEmpty(request.Locale))
            return AudiovisualCatalogPage.Failure(_provider.Id, ProviderOutcomeStatus.Unsupported, "archive_locale_unsupported");
        if (request.Mode == AudiovisualCatalogMode.Search && request.Query.Length == 0)
            return new(Array.Empty<AudiovisualMediaItem>(), null, [new(_provider.Id, ProviderOutcomeStatus.Success, TimeSpan.Zero)]);
        string escaped = request.Query.Replace("\"", "\\\"", StringComparison.Ordinal);
        string query = request.Mode == AudiovisualCatalogMode.Search
            ? $"title:\"{escaped}\" AND mediatype:movies" : "mediatype:movies AND (licenseurl:* OR rights:*)";
        var parameters = new List<string>
        {
            $"q={Uri.EscapeDataString(query)}", "rows=50", $"page={cursor.Page}", "output=json", "sort%5B%5D=downloads+desc"
        };
        parameters.AddRange(Fields.Select(field => $"fl%5B%5D={Uri.EscapeDataString(field)}"));
        var uri = new Uri(_options.InternetArchiveBaseUri, $"advancedsearch.php?{string.Join("&", parameters)}");
        var fetched = await _requests.FetchAsync(_provider, uri, token).ConfigureAwait(false);
        if (fetched.Outcome.Status != ProviderOutcomeStatus.Success)
            return new(Array.Empty<AudiovisualMediaItem>(), null, [fetched.Outcome]);
        try
        {
            using var document = JsonDocument.Parse(fetched.ResponseText ?? "");
            if (!document.RootElement.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
                throw new JsonException();
            var items = docs.EnumerateArray().Select(item => ParseItem(item, request.Kind, request.Mode == AudiovisualCatalogMode.Search ? request.Query : null))
                .Where(item => item != null).Cast<AudiovisualMediaItem>()
                .DistinctBy(item => item.Identity.PrimaryId).ToArray();
            bool more = response.TryGetProperty("numFound", out var total) && total.TryGetInt64(out long count) && count > (long)cursor.Page * 50;
            return CatalogContinuation.Slice(items, cursor, request.PageSize, more, fetched.Outcome, fetched.IsStale);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new(Array.Empty<AudiovisualMediaItem>(), null, [fetched.Outcome with { Status = ProviderOutcomeStatus.InvalidResponse, DiagnosticCode = "invalid_catalog_payload" }]);
        }
    }

    private AudiovisualMediaItem? ParseItem(
        JsonElement item,
        AudiovisualMediaKind kind,
        string? exactTitle)
    {
        string identifier = GetScalarString(item, "identifier");
        string title = GetScalarString(item, "title");
        if (identifier.Length == 0 || title.Length == 0 ||
            exactTitle != null && !TitlesEqual(title, exactTitle))
        {
            return null;
        }

        string license = GetScalarString(item, "licenseurl");
        string rights = GetScalarString(item, "rights");
        if (!InternetArchiveSourceProvider.TryGetExplicitAuthorization(
                license,
                rights,
                out _))
        {
            return null;
        }

        string[] subjects = GetStringValues(item, "subject");
        if (kind == AudiovisualMediaKind.Cartoon &&
            !subjects.Any(IsAnimationSignal))
        {
            return null;
        }

        bool series = kind == AudiovisualMediaKind.Television ||
                      HasValue(item, "season") ||
                      HasValue(item, "season_number") ||
                      HasValue(item, "episode") ||
                      HasValue(item, "episode_number");
        int? year = GetYear(item);
        int? tmdbId = GetNullableInt(item, "tmdb_id");
        string[] languages = GetStringValues(item, "language");

        return new AudiovisualMediaItem
        {
            Identity = new AudiovisualIdentity
            {
                Kind = kind,
                PrimaryId = new("internetarchive", "item", identifier),
                ContentForm = series
                    ? AudiovisualContentForm.Series
                    : AudiovisualContentForm.Feature,
                Title = title,
                OriginalTitle = title,
                Year = year,
                TmdbId = tmdbId
            },
            Title = title,
            Overview = GetScalarString(item, "description"),
            PosterUrl = new Uri(
                _options.InternetArchiveBaseUri,
                $"services/img/{Uri.EscapeDataString(identifier)}").AbsoluteUri,
            Genres = subjects,
            OriginalLanguage = languages.FirstOrDefault() ?? string.Empty
        };
    }

    private static bool TitlesEqual(string left, string right)
    {
        static string Normalize(string value) => string.Join(
            ' ',
            value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAnimationSignal(string value)
    {
        return value.Contains("animation", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("animated", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("cartoon", StringComparison.OrdinalIgnoreCase);
    }

    private static int? GetYear(JsonElement item)
    {
        int? year = GetNullableInt(item, "year");
        if (year is > 0)
        {
            return year;
        }

        string date = GetScalarString(item, "date");
        return date.Length >= 4 &&
               int.TryParse(date.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private static int? GetNullableInt(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        string text = GetScalarString(item, propertyName);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private static bool HasValue(JsonElement item, string propertyName)
    {
        return GetStringValues(item, propertyName).Length > 0;
    }

    private static string GetScalarString(JsonElement item, string propertyName)
    {
        return GetStringValues(item, propertyName).FirstOrDefault() ?? string.Empty;
    }

    private static string[] GetStringValues(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out JsonElement value))
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Select(ToText)
                .Where(text => text.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        string scalar = ToText(value);
        return scalar.Length == 0 ? Array.Empty<string>() : [scalar];
    }

    private static string ToText(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }
}
