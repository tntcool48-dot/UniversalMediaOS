using System.Net.Http.Headers;
using System.Text.Json;

namespace UniversalMediaOS.Core.OtherMedia.Books;

public sealed class OpenLibraryBookProvider : IBookCatalogProvider
{
    private static readonly Uri DefaultBaseUri = new("https://openlibrary.org/");
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    public OpenLibraryBookProvider(HttpClient httpClient, Uri? baseUri = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = EnsureTrailingSlash(baseUri ?? DefaultBaseUri);
    }

    public BookCatalogSource Source => BookCatalogSource.OpenLibrary;

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
        limit = Math.Clamp(limit, 1, 50);
        int page = (offset / limit) + 1;
        string fields = string.Join(
            ",",
            "key",
            "title",
            "subtitle",
            "author_name",
            "first_publish_year",
            "first_publish_date",
            "isbn",
            "cover_i",
            "language",
            "subject",
            "publisher",
            "number_of_pages_median");
        string relative =
            $"search.json?q={Uri.EscapeDataString(query.Trim())}" +
            $"&page={page}&limit={limit}&fields={Uri.EscapeDataString(fields)}";

        try
        {
            using HttpRequestMessage request = CreateRequest(new Uri(_baseUri, relative));
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new BookSearchPage();
            }

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            JsonElement root = document.RootElement;
            int total = TryGetInt32(root, "numFound") ?? 0;
            if (!root.TryGetProperty("docs", out JsonElement docs) ||
                docs.ValueKind != JsonValueKind.Array)
            {
                return new BookSearchPage { Total = total };
            }

            var items = new List<BookRecord>();
            foreach (JsonElement item in docs.EnumerateArray())
            {
                BookRecord? parsed = ParseSearchDocument(item);
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
        catch (HttpRequestException)
        {
            return new BookSearchPage();
        }
        catch (JsonException)
        {
            return new BookSearchPage();
        }
    }

    public async Task<BookRecord?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        string normalizedId = NormalizeWorkId(id);
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return null;
        }

        BookSearchPage result = await SearchAsync(
                $"key:{normalizedId}",
                limit: 1,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.Items.FirstOrDefault();
    }

    private static BookRecord? ParseSearchDocument(JsonElement item)
    {
        string id = GetString(item, "key");
        string title = GetString(item, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        IReadOnlyList<string> authors = GetStringArray(item, "author_name");
        IReadOnlyList<string> isbns = GetStringArray(item, "isbn")
            .Select(NormalizeIsbn)
            .Where(value => value.Length is 10 or 13)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        IReadOnlyList<BookIdentifier> identifiers = isbns
            .Select(value => new BookIdentifier
            {
                Scheme = value.Length == 13 ? "ISBN-13" : "ISBN-10",
                Value = value
            })
            .Append(new BookIdentifier { Scheme = "OpenLibrary", Value = id })
            .ToArray();

        int? coverId = TryGetInt32(item, "cover_i");
        string coverUrl = coverId is > 0
            ? $"https://covers.openlibrary.org/b/id/{coverId.Value}-L.jpg"
            : string.Empty;
        int? publishedYear = TryGetInt32(item, "first_publish_year");

        return new BookRecord
        {
            Id = id,
            Source = BookCatalogSource.OpenLibrary,
            Title = title.Trim(),
            Subtitle = GetString(item, "subtitle").Trim(),
            Authors = authors.Take(8).ToArray(),
            Publisher = GetStringArray(item, "publisher").FirstOrDefault() ?? string.Empty,
            PublishedDate = GetString(item, "first_publish_date"),
            PublishedYear = publishedYear,
            PageCount = TryGetInt32(item, "number_of_pages_median"),
            Language = NormalizeLanguage(GetStringArray(item, "language").FirstOrDefault()),
            CoverUrl = coverUrl,
            InfoUrl = new Uri(DefaultBaseUri, id.TrimStart('/')).AbsoluteUri,
            Subjects = GetStringArray(item, "subject").Take(8).ToArray(),
            Identifiers = identifiers
        };
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
            throw new ArgumentException("The Open Library base URI must be absolute.", nameof(uri));
        }

        string value = uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri.AbsoluteUri
            : uri.AbsoluteUri + "/";
        return new Uri(value, UriKind.Absolute);
    }

    private static string NormalizeWorkId(string id)
    {
        string value = (id ?? string.Empty).Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute))
        {
            value = absolute.AbsolutePath;
        }

        value = value.Split('?', '#')[0].TrimEnd('/');
        if (value.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^5];
        }

        if (!value.StartsWith("/", StringComparison.Ordinal))
        {
            value = "/works/" + value;
        }

        return value;
    }

    private static string NormalizeIsbn(string value)
    {
        return new string((value ?? string.Empty)
            .Where(character => char.IsAsciiLetterOrDigit(character))
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string NormalizeLanguage(string? value)
    {
        string language = (value ?? string.Empty).Trim();
        return language.Equals("eng", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : language;
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

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), out number)
            ? number
            : null;
    }
}
