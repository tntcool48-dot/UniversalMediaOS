using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.OtherMedia.Books
{
    public sealed class AnnasArchiveBookProvider : IBookCatalogProvider
    {
        private readonly BookScraperEngine _engine;
        private readonly DomainHotSwapper _config;

        public BookCatalogSource Source => BookCatalogSource.AnnasArchive;

        public AnnasArchiveBookProvider(BookScraperEngine engine, DomainHotSwapper config)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        private string GetMirrorUrl()
        {
            string url = _config.GetSetting("AnnasArchiveUrl");
            return string.IsNullOrWhiteSpace(url) ? "https://annas-archive.org" : url.Trim();
        }

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

            string mirror = GetMirrorUrl();
            var results = await _engine.SearchAsync(query, mirror, cancellationToken);
            
            var records = results
                .Where(result => !string.IsNullOrWhiteSpace(result.Id))
                .Select(r => new BookRecord
                {
                    Id = r.Id,
                    Source = BookCatalogSource.AnnasArchive,
                    Title = r.Title,
                    Authors = r.Authors?.Count > 0
                        ? r.Authors
                        : new List<string> { "Unknown Author" },
                    PublishedYear = r.Year,
                    Language = NormalizeLanguage(r.Language),
                    Publisher = r.Publisher,
                    InfoUrl = $"{mirror.TrimEnd('/')}/book/{Uri.EscapeDataString(r.Id)}",
                    Identifiers = BuildIdentifiers(r),
                    Description = $"File format: {r.Format.ToUpperInvariant()}, Size: {r.Size}"
                })
                .Skip(Math.Max(0, offset))
                .Take(Math.Max(1, limit))
                .ToList();

            return new BookSearchPage
            {
                Items = records,
                Total = records.Count
            };
        }

        public async Task<BookRecord?> GetByIdAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            
            BookSearchPage page = await SearchAsync(id, limit: int.MaxValue, cancellationToken: cancellationToken);
            BookRecord[] exact = page.Items.Where(item => SameResourceId(item.Id, id) ||
                item.Identifiers.Any(identifier => identifier.Scheme == "MD5" &&
                    IsMd5(id) && SameResourceId(identifier.Value, id))).ToArray();
            return exact.Length == 1 ? exact[0] : null;
        }

        public async Task<IReadOnlyList<BookAsset>> FindAssetsAsync(
            BookRecord book,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(book);

            cancellationToken.ThrowIfCancellationRequested();
            string[] scraperIds = book.Identifiers.Where(i =>
                    i.Scheme.Equals("ANNA-ID", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Value.Trim()).DistinctBy(value => IsMd5(value) ? value.ToLowerInvariant() : value).ToArray();
            if (scraperIds.Any(value => !IsResourceId(value))) return Array.Empty<BookAsset>();
            string[] md5Ids = book.Identifiers.Where(i =>
                    i.Scheme.Equals("MD5", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Value.Trim()).Where(IsMd5).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (scraperIds.Length > 1 || md5Ids.Length > 1 ||
                (scraperIds.Length == 1 && md5Ids.Length == 1 && IsMd5(scraperIds[0]) &&
                 !SameResourceId(scraperIds[0], md5Ids[0])) ||
                (book.Source == BookCatalogSource.AnnasArchive && scraperIds.Length == 1 &&
                 !SameResourceId(book.Id, scraperIds[0])) ||
                (book.Source == BookCatalogSource.AnnasArchive && IsMd5(book.Id) && md5Ids.Length == 1 &&
                 !SameResourceId(book.Id, md5Ids[0])))
                return Array.Empty<BookAsset>();
            if (book.Identifiers.Any(i => i.Scheme.Equals("MD5", StringComparison.OrdinalIgnoreCase) && !IsMd5(i.Value)) &&
                // Older opaque Anna records incorrectly copied their resource ID into MD5.
                !(book.Source == BookCatalogSource.AnnasArchive && scraperIds.Length == 1 &&
                  book.Identifiers.Where(i => i.Scheme.Equals("MD5", StringComparison.OrdinalIgnoreCase))
                      .All(i => SameResourceId(i.Value, scraperIds[0]))))
                return Array.Empty<BookAsset>();

            string resourceId = scraperIds.FirstOrDefault() ?? md5Ids.FirstOrDefault() ??
                (book.Source == BookCatalogSource.AnnasArchive && IsResourceId(book.Id) ? book.Id : "");
            string expectedMd5 = md5Ids.FirstOrDefault() ?? (IsMd5(resourceId) ? resourceId : "");
            BookRecord formatEvidence = book;
            if (string.IsNullOrEmpty(resourceId))
            {
                string[] requestedIsbns = IsbnValues(book).ToArray();
                // An invalid supplied ISBN must not silently become a title-only lookup.
                if (requestedIsbns.Any(value => NormalizeIsbn(value) == "")) return Array.Empty<BookAsset>();
                string searchQuery = requestedIsbns.FirstOrDefault() ??
                    $"{book.Title} {book.Authors.FirstOrDefault()}".Trim();
                if (string.IsNullOrWhiteSpace(searchQuery)) return Array.Empty<BookAsset>();
                var searchResults = await _engine.SearchAsync(searchQuery, GetMirrorUrl(), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var matches = searchResults.Where(result => MatchesEdition(book, result))
                    .DistinctBy(result => result.Id, StringComparer.Ordinal).ToArray();
                if (matches.Length != 1) return Array.Empty<BookAsset>();
                resourceId = matches[0].Id;
                expectedMd5 = IsMd5(matches[0].Md5) ? matches[0].Md5 : (IsMd5(resourceId) ? resourceId : "");
                formatEvidence = book with { Identifiers = book.Identifiers.Concat(
                    [new BookIdentifier { Scheme = "FORMAT", Value = matches[0].Format }]).ToArray() };
            }

            if (string.IsNullOrWhiteSpace(resourceId))
            {
                AppLogger.Log($"[AnnasArchiveBookProvider] Could not find a scraper resource id for book: {book.Title}", "INFO");
                return Array.Empty<BookAsset>();
            }

            string mirror = GetMirrorUrl();
            var links = await _engine.ResolveAsync(resourceId, mirror, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var assets = new List<BookAsset>();
            foreach (var link in links)
            {
                if (!Uri.TryCreate(link.Url, UriKind.Absolute, out Uri? location) ||
                    location.Scheme is not ("http" or "https"))
                {
                    continue;
                }

                BookFileFormat format = InferFormat(link, formatEvidence);
                if (format == BookFileFormat.Unknown) continue;
                assets.Add(new BookAsset
                {
                    Id = $"annas:{resourceId}:{link.Name}",
                    BookId = book.Id,
                    DisplayName = $"{book.Title} ({link.Name})",
                    Format = format,
                    Access = BookAccessKind.RightsUnverified,
                    Location = location.AbsoluteUri,
                    SourceLabel = "Anna's Archive",
                    RightsStatement = "Rights have not been verified for this indexed file.",
                    ExpectedMd5 = expectedMd5,
                    IsDownloadAllowed = true
                });
            }

            return assets;
        }

        private static IReadOnlyList<BookIdentifier> BuildIdentifiers(
            BookScraperSearchResult result)
        {
            var identifiers = new List<BookIdentifier>
            {
                new() { Scheme = "ANNA-ID", Value = result.Id }
            };
            if (IsMd5(result.Md5))
            {
                identifiers.Add(new BookIdentifier { Scheme = "MD5", Value = result.Md5 });
            }

            foreach (string isbn in (result.Isbns ?? Array.Empty<string>()).Select(NormalizeIsbn)
                         .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal))
                identifiers.Add(new BookIdentifier { Scheme = "ISBN-13", Value = isbn });

            if (!string.IsNullOrWhiteSpace(result.Format))
            {
                identifiers.Add(new BookIdentifier { Scheme = "FORMAT", Value = result.Format });
            }

            return identifiers;
        }

        private static BookFileFormat InferFormat(
            BookScraperResolveResult link,
            BookRecord book)
        {
            var evidence = book.Identifiers.Where(i => i.Scheme.Equals("FORMAT", StringComparison.OrdinalIgnoreCase))
                .Select(i => ParseFormat(i.Value)).Append(ParseFormat(link.Format))
                .Append(ParseFormat(System.IO.Path.GetExtension(new Uri(link.Url).AbsolutePath).TrimStart('.')))
                .Where(format => format != BookFileFormat.Unknown).Distinct().ToArray();
            return evidence.Length == 1 ? evidence[0] : BookFileFormat.Unknown;
        }

        private static BookFileFormat ParseFormat(string? value) => value?.Trim().ToLowerInvariant() switch
        { "epub" => BookFileFormat.Epub, "pdf" => BookFileFormat.Pdf, _ => BookFileFormat.Unknown };

        private static bool MatchesEdition(BookRecord book, BookScraperSearchResult candidate)
        {
            if (!IsResourceId(candidate.Id) || ParseFormat(candidate.Format) == BookFileFormat.Unknown ||
                (IsMd5(candidate.Id) && IsMd5(candidate.Md5) && !SameResourceId(candidate.Id, candidate.Md5)) ||
                NormalizeText(candidate.Title) != NormalizeText(string.IsNullOrWhiteSpace(book.Subtitle)
                    ? book.Title : $"{book.Title} {book.Subtitle}")) return false;
            var authors = book.Authors.Where(KnownAuthor).Select(NormalizeText).ToHashSet(StringComparer.Ordinal);
            var actualAuthors = (candidate.Authors ?? []).Where(KnownAuthor).Select(NormalizeText).ToHashSet(StringComparer.Ordinal);
            if (authors.Count > 0 && !authors.SetEquals(actualAuthors)) return false;
            var isbns = IsbnValues(book).Select(NormalizeIsbn).Where(value => value.Length > 0).ToArray();
            if (isbns.Length > 0 && !(candidate.Isbns ?? []).Select(NormalizeIsbn)
                    .Any(value => value.Length > 0 && isbns.Contains(value, StringComparer.Ordinal))) return false;
            // Without an ISBN, require a known author plus all requested edition metadata.
            if (isbns.Length == 0 && authors.Count == 0) return false;
            if (book.PublishedYear.HasValue && candidate.Year != book.PublishedYear) return false;
            if (!string.IsNullOrWhiteSpace(book.Language) &&
                NormalizeLanguage(book.Language) != NormalizeLanguage(candidate.Language)) return false;
            if (!string.IsNullOrWhiteSpace(book.Publisher) && !string.IsNullOrWhiteSpace(candidate.Publisher) &&
                NormalizeText(book.Publisher) != NormalizeText(candidate.Publisher)) return false;
            var formats = book.Identifiers.Where(i => i.Scheme.Equals("FORMAT", StringComparison.OrdinalIgnoreCase))
                .Select(i => ParseFormat(i.Value)).Where(value => value != BookFileFormat.Unknown).Distinct().ToArray();
            return formats.Length == 0 || (formats.Length == 1 && formats[0] == ParseFormat(candidate.Format));
        }

        private static IEnumerable<string> IsbnValues(BookRecord book) => book.Identifiers.Where(i =>
            i.Scheme.Equals("ISBN-13", StringComparison.OrdinalIgnoreCase) ||
            i.Scheme.Equals("ISBN-10", StringComparison.OrdinalIgnoreCase) ||
            i.Scheme.Equals("ISBN", StringComparison.OrdinalIgnoreCase)).Select(i => i.Value);

        internal static string NormalizeIsbn(string? value)
        {
            string isbn = (value ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
            if (isbn.Length == 10 && isbn.Take(9).All(char.IsAsciiDigit) &&
                (char.IsAsciiDigit(isbn[9]) || isbn[9] == 'X'))
            {
                int sum = isbn.Select((c, i) => (c == 'X' ? 10 : c - '0') * (10 - i)).Sum();
                if (sum % 11 != 0) return "";
                string prefix = "978" + isbn[..9];
                int check = (10 - prefix.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum() % 10) % 10;
                return prefix + check.ToString(CultureInfo.InvariantCulture);
            }
            return isbn.Length == 13 && isbn.All(char.IsAsciiDigit) && isbn[..3] is "978" or "979" &&
                isbn.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum() % 10 == 0 ? isbn : "";
        }

        private static bool KnownAuthor(string value) => !string.IsNullOrWhiteSpace(value) &&
            !NormalizeText(value).Equals("unknown author", StringComparison.Ordinal);
        private static string NormalizeText(string? value) => Regex.Replace(
            (value ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
        private static bool IsResourceId(string? value) => value != null &&
            Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,199}\z");
        private static bool IsMd5(string? value) => value != null && Regex.IsMatch(value, @"\A[a-fA-F0-9]{32}\z");
        private static bool SameResourceId(string left, string right) => string.Equals(left, right,
            IsMd5(left) && IsMd5(right) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static string NormalizeLanguage(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) return string.Empty;
            string l = lang.Trim().ToLowerInvariant();
            if (l == "english") return "en";
            if (l == "arabic") return "ar";
            if (l == "french") return "fr";
            if (Regex.IsMatch(l, @"\A[a-z]{2,3}(?:-[a-z0-9]+)*\z"))
                try { return CultureInfo.GetCultureInfo(l).TwoLetterISOLanguageName; }
                catch (CultureNotFoundException) { }
            return l;
        }
    }
}
