using System.Text.RegularExpressions;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace UniversalMediaOS.Core.OtherMedia.Books;

public sealed partial class BookCatalogService
{
    private readonly IReadOnlyList<IBookCatalogProvider> _providers;

    public BookCatalogService(IEnumerable<IBookCatalogProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers
            .GroupBy(provider => provider.Source)
            .Select(group => group.First())
            .ToArray();
        if (_providers.Count == 0)
        {
            throw new ArgumentException(
                "At least one book catalog provider must be supplied.",
                nameof(providers));
        }
    }

    public async Task<BookSearchPage> SearchAsync(
        string query,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        BookSearchPage result = new();
        await foreach (BookSearchUpdate update in SearchUpdatesAsync(query, limit, cancellationToken))
        {
            result = update.Page;
        }
        return result;
    }

    public async IAsyncEnumerable<BookSearchUpdate> SearchUpdatesAsync(
        string query,
        int limit = 100,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string normalizedQuery = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            yield break;
        }

        limit = Math.Clamp(limit, 1, 100);
        int perProvider = Math.Clamp(
            (int)Math.Ceiling(limit / (double)_providers.Count) + 4,
            1,
            50);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<BookSearchPage>[] tasks = _providers
            .Select(provider => SearchProviderAsync(
                provider,
                normalizedQuery,
                perProvider,
                operation.Token))
            .ToArray();
        var pending = tasks.ToList();
        var pages = new BookSearchPage?[_providers.Count];
        try
        {
            while (pending.Count > 0)
            {
                Task<BookSearchPage> completed = await Task.WhenAny(pending)
                    .WaitAsync(operation.Token).ConfigureAwait(false);
                int index = Array.IndexOf(tasks, completed);
                pages[index] = await completed.ConfigureAwait(false);
                pending.Remove(completed);
                operation.Token.ThrowIfCancellationRequested();

                // Merge in configured provider order so arrival timing cannot change the final match.
                var available = pages.OfType<BookSearchPage>().ToArray();
                yield return new BookSearchUpdate(new BookSearchPage
                {
                    Items = MergeDuplicates(available.SelectMany(page => page.Items))
                        .OrderByDescending(book => Score(book, normalizedQuery))
                        .ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)
                        .Take(limit).ToArray(),
                    Total = available.Sum(page => page.Total),
                    Outcome = available.Any(page => page.Outcome == BookSearchOutcome.Unavailable)
                        ? BookSearchOutcome.Unavailable
                        : available.Any(page => page.Outcome == BookSearchOutcome.TimedOut)
                            ? BookSearchOutcome.TimedOut : BookSearchOutcome.Completed
                }, pages.Select((page, i) => page == null ? null :
                    new BookProviderSearchOutcome(_providers[i].Source, page.Outcome))
                    .OfType<BookProviderSearchOutcome>().ToArray(), pending.Count);
            }
        }
        finally
        {
            // Disposing a consumer cancels only this search, including providers still preparing.
            operation.Cancel();
        }
    }

    public async Task<BookRecord?> GetDetailsAsync(
        BookRecord book,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        IBookCatalogProvider? provider = _providers.FirstOrDefault(
            candidate => candidate.Source == book.Source);
        if (provider == null || book.Source == BookCatalogSource.Local)
        {
            return book;
        }

        try
        {
            BookRecord? details = await provider
                .GetByIdAsync(book.Id, cancellationToken)
                .ConfigureAwait(false);
            return details == null ? book : MergePair(book, details);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return book;
        }
        catch (InvalidOperationException)
        {
            return book;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or JsonException or IOException)
        {
            return book;
        }
    }

    public static IReadOnlyList<BookRecord> MergeDuplicates(IEnumerable<BookRecord> books)
    {
        ArgumentNullException.ThrowIfNull(books);
        var output = new List<BookRecord>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (BookRecord book in books)
        {
            string key = GetDeduplicationKey(book);
            if (!indexByKey.TryGetValue(key, out int index))
            {
                indexByKey[key] = output.Count;
                output.Add(book);
                continue;
            }

            output[index] = MergePair(output[index], book);
        }

        return output;
    }

    private static async Task<BookSearchPage> SearchProviderAsync(
        IBookCatalogProvider provider,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            BookSearchPage page = await provider
                .SearchAsync(query, limit: limit, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return page;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            return new BookSearchPage { Outcome = BookSearchOutcome.TimedOut };
        }
        catch (Exception)
        {
            return new BookSearchPage { Outcome = BookSearchOutcome.Unavailable };
        }
    }

    public static string GetDeduplicationKey(BookRecord book)
    {
        string? isbn13 = book.Identifiers.FirstOrDefault(identifier =>
            identifier.Scheme.Equals("ISBN-13", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(identifier.Value))?.Value;
        if (!string.IsNullOrWhiteSpace(isbn13))
        {
            return "isbn13:" + NormalizeIdentifier(isbn13);
        }

        string? isbn10 = book.Identifiers.FirstOrDefault(identifier =>
            identifier.Scheme.Equals("ISBN-10", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(identifier.Value))?.Value;
        if (!string.IsNullOrWhiteSpace(isbn10))
        {
            return "isbn10:" + NormalizeIdentifier(isbn10);
        }

        string title = NormalizeText(book.Title);
        string author = NormalizeText(book.Authors.FirstOrDefault() ?? string.Empty);
        string year = book.PublishedYear?.ToString(
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        return $"title:{title}|author:{author}|year:{year}";
    }

    private static BookRecord MergePair(BookRecord first, BookRecord second)
    {
        BookRecord preferred = CompletenessScore(second) > CompletenessScore(first)
            ? second
            : first;
        BookRecord fallback = ReferenceEquals(preferred, first) ? second : first;

        return preferred with
        {
            Subtitle = FirstNonEmpty(preferred.Subtitle, fallback.Subtitle),
            Authors = MergeStrings(preferred.Authors, fallback.Authors),
            Description = FirstNonEmpty(preferred.Description, fallback.Description),
            Publisher = FirstNonEmpty(preferred.Publisher, fallback.Publisher),
            PublishedDate = FirstNonEmpty(preferred.PublishedDate, fallback.PublishedDate),
            PublishedYear = preferred.PublishedYear ?? fallback.PublishedYear,
            PageCount = preferred.PageCount ?? fallback.PageCount,
            Language = FirstNonEmpty(preferred.Language, fallback.Language),
            CoverUrl = FirstNonEmpty(preferred.CoverUrl, fallback.CoverUrl),
            PreviewUrl = FirstNonEmpty(preferred.PreviewUrl, fallback.PreviewUrl),
            InfoUrl = FirstNonEmpty(preferred.InfoUrl, fallback.InfoUrl),
            Subjects = MergeStrings(preferred.Subjects, fallback.Subjects),
            Identifiers = preferred.Identifiers
                .Concat(fallback.Identifiers)
                .DistinctBy(
                    identifier => $"{identifier.Scheme}:{identifier.Value}",
                    StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Assets = preferred.Assets
                .Concat(fallback.Assets)
                .DistinctBy(asset => asset.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static int CompletenessScore(BookRecord book)
    {
        int score = 0;
        score += string.IsNullOrWhiteSpace(book.Description) ? 0 : 5;
        score += string.IsNullOrWhiteSpace(book.CoverUrl) ? 0 : 4;
        score += book.Authors.Count > 0 ? 3 : 0;
        score += book.PageCount is > 0 ? 2 : 0;
        score += book.PublishedYear is > 0 ? 1 : 0;
        score += book.Subjects.Count > 0 ? 1 : 0;
        score += book.Assets.Count > 0 ? 3 : 0;
        return score;
    }

    private static int Score(BookRecord book, string query)
    {
        string normalizedQuery = NormalizeText(query);
        string title = NormalizeText(book.Title);
        int score = CompletenessScore(book);
        if (title.Equals(normalizedQuery, StringComparison.Ordinal))
        {
            return score + 1000;
        }

        if (title.StartsWith(normalizedQuery + " ", StringComparison.Ordinal))
        {
            score += 700;
        }
        else if (title.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 450;
        }

        if (book.Authors.Any(author =>
                NormalizeText(author).Contains(normalizedQuery, StringComparison.Ordinal)))
        {
            score += 250;
        }

        return score;
    }

    private static IReadOnlyList<string> MergeStrings(
        IEnumerable<string> first,
        IEnumerable<string> second)
    {
        return first.Concat(second)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FirstNonEmpty(string first, string second)
    {
        return !string.IsNullOrWhiteSpace(first) ? first : second;
    }

    private static string NormalizeIdentifier(string value)
    {
        return new string((value ?? string.Empty)
            .Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string NormalizeText(string value)
    {
        return NonAlphaNumericRegex()
            .Replace((value ?? string.Empty).ToLowerInvariant(), " ")
            .Trim();
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumericRegex();
}
