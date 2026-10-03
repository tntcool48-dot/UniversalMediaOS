using System.Net.Http.Headers;
using System.Text.Json;

namespace UniversalMediaOS.Core.OtherMedia.Books;

public sealed class InternetArchiveBookService
{
    private static readonly Uri DefaultSearchUri = new("https://archive.org/advancedsearch.php");
    private static readonly Uri DefaultMetadataUri = new("https://archive.org/metadata/");
    private readonly HttpClient _httpClient;
    private readonly Uri _searchBaseUri;
    private readonly Uri _metadataBaseUri;
    private readonly Uri _downloadBaseUri;

    public InternetArchiveBookService(
        HttpClient httpClient,
        Uri? searchBaseUri = null,
        Uri? metadataBaseUri = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _searchBaseUri = RequireAbsolute(searchBaseUri ?? DefaultSearchUri, nameof(searchBaseUri));
        _metadataBaseUri = EnsureTrailingSlash(
            RequireAbsolute(metadataBaseUri ?? DefaultMetadataUri, nameof(metadataBaseUri)));
        _downloadBaseUri = BuildDownloadBaseUri(_metadataBaseUri);
    }

    public async Task<IReadOnlyList<BookAsset>> FindAssetsAsync(
        BookRecord book,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        string identityQuery = BuildIdentityQuery(book);
        if (string.IsNullOrWhiteSpace(identityQuery))
        {
            return Array.Empty<BookAsset>();
        }

        string query =
            $"mediatype:texts AND access-restricted-item:false AND ({identityQuery})";
        var parameters = new Dictionary<string, string>
        {
            ["q"] = query,
            ["fl[]"] = "identifier",
            ["rows"] = "10",
            ["page"] = "1",
            ["output"] = "json"
        };
        Uri requestUri = AppendQuery(_searchBaseUri, parameters);

        try
        {
            using HttpRequestMessage request = CreateRequest(requestUri);
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<BookAsset>();
            }

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<string> identifiers = ParseCandidateIdentifiers(document.RootElement);
            if (identifiers.Count == 0)
            {
                return Array.Empty<BookAsset>();
            }

            Task<IReadOnlyList<BookAsset>>[] tasks = identifiers
                .Select(identifier => InspectItemAsync(
                    identifier,
                    book.Id,
                    cancellationToken))
                .ToArray();
            IReadOnlyList<BookAsset>[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results
                .SelectMany(result => result)
                .DistinctBy(asset => asset.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(asset => asset.Format == BookFileFormat.Epub ? 0 : 1)
                .ThenBy(asset => asset.SizeBytes ?? long.MaxValue)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return Array.Empty<BookAsset>();
        }
        catch (JsonException)
        {
            return Array.Empty<BookAsset>();
        }
    }

    public static bool HasExplicitOpenRights(
        string? licenseUrl,
        string? rights,
        IEnumerable<string>? collections)
    {
        string license = licenseUrl?.Trim() ?? string.Empty;
        string statement = rights?.Trim() ?? string.Empty;
        if (license.Contains("creativecommons.org/publicdomain/", StringComparison.OrdinalIgnoreCase) ||
            license.Contains("creativecommons.org/licenses/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (statement.Contains("public domain", StringComparison.OrdinalIgnoreCase) ||
            statement.Contains("creative commons", StringComparison.OrdinalIgnoreCase) ||
            statement.Contains("CC0", StringComparison.OrdinalIgnoreCase) ||
            statement.Contains("CC BY", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return collections?.Any(collection =>
            collection.Equals("gutenberg", StringComparison.OrdinalIgnoreCase) ||
            collection.Equals("project_gutenberg", StringComparison.OrdinalIgnoreCase) ||
            collection.Equals("publicdomain", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private async Task<IReadOnlyList<BookAsset>> InspectItemAsync(
        string identifier,
        string bookId,
        CancellationToken cancellationToken)
    {
        if (!IsSafeIdentifier(identifier))
        {
            return Array.Empty<BookAsset>();
        }

        Uri uri = new(_metadataBaseUri, Uri.EscapeDataString(identifier));
        try
        {
            using HttpRequestMessage request = CreateRequest(uri);
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<BookAsset>();
            }

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("metadata", out JsonElement metadata) ||
                metadata.ValueKind != JsonValueKind.Object ||
                IsRestricted(metadata))
            {
                return Array.Empty<BookAsset>();
            }

            string licenseUrl = GetFlexibleString(metadata, "licenseurl");
            string rights = FirstNonEmpty(
                GetFlexibleString(metadata, "rights"),
                GetFlexibleString(metadata, "usage"));
            IReadOnlyList<string> collections = GetFlexibleStringArray(metadata, "collection");
            if (!HasExplicitOpenRights(licenseUrl, rights, collections))
            {
                return Array.Empty<BookAsset>();
            }

            BookAccessKind access = IsPublicDomain(licenseUrl, rights, collections)
                ? BookAccessKind.PublicDomain
                : BookAccessKind.OpenLicense;
            string rightsStatement = FirstNonEmpty(
                rights,
                licenseUrl,
                access == BookAccessKind.PublicDomain
                    ? "Public domain"
                    : "Creative Commons/open license");
            if (!root.TryGetProperty("files", out JsonElement files) ||
                files.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<BookAsset>();
            }

            var assets = new List<BookAsset>();
            foreach (JsonElement file in files.EnumerateArray())
            {
                if (IsPrivate(file))
                {
                    continue;
                }

                string name = GetFlexibleString(file, "name");
                string formatName = GetFlexibleString(file, "format");
                BookFileFormat format = DetectFormat(name, formatName);
                if (format == BookFileFormat.Unknown || !IsSafeArchiveFileName(name))
                {
                    continue;
                }

                string encodedName = string.Join(
                    "/",
                    name.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Select(Uri.EscapeDataString));
                Uri location = new(
                    _downloadBaseUri,
                    $"{Uri.EscapeDataString(identifier)}/{encodedName}");
                assets.Add(new BookAsset
                {
                    Id = $"ia:{identifier}:{name}",
                    BookId = bookId,
                    DisplayName = BuildDisplayName(name, format),
                    Format = format,
                    Access = access,
                    Location = location.AbsoluteUri,
                    SourceLabel = "Internet Archive",
                    RightsStatement = rightsStatement,
                    SizeBytes = TryGetInt64(file, "size"),
                    IsDownloadAllowed = true
                });
            }

            return assets;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return Array.Empty<BookAsset>();
        }
        catch (JsonException)
        {
            return Array.Empty<BookAsset>();
        }
    }

    private static string BuildIdentityQuery(BookRecord book)
    {
        string? isbn = book.Identifiers
            .Where(identifier =>
                identifier.Scheme.Equals("ISBN-13", StringComparison.OrdinalIgnoreCase) ||
                identifier.Scheme.Equals("ISBN-10", StringComparison.OrdinalIgnoreCase))
            .Select(identifier => NormalizeIsbn(identifier.Value))
            .FirstOrDefault(value => value.Length is 10 or 13);
        if (!string.IsNullOrWhiteSpace(isbn))
        {
            return $"isbn:\"{EscapeQueryValue(isbn)}\"";
        }

        string title = (book.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        string query = $"title:\"{EscapeQueryValue(title)}\"";
        string author = book.Authors.FirstOrDefault()?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(author))
        {
            query += $" AND creator:\"{EscapeQueryValue(author)}\"";
        }

        return query;
    }

    private static IReadOnlyList<string> ParseCandidateIdentifiers(JsonElement root)
    {
        if (!root.TryGetProperty("response", out JsonElement response) ||
            response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("docs", out JsonElement docs) ||
            docs.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return docs.EnumerateArray()
            .Select(item => GetFlexibleString(item, "identifier"))
            .Where(IsSafeIdentifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsRestricted(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("access-restricted-item", out JsonElement value))
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.True ||
               (value.ValueKind == JsonValueKind.String &&
                value.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool IsPrivate(JsonElement file)
    {
        if (!file.TryGetProperty("private", out JsonElement value))
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.True ||
               (value.ValueKind == JsonValueKind.String &&
                value.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool IsPublicDomain(
        string licenseUrl,
        string rights,
        IEnumerable<string> collections)
    {
        return licenseUrl.Contains(
                   "creativecommons.org/publicdomain/",
                   StringComparison.OrdinalIgnoreCase) ||
               rights.Contains("public domain", StringComparison.OrdinalIgnoreCase) ||
               collections.Any(collection =>
                   collection.Equals("gutenberg", StringComparison.OrdinalIgnoreCase) ||
                   collection.Equals("project_gutenberg", StringComparison.OrdinalIgnoreCase) ||
                   collection.Equals("publicdomain", StringComparison.OrdinalIgnoreCase));
    }

    private static BookFileFormat DetectFormat(string name, string formatName)
    {
        string extension = Path.GetExtension(name);
        if (extension.Equals(".epub", StringComparison.OrdinalIgnoreCase) ||
            formatName.Contains("EPUB", StringComparison.OrdinalIgnoreCase))
        {
            return BookFileFormat.Epub;
        }

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
            formatName.Contains("PDF", StringComparison.OrdinalIgnoreCase))
        {
            return BookFileFormat.Pdf;
        }

        return BookFileFormat.Unknown;
    }

    private static string BuildDisplayName(string name, BookFileFormat format)
    {
        string fileName = Path.GetFileNameWithoutExtension(name)
            .Replace('_', ' ')
            .Trim();
        return string.IsNullOrWhiteSpace(fileName)
            ? $"{format.ToString().ToUpperInvariant()} edition"
            : fileName;
    }

    private static bool IsSafeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '_' or '-' or '.');
    }

    private static bool IsSafeArchiveFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > 512 ||
            Path.IsPathRooted(name))
        {
            return false;
        }

        string normalized = name.Replace('\\', '/');
        return normalized.Split('/').All(segment =>
            segment.Length > 0 &&
            segment != "." &&
            segment != "..");
    }

    private static string NormalizeIsbn(string value)
    {
        return new string((value ?? string.Empty)
            .Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string EscapeQueryValue(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static Uri AppendQuery(Uri baseUri, IReadOnlyDictionary<string, string> parameters)
    {
        string separator = string.IsNullOrEmpty(baseUri.Query) ? "?" : "&";
        string query = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri(baseUri.AbsoluteUri + separator + query, UriKind.Absolute);
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("UniversalMediaOS/1.0 (open-access book client)");
        return request;
    }

    private static Uri RequireAbsolute(Uri uri, string parameterName)
    {
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("The URI must be absolute.", parameterName);
        }

        return uri;
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        string value = uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri.AbsoluteUri
            : uri.AbsoluteUri + "/";
        return new Uri(value, UriKind.Absolute);
    }

    private static Uri BuildDownloadBaseUri(Uri metadataBaseUri)
    {
        var builder = new UriBuilder(metadataBaseUri);
        string path = builder.Path.TrimEnd('/');
        int slash = path.LastIndexOf('/');
        string parent = slash >= 0 ? path[..(slash + 1)] : "/";
        builder.Path = parent + "download/";
        builder.Query = string.Empty;
        builder.Fragment = string.Empty;
        return EnsureTrailingSlash(builder.Uri);
    }

    private static string GetFlexibleString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return string.Empty;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    return item.GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> GetFlexibleStringArray(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(text) ? Array.Empty<string>() : [text];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static long? TryGetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
