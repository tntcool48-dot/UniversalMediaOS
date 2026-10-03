using System.Globalization;
using System.Text.Json;

namespace UniversalMediaOS.Core.OtherMedia;

// Keyless default for Television. Cartoon series are supported by the adapter, but
// film/cartoon catalog selection remains separate. Search has no upstream paging.
internal sealed class TvmazeMetadataClient : IAudiovisualMetadataClient, IAudiovisualEpisodeMetadataClient
{
    private readonly ProviderRequestCoordinator _requests;
    private readonly Uri _baseUri;
    private readonly AudiovisualProviderDefinition _provider;

    public TvmazeMetadataClient(ProviderRequestCoordinator requests, Uri? baseUri = null)
    {
        _requests = requests;
        _baseUri = baseUri ?? new("https://api.tvmaze.com/");
        _provider = new()
        {
            Id = "tvmaze", Name = "TVmaze", BaseUrl = _baseUri.AbsoluteUri,
            MediaKinds = [AudiovisualMediaKind.Television, AudiovisualMediaKind.Cartoon],
            RequestsPerMinute = 60, CacheSeconds = 3600, TimeoutSeconds = 12,
            MaxResponseBytes = 4 * 1024 * 1024
        };
    }

    public AudiovisualCatalogCapabilities Capabilities =>
        AudiovisualCatalogCapabilities.Search | AudiovisualCatalogCapabilities.Continuation |
        AudiovisualCatalogCapabilities.Discover | AudiovisualCatalogCapabilities.Episodes;

    public async Task<IReadOnlyList<AudiovisualMediaItem>> SearchAsync(
        AudiovisualMediaKind kind, string query, CancellationToken token = default) =>
        (await GetPageAsync(new(kind, AudiovisualCatalogMode.Search, query), token).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<AudiovisualMediaItem>> GetPopularAsync(
        AudiovisualMediaKind kind, CancellationToken token = default) =>
        (await GetPageAsync(new(kind), token).ConfigureAwait(false)).Items;

    public async Task<AudiovisualCatalogPage> GetPageAsync(
        AudiovisualCatalogRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        request = request.Normalize();
        var cursor = CatalogContinuation.Read(_provider.Id, _baseUri.AbsoluteUri, request, maxOffset: 250);
        if (request.Kind == AudiovisualMediaKind.Movie)
            return AudiovisualCatalogPage.Failure(_provider.Id, ProviderOutcomeStatus.Unsupported, "tvmaze_series_only");
        if (!string.IsNullOrEmpty(request.Locale))
            return AudiovisualCatalogPage.Failure(_provider.Id, ProviderOutcomeStatus.Unsupported, "tvmaze_locale_unsupported");
        bool discover = request.Mode == AudiovisualCatalogMode.Discover;
        if (!discover && request.Query.Length == 0)
            return new([], null, [new(_provider.Id, ProviderOutcomeStatus.Success, TimeSpan.Zero)]);

        var outcomes = new List<ProviderOutcome>();
        bool stale = false;
        // At most three index pages per call. Empty animation pages retain a cursor
        // when this refill budget runs out; never crawl the entire index on the UI's behalf.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            string path = discover ? "shows?page=" + (cursor.Page - 1).ToString(CultureInfo.InvariantCulture)
                : "search/shows?q=" + Uri.EscapeDataString(request.Query);
            var fetched = await _requests.FetchAsync(_provider, new Uri(_baseUri, path), token).ConfigureAwait(false);
            if (discover && fetched.Outcome.Status == ProviderOutcomeStatus.NotFound)
            {
                outcomes.Add(fetched.Outcome with { Status = ProviderOutcomeStatus.Success, DiagnosticCode = "index_complete" });
                return new([], null, outcomes, IsStale: stale);
            }
            outcomes.Add(fetched.Outcome);
            if (fetched.Outcome.Status != ProviderOutcomeStatus.Success)
                return new([], null, outcomes, IsPartial: attempt > 0);
            stale |= fetched.IsStale;
            try
            {
                using var document = JsonDocument.Parse(fetched.ResponseText ?? "");
                if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 250)
                    throw new JsonException();
                var items = new List<AudiovisualMediaItem>();
                foreach (var result in document.RootElement.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    var show = discover ? result : result.GetProperty("show");
                    var item = ParseShow(show, request.Kind);
                    if (item != null) items.Add(item);
                }
                var page = CatalogContinuation.Slice(items.DistinctBy(i => i.Identity.PrimaryId).ToArray(),
                    cursor, request.PageSize, discover, fetched.Outcome, stale);
                if (!discover || page.Items.Count > 0 || attempt == 2)
                    return page with { Outcomes = outcomes };
                cursor = cursor with { Page = cursor.Page + 1, Offset = 0 };
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            {
                outcomes[^1] = fetched.Outcome with
                { Status = ProviderOutcomeStatus.InvalidResponse, DiagnosticCode = "invalid_tvmaze_payload" };
                return new([], null, outcomes);
            }
        }
        throw new InvalidOperationException("Unreachable refill state.");
    }

    public async Task<AudiovisualUnitsResult> GetUnitsAsync(AudiovisualIdentity identity,
        int? season = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(identity);
        if (season < 0) throw new ArgumentOutOfRangeException(nameof(season));
        if (identity.ContentForm != AudiovisualContentForm.Series)
            return UnitsFailure(ProviderOutcomeStatus.Unsupported, "tvmaze_series_only");
        string showId;
        try
        {
            if (AudiovisualIdentityKeys.HasConflictingIds(identity, identity))
                return UnitsFailure(ProviderOutcomeStatus.InvalidResponse, "conflicting_show_identity");
            var ids = AudiovisualIdentityKeys.GetIds(identity).Where(i => i.Provider == "tvmaze" && i.Namespace == "show").ToArray();
            if (ids.Length != 1) return UnitsFailure(ProviderOutcomeStatus.Unsupported, "tvmaze_show_id_required");
            if (!int.TryParse(ids[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0)
                return UnitsFailure(ProviderOutcomeStatus.InvalidResponse, "invalid_show_identity");
            showId = number.ToString(CultureInfo.InvariantCulture);
        }
        catch (ArgumentException) { return UnitsFailure(ProviderOutcomeStatus.InvalidResponse, "invalid_show_identity"); }
        var fetched = await _requests.FetchAsync(_provider,
            new Uri(_baseUri, "shows/" + showId + "/episodes?specials=1"), token).ConfigureAwait(false);
        if (fetched.Outcome.Status != ProviderOutcomeStatus.Success) return new([], [fetched.Outcome]);
        try
        {
            using var document = JsonDocument.Parse(fetched.ResponseText ?? "");
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
            var episodes = new Dictionary<int, AudiovisualEpisodeMetadata>();
            foreach (var row in document.RootElement.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                int id = row.GetProperty("id").GetInt32();
                if (id <= 0) throw new JsonException();
                int? seasonNumber = Number(row, "season");
                int? episodeNumber = Number(row, "number");
                if (seasonNumber < 0 || episodeNumber <= 0) throw new JsonException();
                string type = Text(row, "type");
                bool special = type is "significant_special" or "insignificant_special" || seasonNumber == 0;
                string title = Text(row, "name");
                var unit = !special && type == "regular" && seasonNumber is > 0 && episodeNumber is > 0
                    ? new AudiovisualUnit { SeasonNumber = seasonNumber, EpisodeNumber = episodeNumber, Title = title } : null;
                var entry = new AudiovisualEpisodeMetadata(new("tvmaze", "episode", id.ToString(CultureInfo.InvariantCulture)),
                    title, seasonNumber, unit, special);
                if (episodes.TryGetValue(id, out var previous) && previous != entry) throw new JsonException();
                episodes[id] = entry;
            }
            var selected = episodes.Values.Where(e => season == null || e.SeasonNumber == season).ToArray();
            return new(selected, [fetched.Outcome], IsPartial: selected.Any(e => e.Unit == null && !e.IsSpecial));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or OverflowException)
        {
            return new([], [fetched.Outcome with { Status = ProviderOutcomeStatus.InvalidResponse, DiagnosticCode = "invalid_tvmaze_episodes" }]);
        }
    }

    private AudiovisualUnitsResult UnitsFailure(ProviderOutcomeStatus status, string code) =>
        new([], [new(_provider.Id, status, TimeSpan.Zero, DiagnosticCode: code)]);

    private static int? Number(JsonElement value, string property) =>
        !value.TryGetProperty(property, out var number) || number.ValueKind == JsonValueKind.Null ? null : number.GetInt32();

    private static AudiovisualMediaItem? ParseShow(JsonElement show, AudiovisualMediaKind kind)
    {
        if (!show.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out int id) || id <= 0)
            throw new JsonException();
        string title = Text(show, "name");
        if (string.IsNullOrWhiteSpace(title)) throw new JsonException();
        string type = Text(show, "type");
        bool? animated = type.Length == 0 ? null : type.Equals("Animation", StringComparison.OrdinalIgnoreCase);
        if (kind == AudiovisualMediaKind.Cartoon && animated != true) return null;
        int? year = DateOnly.TryParseExact(Text(show, "premiered"), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var premiered) ? premiered.Year : null;
        string imdb = show.TryGetProperty("externals", out var external) && external.ValueKind == JsonValueKind.Object
            ? Text(external, "imdb") : "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(imdb, @"\Att[0-9]+\z")) imdb = "";
        string poster = show.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object
            ? Text(image, "medium") : "";
        if (!Uri.TryCreate(poster, UriKind.Absolute, out var posterUri) || posterUri.Scheme != "https" || posterUri.UserInfo.Length != 0)
            poster = "";
        return new()
        {
            Identity = new()
            {
                PrimaryId = new("tvmaze", "show", id.ToString(CultureInfo.InvariantCulture)),
                Kind = kind, ContentForm = AudiovisualContentForm.Series, IsAnimated = animated,
                Title = title, Year = year, ImdbId = imdb
            },
            Title = title, PosterUrl = poster, Overview = PlainSummary(Text(show, "summary")),
            Rating = show.TryGetProperty("rating", out var rating) && rating.ValueKind == JsonValueKind.Object &&
                rating.TryGetProperty("average", out var average) && average.ValueKind == JsonValueKind.Number && average.TryGetDouble(out double score) &&
                score is >= 0 and <= 10 ? score : 0,
            Genres = show.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array
                ? genres.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.String).Select(g => g.GetString()!).ToArray() : [],
            // TVmaze's show language is not independently observed playback audio.
            // Do not infer an original title/language or dub availability from it.
        };
    }

    private static string PlainSummary(string html)
    {
        string plain = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ",
            System.Text.RegularExpressions.RegexOptions.NonBacktracking);
        return System.Text.RegularExpressions.Regex.Replace(System.Net.WebUtility.HtmlDecode(plain), @"\s+", " ",
            System.Text.RegularExpressions.RegexOptions.NonBacktracking).Trim();
    }

    private static string Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
}
