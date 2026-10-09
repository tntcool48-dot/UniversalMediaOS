using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UniversalMediaOS.Core.OtherMedia;

// Typed film catalog. IMDb artwork is optional and matched only by the film's exact ID.
internal sealed class WikidataMetadataClient : IAudiovisualMetadataClient
{
    private readonly ProviderRequestCoordinator _requests;
    private readonly Uri _baseUri;
    private readonly AudiovisualProviderDefinition _provider;
    private readonly bool _includePosters;
    private readonly TimeSpan _posterBudget;
    private static readonly AudiovisualProviderDefinition PosterProvider = new()
    {
        Id = "imdb-posters", Name = "IMDb", BaseUrl = "https://v3.sg.media-imdb.com/",
        RequestsPerMinute = 120, CacheSeconds = 86400, TimeoutSeconds = 3,
        MaxResponseBytes = 128 * 1024, MediaKinds = [AudiovisualMediaKind.Movie, AudiovisualMediaKind.Cartoon]
    };
    private const int UpstreamPageSize = 5;
    // Qualified subclasses occur as direct P31 values; Wikidata search does not
    // automatically include their ancestors. Keep discovery and parsing aligned.
    private static readonly string[] AnimatedFilmTypes = ["Q202866", "Q20650540", "Q29168811", "Q17517379"];
    private static readonly string[] FilmTypes = ["Q11424", "Q24869", .. AnimatedFilmTypes];

    public WikidataMetadataClient(ProviderRequestCoordinator requests, Uri? baseUri = null,
        bool includePosters = false, TimeSpan? posterBudget = null)
    {
        _requests = requests;
        _baseUri = baseUri ?? new("https://www.wikidata.org/w/api.php");
        _includePosters = includePosters;
        _posterBudget = posterBudget ?? TimeSpan.FromSeconds(4);
        _provider = new()
        {
            Id = "wikidata", Name = "Wikidata", BaseUrl = _baseUri.AbsoluteUri,
            RequestsPerMinute = 60, CacheSeconds = 3600, TimeoutSeconds = 12,
            MaxResponseBytes = 4 * 1024 * 1024,
            MediaKinds = [AudiovisualMediaKind.Movie, AudiovisualMediaKind.Cartoon]
        };
    }

    public AudiovisualCatalogCapabilities Capabilities => AudiovisualCatalogCapabilities.Discover |
        AudiovisualCatalogCapabilities.Search | AudiovisualCatalogCapabilities.Continuation;

    public async Task<IReadOnlyList<AudiovisualMediaItem>> SearchAsync(AudiovisualMediaKind kind,
        string query, CancellationToken token = default) =>
        (await GetPageAsync(new(kind, AudiovisualCatalogMode.Search, query), token).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<AudiovisualMediaItem>> GetPopularAsync(AudiovisualMediaKind kind,
        CancellationToken token = default) =>
        (await GetPageAsync(new(kind), token).ConfigureAwait(false)).Items;

    public async Task<AudiovisualCatalogPage> GetPageAsync(AudiovisualCatalogRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        request = request.Normalize();
        var cursor = CatalogContinuation.Read(_provider.Id, _baseUri.AbsoluteUri, request);
        if (request.Kind == AudiovisualMediaKind.Television || !string.IsNullOrEmpty(request.Locale))
            return AudiovisualCatalogPage.Failure(_provider.Id, ProviderOutcomeStatus.Unsupported, "wikidata_scope_unsupported");
        string terms = Regex.Replace(request.Query, @"[^\p{L}\p{N}\s]", " ").Trim();
        if (request.Mode == AudiovisualCatalogMode.Search && terms.Length == 0)
            return new([], null, [new(_provider.Id, ProviderOutcomeStatus.Success, TimeSpan.Zero)]);
        var filmTypes = request.Kind == AudiovisualMediaKind.Cartoon ? AnimatedFilmTypes : FilmTypes;
        string filter = "haswbstatement:" + string.Join('|', filmTypes.Select(type => "P31=" + type));
        string query = request.Mode == AudiovisualCatalogMode.Search ? $"\"{terms}\" {filter}" : filter;
        int limit = Math.Min(request.PageSize, UpstreamPageSize);
        var searched = await _requests.FetchAsync(_provider, Url(new()
        {
            ["action"] = "query", ["list"] = "search", ["srsearch"] = query,
            ["srnamespace"] = "0", ["srprop"] = "", ["srlimit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["sroffset"] = (cursor.Page - 1).ToString(CultureInfo.InvariantCulture),
            ["srsort"] = request.Mode == AudiovisualCatalogMode.Search ? "relevance" : "incoming_links_desc"
        }), token).ConfigureAwait(false);
        if (searched.Outcome.Status != ProviderOutcomeStatus.Success) return new([], null, [searched.Outcome]);
        var outcomes = new List<ProviderOutcome> { searched.Outcome };
        try
        {
            using var search = JsonDocument.Parse(searched.ResponseText ?? "");
            if (ApiFailure(search.RootElement) is { } searchFailure)
                return new([], null, [searched.Outcome with { Status = searchFailure, DiagnosticCode = "wikidata_api_error" }]);
            var rows = search.RootElement.GetProperty("query").GetProperty("search");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > limit) throw new JsonException();
            var ids = rows.EnumerateArray().Select(row => row.GetProperty("title").GetString() ?? "").Distinct().ToArray();
            if (ids.Any(id => !Regex.IsMatch(id, @"\AQ[1-9][0-9]*\z"))) throw new JsonException();
            string? next = null;
            if (search.RootElement.TryGetProperty("continue", out var continuation))
            {
                int offset = continuation.GetProperty("sroffset").GetInt32();
                if (offset <= cursor.Page - 1 || offset >= 1_000_000) throw new JsonException();
                next = CatalogContinuation.Write(cursor with { Page = offset + 1 });
            }
            if (ids.Length == 0) return new([], next, outcomes);
            token.ThrowIfCancellationRequested();
            var fetched = await _requests.FetchAsync(_provider, Url(new()
            {
                ["action"] = "wbgetentities", ["ids"] = string.Join('|', ids),
                ["props"] = "labels|descriptions|claims", ["languages"] = "en|mul"
            }), token).ConfigureAwait(false);
            outcomes.Add(fetched.Outcome);
            // Do not advance the cursor when enrichment fails: retry must fetch this page.
            if (fetched.Outcome.Status != ProviderOutcomeStatus.Success) return new([], null, outcomes, IsPartial: true);
            using var document = JsonDocument.Parse(fetched.ResponseText ?? "");
            if (ApiFailure(document.RootElement) is { } entityFailure)
                return new([], null, [searched.Outcome, fetched.Outcome with
                { Status = entityFailure, DiagnosticCode = "wikidata_api_error" }], IsPartial: true);
            var entities = document.RootElement.GetProperty("entities");
            var items = new List<AudiovisualMediaItem>();
            bool partial = false;
            foreach (string id in ids)
            {
                token.ThrowIfCancellationRequested();
                if (!entities.TryGetProperty(id, out var entity) || entity.TryGetProperty("missing", out _))
                { partial = true; continue; }
                var item = ParseEntity(id, entity, request.Kind);
                if (item != null) items.Add(item);
                else partial = true;
            }
            if (_includePosters)
            {
                using var artwork = CancellationTokenSource.CreateLinkedTokenSource(token);
                artwork.CancelAfter(_posterBudget);
                items = (await Task.WhenAll(items.Select(item => WithPosterAsync(item, artwork.Token, token))).ConfigureAwait(false)).ToList();
                partial |= items.Any(item => string.IsNullOrEmpty(item.PosterUrl));
            }
            token.ThrowIfCancellationRequested();
            return new(items, next, outcomes, partial, searched.IsStale || fetched.IsStale);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return new([], null, [.. outcomes, new(_provider.Id, ProviderOutcomeStatus.InvalidResponse,
                TimeSpan.Zero, DiagnosticCode: "invalid_wikidata_payload")], IsPartial: true);
        }
    }

    private async Task<AudiovisualMediaItem> WithPosterAsync(AudiovisualMediaItem item,
        CancellationToken artworkToken, CancellationToken callerToken)
    {
        string imdb = item.Identity.ImdbId;
        // Commons redirects can reject image loads. Prefer exact-ID IMDb artwork,
        // keeping the explicit Wikidata poster when that optional lookup fails.
        if (!Regex.IsMatch(imdb, @"\Att[0-9]+\z")) return item;
        try
        {
            var fetched = await _requests.FetchAsync(PosterProvider,
                new Uri(PosterProvider.BaseUrl + "suggestion/t/" + imdb + ".json"), artworkToken).ConfigureAwait(false);
            if (fetched.Outcome.Status != ProviderOutcomeStatus.Success) return item;
            using var document = JsonDocument.Parse(fetched.ResponseText ?? "");
            if (!document.RootElement.TryGetProperty("d", out var rows) || rows.ValueKind != JsonValueKind.Array) return item;
            var matches = rows.EnumerateArray().Where(row => Text(row, "id") == imdb).ToArray();
            if (matches.Length != 1) return item;
            var match = matches[0];
            // A same-name show, game or different remake cannot provide this film's poster.
            if (Text(match, "qid") is not ("movie" or "tvMovie") ||
                !match.TryGetProperty("i", out var image)) return item;
            string poster = Text(image, "imageUrl");
            if (!Uri.TryCreate(poster, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Host != "m.media-amazon.com") return item;
            return item with { PosterUrl = uri.AbsoluteUri };
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { return item; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return item; }
    }

    private Uri Url(Dictionary<string, string> parameters)
    {
        parameters["format"] = "json";
        // These are foreground catalog requests. MediaWiki permits interactive tasks
        // to omit maxlag; batch qualification tools retain it. HTTP rate limits still apply.
        // https://www.mediawiki.org/wiki/Manual:Maxlag_parameter
        return new(_baseUri.AbsoluteUri + "?" + string.Join('&', parameters.Select(p =>
            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
    }

    private static ProviderOutcomeStatus? ApiFailure(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error)) return null;
        return Text(error, "code") is "maxlag" or "ratelimited"
            ? ProviderOutcomeStatus.RateLimited : ProviderOutcomeStatus.InvalidResponse;
    }

    private static AudiovisualMediaItem? ParseEntity(string id, JsonElement entity, AudiovisualMediaKind kind)
    {
        if (Text(entity, "id") != id) throw new JsonException();
        var types = Values(entity, "P31").Select(v => Text(v, "id")).ToArray();
        bool animated = types.Any(type => AnimatedFilmTypes.Contains(type));
        if (!types.Any(type => FilmTypes.Contains(type)) || kind == AudiovisualMediaKind.Cartoon && !animated) return null;
        var labels = entity.GetProperty("labels");
        string title = labels.TryGetProperty("en", out var english) ? Text(english, "value") : "";
        if (title.Length == 0 && labels.TryGetProperty("mul", out var shared)) title = Text(shared, "value");
        if (string.IsNullOrWhiteSpace(title)) return null;
        int? year = Values(entity, "P577").Select(v =>
        {
            string date = Text(v, "time");
            return v.TryGetProperty("precision", out var precision) && precision.GetInt32() >= 9 &&
                date.Length >= 5 && date[0] == '+' && int.TryParse(date.AsSpan(1, 4), out int parsed) && parsed > 0
                ? (int?)parsed : null;
        }).Where(y => y != null).DefaultIfEmpty().Min();
        string[] imdbIds = Values(entity, "P345").Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString()!).Where(v => Regex.IsMatch(v, @"\Att[0-9]+\z")).Distinct().ToArray();
        if (imdbIds.Length > 1) return null;
        string imdb = imdbIds.FirstOrDefault() ?? "";
        // Only an explicit poster claim becomes artwork; unrelated entity images are not posters.
        string poster = Values(entity, "P3383").Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString()!).FirstOrDefault() ?? "";
        return new()
        {
            Title = title,
            Overview = entity.TryGetProperty("descriptions", out var descriptions) && descriptions.TryGetProperty("en", out var description)
                ? Text(description, "value") : "",
            Identity = new()
            {
                PrimaryId = new("wikidata", "item", id), Kind = kind,
                ContentForm = AudiovisualContentForm.Feature, Title = title, Year = year,
                ImdbId = imdb, IsAnimated = animated ? true : null
            },
            PosterUrl = poster.Length == 0 ? "" : "https://commons.wikimedia.org/wiki/Special:Redirect/file/" + Uri.EscapeDataString(poster)
        };
    }

    private static IEnumerable<JsonElement> Values(JsonElement entity, string property)
    {
        if (!entity.TryGetProperty("claims", out var claims) || !claims.TryGetProperty(property, out var values)) yield break;
        foreach (var statement in values.EnumerateArray())
            if (Text(statement, "rank") != "deprecated" && statement.TryGetProperty("mainsnak", out var snak) &&
                Text(snak, "snaktype") == "value" && snak.TryGetProperty("datavalue", out var data) && data.TryGetProperty("value", out var value))
                yield return value;
    }

    private static string Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
}
