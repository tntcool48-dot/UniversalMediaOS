using System.Globalization;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;

namespace UniversalMediaOS.Core.OtherMedia;

public sealed class InternetArchiveSourceProvider : IAudiovisualSourceProvider
{
    private static readonly string[] SearchFields =
    [
        "identifier",
        "title",
        "year",
        "date",
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

    private static readonly string[] SupportedVideoExtensions =
    [
        ".mp4",
        ".m4v",
        ".webm",
        ".ogv",
        ".mkv"
    ];

    private readonly AudiovisualOptions _options;
    private readonly ProviderRequestCoordinator _requests;
    private readonly AudiovisualProviderDefinition _provider;

    public InternetArchiveSourceProvider(
        DomainHotSwapper? config = null,
        HttpClient? httpClient = null)
        : this(
            AudiovisualOptions.FromConfiguration(config),
            new ProviderRequestCoordinator(httpClient))
    {
    }

    internal InternetArchiveSourceProvider(
        AudiovisualOptions options,
        ProviderRequestCoordinator requests)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _provider = new AudiovisualProviderDefinition
        {
            Id = "internet-archive",
            Name = "Internet Archive",
            BaseUrl = options.InternetArchiveBaseUri.AbsoluteUri,
            SearchUrlTemplate = new Uri(
                options.InternetArchiveBaseUri,
                "advancedsearch.php?q={query}").AbsoluteUri,
            MediaKinds =
            [
                AudiovisualMediaKind.Movie,
                AudiovisualMediaKind.Television,
                AudiovisualMediaKind.Cartoon
            ],
            Languages = Array.Empty<string>(),
            Authorization = ProviderAuthorization.PublicDomain,
            RightsStatement = "Only items carrying an explicit public-domain or Creative Commons declaration are accepted.",
            RequestsPerMinute = 180,
            CacheSeconds = 900,
            TimeoutSeconds = 15,
            MaxResponseBytes = 4 * 1024 * 1024
        };
    }

    public async Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(
        AudiovisualIdentity identity,
        AudiovisualUnit? unit = null,
        string? preferredLanguage = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!_options.EnableInternetArchive || string.IsNullOrWhiteSpace(identity.Title))
        {
            return Array.Empty<AudiovisualSource>();
        }

        Uri searchUri = BuildSearchUri(identity);
        string? searchPayload = await _requests.GetStringAsync(_provider, searchUri, token)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(searchPayload))
        {
            return Array.Empty<AudiovisualSource>();
        }

        IReadOnlyList<ArchiveSearchDocument> documents = ParseSearchDocuments(
            searchPayload,
            identity,
            unit);
        if (documents.Count == 0)
        {
            return Array.Empty<AudiovisualSource>();
        }

        var sources = new List<AudiovisualSource>();
        foreach (ArchiveSearchDocument document in documents)
        {
            token.ThrowIfCancellationRequested();
            AudiovisualSource? source = await ResolveDocumentAsync(
                document,
                identity,
                unit,
                preferredLanguage,
                token).ConfigureAwait(false);
            if (source is not null)
            {
                sources.Add(source);
            }
        }

        return sources
            .DistinctBy(source => source.Location.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private Uri BuildSearchUri(AudiovisualIdentity identity)
    {
        string escapedTitle = identity.Title.Replace("\"", "\\\"", StringComparison.Ordinal);
        string query = $"title:\"{escapedTitle}\" AND mediatype:movies";
        if (identity.Year.HasValue)
        {
            query += $" AND year:{identity.Year.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        var queryParts = new List<string>
        {
            $"q={Uri.EscapeDataString(query)}",
            "rows=20",
            "page=1",
            "output=json",
            "sort%5B%5D=downloads+desc"
        };
        queryParts.AddRange(SearchFields.Select(field => $"fl%5B%5D={Uri.EscapeDataString(field)}"));
        return new Uri(
            _options.InternetArchiveBaseUri,
            $"advancedsearch.php?{string.Join("&", queryParts)}");
    }

    private static IReadOnlyList<ArchiveSearchDocument> ParseSearchDocuments(
        string payload,
        AudiovisualIdentity requestedIdentity,
        AudiovisualUnit? requestedUnit)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("response", out JsonElement response) ||
                !response.TryGetProperty("docs", out JsonElement docs) ||
                docs.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ArchiveSearchDocument>();
            }

            var results = new List<ArchiveSearchDocument>();
            foreach (JsonElement item in docs.EnumerateArray())
            {
                string identifier = GetScalarString(item, "identifier");
                if (string.IsNullOrWhiteSpace(identifier))
                {
                    continue;
                }

                AudiovisualIdentity candidateIdentity = BuildIdentity(item, requestedIdentity.Kind);
                AudiovisualUnit candidateUnit = BuildUnit(item);
                candidateIdentity = ApplyUnitContentForm(candidateIdentity, candidateUnit);
                if (!ExactAudiovisualMatcher.IsExactMatch(
                        requestedIdentity,
                        requestedUnit,
                        candidateIdentity,
                        candidateUnit))
                {
                    continue;
                }

                if (requestedIdentity.Kind == AudiovisualMediaKind.Cartoon &&
                    !HasAnimationSignal(item))
                {
                    continue;
                }

                results.Add(new ArchiveSearchDocument(
                    identifier,
                    item.Clone(),
                    candidateIdentity,
                    candidateUnit));
            }

            return results;
        }
        catch (JsonException)
        {
            return Array.Empty<ArchiveSearchDocument>();
        }
    }

    private async Task<AudiovisualSource?> ResolveDocumentAsync(
        ArchiveSearchDocument searchDocument,
        AudiovisualIdentity requestedIdentity,
        AudiovisualUnit? requestedUnit,
        string? preferredLanguage,
        CancellationToken token)
    {
        Uri metadataUri = new(
            _options.InternetArchiveBaseUri,
            $"metadata/{Uri.EscapeDataString(searchDocument.Identifier)}");
        string? payload = await _requests.GetStringAsync(_provider, metadataUri, token)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            JsonElement metadata = root.TryGetProperty("metadata", out JsonElement metadataValue) &&
                                   metadataValue.ValueKind == JsonValueKind.Object
                ? metadataValue
                : searchDocument.SearchDocument;

            AudiovisualIdentity candidateIdentity = MergeIdentity(
                BuildIdentity(metadata, requestedIdentity.Kind),
                searchDocument.Identity);
            AudiovisualUnit candidateUnit = MergeUnit(
                BuildUnit(metadata),
                searchDocument.Unit);
            candidateIdentity = ApplyUnitContentForm(candidateIdentity, candidateUnit);
            if (!ExactAudiovisualMatcher.IsExactMatch(
                    requestedIdentity,
                    requestedUnit,
                    candidateIdentity,
                    candidateUnit))
            {
                return null;
            }

            bool animation = requestedIdentity.Kind != AudiovisualMediaKind.Cartoon ||
                             HasAnimationSignal(metadata) ||
                             HasAnimationSignal(searchDocument.SearchDocument);
            if (!animation)
            {
                return null;
            }

            string licenseUrl = FirstNonEmpty(
                GetScalarString(metadata, "licenseurl"),
                GetScalarString(searchDocument.SearchDocument, "licenseurl"));
            string rights = FirstNonEmpty(
                GetScalarString(metadata, "rights"),
                GetScalarString(searchDocument.SearchDocument, "rights"));


            IReadOnlyList<string> languages = MergeStrings(
                GetStringValues(metadata, "language"),
                GetStringValues(searchDocument.SearchDocument, "language"));
            if (!string.IsNullOrWhiteSpace(preferredLanguage) &&
                !languages.Any(language =>
                    AudiovisualProviderDefinition.LanguageMatches(language, preferredLanguage)))
            {
                return null;
            }

            if (!root.TryGetProperty("files", out JsonElement files) ||
                files.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? fileName = SelectVideoFile(files);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            Uri mediaUri = new(
                _options.InternetArchiveBaseUri,
                $"download/{Uri.EscapeDataString(searchDocument.Identifier)}/{EscapePathSegment(fileName)}");
            bool arabicCartoonLane =
                requestedIdentity.Kind == AudiovisualMediaKind.Cartoon &&
                languages.Any(AudiovisualProviderDefinition.IsArabicLanguage);

            return new AudiovisualSource
            {
                Evidence = BuildIndependentEvidence(metadata, searchDocument.SearchDocument,
                    candidateIdentity, candidateUnit, animation),
                Provenance = AudiovisualSourceProvenance.BuiltInInternetArchive,
                ProviderId = _provider.Id,
                ProviderName = _provider.Name,
                Location = mediaUri,
                AccessMode = AudiovisualSourceAccessMode.DirectMedia,
                Authorization = ProviderAuthorization.Unspecified,
                License = licenseUrl,
                Rights = rights,
                ContentType = GetVideoContentType(fileName),
                Languages = languages,
                Identity = candidateIdentity,
                Unit = candidateUnit,
                Lane = arabicCartoonLane ? "arabic-cartoon" : string.Empty
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AudiovisualSourceEvidence? BuildIndependentEvidence(JsonElement metadata,
        JsonElement searchDocument, AudiovisualIdentity identity, AudiovisualUnit unit, bool animation)
    {
        // Archive item fields are independent of the requested catalog selection.
        // Its broad "movies" mediatype alone does not establish a feature film.
        bool feature = HasFeatureFilmSignal(metadata) || HasFeatureFilmSignal(searchDocument);
        bool episode = unit.SeasonNumber.HasValue && unit.EpisodeNumber.HasValue;
        if ((identity.ContentForm == AudiovisualContentForm.Feature && !feature) ||
            (identity.ContentForm == AudiovisualContentForm.Series && !episode) ||
            string.IsNullOrWhiteSpace(identity.Title) || !identity.Year.HasValue)
            return null;

        return new AudiovisualSourceEvidence
        {
            Origin = SourceEvidenceOrigin.ProviderItem,
            Identity = identity with { IsAnimated = identity.Kind == AudiovisualMediaKind.Cartoon && animation },
            Unit = identity.ContentForm == AudiovisualContentForm.Feature ? AudiovisualUnit.Feature : unit,
            SpecialUnitEstablished = episode && unit.SeasonNumber == 0
        };
    }

    private static bool HasFeatureFilmSignal(JsonElement item) =>
        GetStringValues(item, "subject").Concat(GetStringValues(item, "genre"))
            .Concat(GetStringValues(item, "type"))
            .Any(value => System.Text.RegularExpressions.Regex.IsMatch(value,
                @"\b(?:feature[\s_-]+film|full[\s_-]+length[\s_-]+film)s?\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    internal static bool TryGetExplicitAuthorization(
        string? licenseUrl,
        string? rights,
        out ProviderAuthorization authorization)
    {
        string normalizedLicense = (licenseUrl ?? string.Empty).Trim().ToLowerInvariant();
        string normalizedRights = (rights ?? string.Empty).Trim().ToLowerInvariant();

        if (normalizedLicense.Contains("creativecommons.org/publicdomain", StringComparison.Ordinal) ||
            normalizedLicense.Contains("creativecommons.org/zero", StringComparison.Ordinal) ||
            normalizedRights.Contains("public domain", StringComparison.Ordinal) ||
            normalizedRights.Contains("cc0", StringComparison.Ordinal))
        {
            authorization = ProviderAuthorization.PublicDomain;
            return true;
        }

        if (normalizedLicense.Contains("creativecommons.org/licenses/", StringComparison.Ordinal) ||
            normalizedRights.Contains("creative commons", StringComparison.Ordinal) ||
            ContainsCreativeCommonsAbbreviation(normalizedRights))
        {
            authorization = ProviderAuthorization.CreativeCommons;
            return true;
        }

        if (Uri.TryCreate(licenseUrl, UriKind.Absolute, out Uri? explicitLicense) &&
            explicitLicense.Scheme is "http" or "https" &&
            (normalizedRights.Contains("licensed under", StringComparison.Ordinal) ||
             normalizedRights.Contains("used with permission", StringComparison.Ordinal)))
        {
            authorization = ProviderAuthorization.Licensed;
            return true;
        }

        authorization = ProviderAuthorization.Unspecified;
        return false;
    }

    private static AudiovisualIdentity BuildIdentity(
        JsonElement metadata,
        AudiovisualMediaKind kind)
    {
        string title = GetScalarString(metadata, "title");
        return new AudiovisualIdentity
        {
            Kind = kind,
            ContentForm = kind switch
            {
                AudiovisualMediaKind.Movie => AudiovisualContentForm.Feature,
                AudiovisualMediaKind.Television => AudiovisualContentForm.Series,
                AudiovisualMediaKind.Cartoon when
                    HasTelevisionSignal(metadata) ||
                    GetInteger(metadata, "season_number", "season").HasValue ||
                    GetInteger(metadata, "episode_number", "episode").HasValue =>
                    AudiovisualContentForm.Series,
                AudiovisualMediaKind.Cartoon => AudiovisualContentForm.Feature,
                _ => AudiovisualContentForm.Unknown
            },
            Title = title,
            OriginalTitle = GetScalarString(metadata, "original_title"),
            AlternateTitles = GetStringValues(metadata, "alternate_titles"),
            Year = GetYear(metadata),
            TmdbId = GetInteger(metadata, "tmdb_id", "tmdbid")
        };
    }

    private static AudiovisualIdentity MergeIdentity(
        AudiovisualIdentity primary,
        AudiovisualIdentity fallback)
    {
        return primary with
        {
            Title = FirstNonEmpty(primary.Title, fallback.Title),
            OriginalTitle = FirstNonEmpty(primary.OriginalTitle, fallback.OriginalTitle),
            AlternateTitles = MergeStrings(primary.AlternateTitles, fallback.AlternateTitles),
            ContentForm = primary.ContentForm != AudiovisualContentForm.Unknown
                ? primary.ContentForm
                : fallback.ContentForm,
            Year = primary.Year ?? fallback.Year,
            TmdbId = primary.TmdbId ?? fallback.TmdbId
        };
    }

    private static AudiovisualUnit BuildUnit(JsonElement metadata)
    {
        return new AudiovisualUnit
        {
            SeasonNumber = GetInteger(metadata, "season_number", "season"),
            EpisodeNumber = GetInteger(metadata, "episode_number", "episode"),
            Title = FirstNonEmpty(
                GetScalarString(metadata, "episode_title"),
                GetScalarString(metadata, "unit_title"))
        };
    }

    private static AudiovisualUnit MergeUnit(
        AudiovisualUnit primary,
        AudiovisualUnit fallback)
    {
        return primary with
        {
            SeasonNumber = primary.SeasonNumber ?? fallback.SeasonNumber,
            EpisodeNumber = primary.EpisodeNumber ?? fallback.EpisodeNumber,
            Title = FirstNonEmpty(primary.Title, fallback.Title)
        };
    }

    private static AudiovisualIdentity ApplyUnitContentForm(
        AudiovisualIdentity identity,
        AudiovisualUnit unit)
    {
        return !unit.IsFeature &&
               identity.Kind != AudiovisualMediaKind.Movie
            ? identity with { ContentForm = AudiovisualContentForm.Series }
            : identity;
    }

    private static string? SelectVideoFile(JsonElement files)
    {
        return files
            .EnumerateArray()
            .Where(file => file.ValueKind == JsonValueKind.Object)
            .Select(file => new
            {
                Name = GetScalarString(file, "name"),
                Format = GetScalarString(file, "format"),
                Source = GetScalarString(file, "source"),
                Size = GetLong(file, "size")
            })
            .Where(file => IsSupportedVideoFile(file.Name, file.Format))
            .OrderByDescending(file => file.Source.Equals("original", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(file => Path.GetExtension(file.Name).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(file => file.Size)
            .Select(file => file.Name)
            .FirstOrDefault();
    }

    private static bool IsSupportedVideoFile(string fileName, string format)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Contains("__ia_thumb", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("sample", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string extension = Path.GetExtension(fileName);
        if (SupportedVideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        string normalizedFormat = format.ToLowerInvariant();
        return normalizedFormat.Contains("mpeg4", StringComparison.Ordinal) ||
               normalizedFormat.Contains("h.264", StringComparison.Ordinal) ||
               normalizedFormat.Contains("matroska", StringComparison.Ordinal) ||
               normalizedFormat.Contains("webm", StringComparison.Ordinal) ||
               normalizedFormat.Contains("ogg video", StringComparison.Ordinal);
    }

    private static bool HasAnimationSignal(JsonElement metadata)
    {
        return GetStringValues(metadata, "subject")
            .Concat(GetStringValues(metadata, "collection"))
            .Any(value =>
                value.Contains("animation", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("animated", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("cartoon", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("كرتون", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("أنمي", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("انمي", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("سبيستون", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("سبيس تون", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("رسوم متحركة", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasTelevisionSignal(JsonElement metadata)
    {
        return GetStringValues(metadata, "subject")
            .Concat(GetStringValues(metadata, "collection"))
            .Any(value =>
                value.Contains("television", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("tv series", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("tv show", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("television series", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetVideoContentType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".ogv" => "video/ogg",
            ".mkv" => "video/x-matroska",
            _ => "application/octet-stream"
        };
    }

    private static int? GetYear(JsonElement element)
    {
        int? year = GetInteger(element, "year");
        if (year.HasValue)
        {
            return year;
        }

        string date = GetScalarString(element, "date");
        if (date.Length >= 4 &&
            int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int? GetInteger(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int numeric))
            {
                return numeric;
            }

            string text = GetFirstString(value);
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (!TryGetPropertyIgnoreCase(element, name, out JsonElement value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long numeric))
        {
            return numeric;
        }

        return long.TryParse(
            GetFirstString(value),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long parsed)
            ? parsed
            : 0;
    }

    private static string GetScalarString(JsonElement element, string name)
    {
        return TryGetPropertyIgnoreCase(element, name, out JsonElement value)
            ? GetFirstString(value)
            : string.Empty;
    }

    private static IReadOnlyList<string> GetStringValues(JsonElement element, string name)
    {
        if (!TryGetPropertyIgnoreCase(element, name, out JsonElement value))
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value
                .EnumerateArray()
                .Select(GetFirstString)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        string scalar = GetFirstString(value);
        return scalar
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetFirstString(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetRawText();
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in value.EnumerateArray())
            {
                string candidate = GetFirstString(child);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }
        }

        return string.Empty;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static IReadOnlyList<string> MergeStrings(
        IEnumerable<string> first,
        IEnumerable<string> second)
    {
        return first
            .Concat(second)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static bool ContainsCreativeCommonsAbbreviation(string rights)
    {
        return rights.Contains("cc by", StringComparison.Ordinal) ||
               rights.Contains("cc-by", StringComparison.Ordinal) ||
               rights.Contains("cc by-sa", StringComparison.Ordinal) ||
               rights.Contains("cc-by-sa", StringComparison.Ordinal) ||
               rights.Contains("cc by-nc", StringComparison.Ordinal) ||
               rights.Contains("cc-by-nc", StringComparison.Ordinal);
    }

    private static string EscapePathSegment(string value)
    {
        return string.Join(
            "/",
            value.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
    }

    private sealed record ArchiveSearchDocument(
        string Identifier,
        JsonElement SearchDocument,
        AudiovisualIdentity Identity,
        AudiovisualUnit Unit);
}
