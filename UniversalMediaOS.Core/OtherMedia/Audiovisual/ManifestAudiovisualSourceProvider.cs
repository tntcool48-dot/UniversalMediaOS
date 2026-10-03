using System.Globalization;
using System.Text.Json;

namespace UniversalMediaOS.Core.OtherMedia;

public interface IAudiovisualSourceProvider
{
    Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(
        AudiovisualIdentity identity,
        AudiovisualUnit? unit = null,
        string? preferredLanguage = null,
        CancellationToken token = default);
}

public sealed class ManifestAudiovisualSourceProvider : IAudiovisualSourceProvider
{
    private readonly AudiovisualProviderManifest _manifest;
    private readonly ProviderRequestCoordinator _requests;

    public ManifestAudiovisualSourceProvider(
        AudiovisualProviderManifest manifest,
        ProviderRequestCoordinator? requests = null)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _requests = requests ?? new ProviderRequestCoordinator();
    }

    public async Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(
        AudiovisualIdentity identity,
        AudiovisualUnit? unit = null,
        string? preferredLanguage = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Title))
        {
            return Array.Empty<AudiovisualSource>();
        }

        AudiovisualProviderDefinition[] providers = _manifest.Providers
            .Where(provider => provider.Supports(identity.Kind, preferredLanguage))
            .ToArray();
        if (providers.Length == 0)
        {
            return Array.Empty<AudiovisualSource>();
        }

        Task<IReadOnlyList<AudiovisualSource>>[] searches = providers
            .Select(provider => FindProviderSourcesAsync(
                provider,
                identity,
                unit,
                preferredLanguage,
                token))
            .ToArray();
        IReadOnlyList<AudiovisualSource>[] results = await Task.WhenAll(searches).ConfigureAwait(false);

        return results
            .SelectMany(group => group)
            .DistinctBy(source => source.Location.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(source => source.AccessMode == AudiovisualSourceAccessMode.DirectMedia)
            .ThenBy(source => source.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<AudiovisualSource>> FindProviderSourcesAsync(
        AudiovisualProviderDefinition provider,
        AudiovisualIdentity requestedIdentity,
        AudiovisualUnit? requestedUnit,
        string? preferredLanguage,
        CancellationToken token)
    {
        Uri requestUri;
        try
        {
            requestUri = provider.BuildSearchUri(requestedIdentity, requestedUnit, preferredLanguage);
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<AudiovisualSource>();
        }

        string? payload = await _requests.GetStringAsync(provider, requestUri, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<AudiovisualSource>();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement items = GetItems(document.RootElement);
            if (items.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<AudiovisualSource>();
            }

            var sources = new List<AudiovisualSource>();
            foreach (JsonElement item in items.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                AudiovisualSource? source = ParseSource(
                    provider,
                    item,
                    requestedIdentity,
                    requestedUnit,
                    preferredLanguage);
                if (source is not null)
                {
                    sources.Add(source);
                }
            }

            return sources
                .DistinctBy(source => source.Location.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<AudiovisualSource>();
        }
    }

    private static AudiovisualSource? ParseSource(
        AudiovisualProviderDefinition provider,
        JsonElement item,
        AudiovisualIdentity requestedIdentity,
        AudiovisualUnit? requestedUnit,
        string? preferredLanguage)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        AudiovisualMediaKind? kind = ParseKind(GetString(item, "kind", "mediaKind", "type"));
        if (!kind.HasValue && provider.MediaKinds.Count == 1)
        {
            kind = provider.MediaKinds[0];
        }

        if (!kind.HasValue)
        {
            return null;
        }

        var candidateUnit = new AudiovisualUnit
        {
            SeasonNumber = GetInteger(item, "season", "seasonNumber", "season_number"),
            EpisodeNumber = GetInteger(item, "episode", "episodeNumber", "episode_number"),
            Title = GetString(item, "episodeTitle", "unitTitle", "episode_title")
        };
        var candidateIdentity = new AudiovisualIdentity
        {
            Kind = kind.Value,
            ContentForm = ParseContentForm(
                GetString(item, "contentForm", "content_form", "form", "subtype"),
                kind.Value,
                candidateUnit),
            Title = GetString(item, "title", "name"),
            OriginalTitle = GetString(item, "originalTitle", "original_title"),
            AlternateTitles = GetStringArray(item, "alternateTitles", "aliases", "alternate_titles"),
            Year = GetInteger(item, "year", "releaseYear", "release_year"),
            TmdbId = GetInteger(item, "tmdbId", "tmdb_id")
        };

        if (!ExactAudiovisualMatcher.IsExactMatch(
                requestedIdentity,
                requestedUnit,
                candidateIdentity,
                candidateUnit))
        {
            return null;
        }

        string locationValue = GetString(item, "url", "location", "mediaUrl", "media_url");
        if (!Uri.TryCreate(locationValue, UriKind.Absolute, out Uri? location) ||
            !provider.IsUriAllowed(location))
        {
            return null;
        }

        IReadOnlyList<string> languages = GetStringArray(
                item,
                "languages",
                "language",
                "audioLanguages")
            .Concat(provider.Languages)
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(preferredLanguage) &&
            !languages.Any(language =>
                AudiovisualProviderDefinition.LanguageMatches(language, preferredLanguage)))
        {
            return null;
        }

        AudiovisualSourceAccessMode accessMode = ParseAccessMode(
            GetString(item, "accessMode", "access_mode", "mode"));
        string lane = GetString(item, "lane");
        if (!provider.ArabicCartoonLane ||
            !lane.Equals("arabic-cartoon", StringComparison.OrdinalIgnoreCase))
        {
            lane = string.Empty;
        }

        return new AudiovisualSource
        {
            ProviderId = provider.Id,
            ProviderName = provider.Name,
            Location = location,
            AccessMode = accessMode,
            Authorization = provider.Authorization,
            License = GetString(item, "license", "licenseUrl", "license_url"),
            Rights = GetString(item, "rights", "rightsStatement", "rights_statement") is string rights &&
                     !string.IsNullOrWhiteSpace(rights)
                ? rights
                : provider.RightsStatement,
            ContentType = GetString(
                item,
                "contentType",
                "content_type",
                "mimeType",
                "mime_type",
                "mime"),
            Languages = languages,
            Identity = candidateIdentity,
            Unit = candidateUnit,
            Lane = lane
        };
    }

    private static JsonElement GetItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        foreach (string name in new[] { "items", "results", "sources" })
        {
            if (root.TryGetProperty(name, out JsonElement items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                return items;
            }
        }

        if (root.TryGetProperty("data", out JsonElement data) &&
            data.ValueKind == JsonValueKind.Object)
        {
            return GetItems(data);
        }

        return default;
    }

    private static AudiovisualMediaKind? ParseKind(string value)
    {
        string normalized = value.Trim().Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        if (normalized.Equals("movie", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("film", StringComparison.OrdinalIgnoreCase))
        {
            return AudiovisualMediaKind.Movie;
        }

        if (normalized.Equals("television", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("tv", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            return AudiovisualMediaKind.Television;
        }

        if (normalized.Equals("cartoon", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("animation", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("animated", StringComparison.OrdinalIgnoreCase))
        {
            return AudiovisualMediaKind.Cartoon;
        }

        return null;
    }

    internal static AudiovisualSourceAccessMode ParseAccessMode(string? value)
    {
        string normalized = (value ?? string.Empty).Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.ToLowerInvariant() switch
        {
            "directmedia" or "direct" or "media" => AudiovisualSourceAccessMode.DirectMedia,
            "webpage" or "page" or "external" => AudiovisualSourceAccessMode.WebPage,
            "internetarchiveitem" or "archiveitem" => AudiovisualSourceAccessMode.InternetArchiveItem,
            _ => AudiovisualSourceAccessMode.WebPage
        };
    }

    private static AudiovisualContentForm ParseContentForm(
        string value,
        AudiovisualMediaKind kind,
        AudiovisualUnit unit)
    {
        string normalized = value.Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        if (normalized is "feature" or "film" or "movie")
        {
            return AudiovisualContentForm.Feature;
        }

        if (normalized is "series" or "television" or "tv" or "episodic")
        {
            return AudiovisualContentForm.Series;
        }

        if (kind == AudiovisualMediaKind.Movie)
        {
            return AudiovisualContentForm.Feature;
        }

        if (kind == AudiovisualMediaKind.Television || !unit.IsFeature)
        {
            return AudiovisualContentForm.Series;
        }

        return AudiovisualContentForm.Unknown;
    }

    private static string GetString(JsonElement item, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (TryGetPropertyIgnoreCase(item, propertyName, out JsonElement property))
            {
                if (property.ValueKind == JsonValueKind.String)
                {
                    return property.GetString() ?? string.Empty;
                }

                if (property.ValueKind == JsonValueKind.Number)
                {
                    return property.GetRawText();
                }
            }
        }

        return string.Empty;
    }

    private static int? GetInteger(JsonElement item, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(item, propertyName, out JsonElement property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number &&
                property.TryGetInt32(out int numeric))
            {
                return numeric;
            }

            if (property.ValueKind == JsonValueKind.String &&
                int.TryParse(
                    property.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetStringArray(
        JsonElement item,
        params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(item, propertyName, out JsonElement property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Array)
            {
                return property
                    .EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => element.GetString() ?? string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                string value = property.GetString() ?? string.Empty;
                return value
                    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }

        return Array.Empty<string>();
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
