using System.Net.Http.Headers;
using System.Text.Json;

namespace UniversalMediaOS.Core.OtherMedia.Books;

public sealed class GoogleBooksBookProvider : IBookCatalogProvider
{
    private static readonly Uri DefaultBaseUri = new("https://www.googleapis.com/books/v1/");
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly Uri _baseUri;

    public GoogleBooksBookProvider(
        HttpClient httpClient,
        string? apiKey = null,
        Uri? baseUri = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey?.Trim() ?? string.Empty;
        _baseUri = EnsureTrailingSlash(baseUri ?? DefaultBaseUri);
    }

    public BookCatalogSource Source => BookCatalogSource.GoogleBooks;

    public async Task<BookSearchPage> SearchAsync(
        string query,
        int offset = 0,
        int limit = 24,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new BookSearchPage();
        }

        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 40);
        string relative =
            $"volumes?q={Uri.EscapeDataString(query.Trim())}" +
            $"&startIndex={offset}&maxResults={limit}&printType=books";
        if (!string.IsNullOrEmpty(_apiKey))
        {
            relative += $"&key={Uri.EscapeDataString(_apiKey)}";
        }

        return await RequestPageAsync(new Uri(_baseUri, relative), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BookRecord?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        string normalizedId = (id ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedId) ||
            normalizedId.Contains('/'))
        {
            return null;
        }

        string relative = $"volumes/{Uri.EscapeDataString(normalizedId)}";
        if (!string.IsNullOrEmpty(_apiKey))
        {
            relative += $"?key={Uri.EscapeDataString(_apiKey)}";
        }

        try
        {
            using HttpRequestMessage request = CreateRequest(new Uri(_baseUri, relative));
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return ParseVolume(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<BookSearchPage> RequestPageAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_httpClient.Timeout);
        CancellationToken requestToken = deadline.Token;
        try
        {
            using HttpRequestMessage request = CreateRequest(uri);
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new BookSearchPage { Outcome = BookSearchOutcome.Unavailable };
            }

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(requestToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: requestToken)
                .ConfigureAwait(false);
            JsonElement root = document.RootElement;
            int total = TryGetInt32(root, "totalItems") ?? 0;

            if (!root.TryGetProperty("items", out JsonElement itemsElement) ||
                itemsElement.ValueKind != JsonValueKind.Array)
            {
                return new BookSearchPage { Total = total, Outcome = total > 0
                    ? BookSearchOutcome.Unavailable : BookSearchOutcome.Completed };
            }

            var items = new List<BookRecord>();
            foreach (JsonElement item in itemsElement.EnumerateArray())
            {
                BookRecord? parsed = ParseVolume(item);
                if (parsed != null)
                {
                    items.Add(parsed);
                }
            }

            return new BookSearchPage
            {
                Items = items,
                Total = total
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new BookSearchPage { Outcome = BookSearchOutcome.TimedOut };
        }
        catch (HttpRequestException)
        {
            return new BookSearchPage { Outcome = BookSearchOutcome.Unavailable };
        }
        catch (JsonException)
        {
            return new BookSearchPage { Outcome = BookSearchOutcome.Unavailable };
        }
    }

    private static BookRecord? ParseVolume(JsonElement root)
    {
        string id = GetString(root, "id");
        if (string.IsNullOrWhiteSpace(id) ||
            !root.TryGetProperty("volumeInfo", out JsonElement info) ||
            info.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string title = GetString(info, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string publishedDate = GetString(info, "publishedDate");
        int? publishedYear = ParseYear(publishedDate);
        var identifiers = new List<BookIdentifier>
        {
            new() { Scheme = "GoogleBooks", Value = id }
        };
        if (info.TryGetProperty("industryIdentifiers", out JsonElement identifiersElement) &&
            identifiersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement identifier in identifiersElement.EnumerateArray())
            {
                string scheme = GetString(identifier, "type").Replace('_', '-');
                string value = NormalizeIsbn(GetString(identifier, "identifier"));
                if (!string.IsNullOrWhiteSpace(scheme) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    identifiers.Add(new BookIdentifier { Scheme = scheme, Value = value });
                }
            }
        }

        string coverUrl = string.Empty;
        if (info.TryGetProperty("imageLinks", out JsonElement images) &&
            images.ValueKind == JsonValueKind.Object)
        {
            coverUrl = FirstNonEmpty(
                GetString(images, "extraLarge"),
                GetString(images, "large"),
                GetString(images, "medium"),
                GetString(images, "thumbnail"),
                GetString(images, "smallThumbnail"));
            if (coverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                coverUrl = "https://" + coverUrl["http://".Length..];
            }
        }

        string previewUrl = GetString(info, "previewLink");
        string infoUrl = GetString(info, "infoLink");
        var assets = new List<BookAsset>();
        if (root.TryGetProperty("accessInfo", out JsonElement accessInfo) &&
            accessInfo.ValueKind == JsonValueKind.Object)
        {
            string webReaderLink = GetString(accessInfo, "webReaderLink");
            if (!string.IsNullOrWhiteSpace(webReaderLink))
            {
                previewUrl = webReaderLink;
            }

            bool publicDomain =
                GetBoolean(accessInfo, "publicDomain") ||
                GetString(accessInfo, "accessViewStatus")
                    .Equals("FULL_PUBLIC_DOMAIN", StringComparison.OrdinalIgnoreCase);
            if (publicDomain)
            {
                TryAddPublicAsset(assets, id, accessInfo, "epub", BookFileFormat.Epub);
                TryAddPublicAsset(assets, id, accessInfo, "pdf", BookFileFormat.Pdf);
            }
        }

        return new BookRecord
        {
            Id = id,
            Source = BookCatalogSource.GoogleBooks,
            Title = title.Trim(),
            Subtitle = GetString(info, "subtitle").Trim(),
            Authors = GetStringArray(info, "authors").Take(8).ToArray(),
            Description = GetString(info, "description"),
            Publisher = GetString(info, "publisher"),
            PublishedDate = publishedDate,
            PublishedYear = publishedYear,
            PageCount = TryGetInt32(info, "pageCount"),
            Language = GetString(info, "language"),
            CoverUrl = coverUrl,
            PreviewUrl = previewUrl,
            InfoUrl = infoUrl,
            Subjects = GetStringArray(info, "categories").Take(8).ToArray(),
            Identifiers = identifiers
                .DistinctBy(identifier => $"{identifier.Scheme}:{identifier.Value}", StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Assets = assets
        };
    }

    private static void TryAddPublicAsset(
        ICollection<BookAsset> assets,
        string bookId,
        JsonElement accessInfo,
        string propertyName,
        BookFileFormat format)
    {
        if (!accessInfo.TryGetProperty(propertyName, out JsonElement formatInfo) ||
            formatInfo.ValueKind != JsonValueKind.Object ||
            !GetBoolean(formatInfo, "isAvailable"))
        {
            return;
        }

        string location = GetString(formatInfo, "downloadLink");
        if (!TryGetSafeHttpUri(location, out Uri? uri))
        {
            return;
        }

        assets.Add(new BookAsset
        {
            Id = $"google:{bookId}:{propertyName}",
            BookId = bookId,
            DisplayName = $"{format.ToString().ToUpperInvariant()} edition",
            Format = format,
            Access = BookAccessKind.PublicDomain,
            Location = uri.AbsoluteUri,
            SourceLabel = "Google Books",
            RightsStatement = "Google Books marks this volume as public domain.",
            IsDownloadAllowed = true
        });
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("UniversalMediaOS/1.0 (book metadata client)");
        return request;
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("The Google Books base URI must be absolute.", nameof(uri));
        }

        string value = uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri.AbsoluteUri
            : uri.AbsoluteUri + "/";
        return new Uri(value, UriKind.Absolute);
    }

    private static bool TryGetSafeHttpUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed) &&
            parsed.Scheme is "http" or "https")
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static int? ParseYear(string value)
    {
        if (value.Length >= 4 && int.TryParse(value.AsSpan(0, 4), out int year))
        {
            return year;
        }

        return null;
    }

    private static string NormalizeIsbn(string value)
    {
        return new string((value ?? string.Empty)
            .Where(character => char.IsAsciiLetterOrDigit(character))
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed)
            ? parsed
            : null;
    }

    private static bool GetBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) &&
               value.ValueKind is JsonValueKind.True;
    }
}
