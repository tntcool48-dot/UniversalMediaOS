using System;
using System.Collections.Generic;
using System.Linq;
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
                    Publisher = "Anna's Archive",
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
            
            // For Anna's Archive, the ID is typically the MD5 hash
            BookSearchPage page = await SearchAsync(id, limit: 1, cancellationToken: cancellationToken);
            return page.Items.FirstOrDefault();
        }

        public async Task<IReadOnlyList<BookAsset>> FindAssetsAsync(
            BookRecord book,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(book);

            string resourceId = string.Empty;
            
            // 1. Try to find MD5 directly from book identifiers
            BookIdentifier? scraperId = book.Identifiers.FirstOrDefault(identifier =>
                identifier.Scheme.Equals("ANNA-ID", StringComparison.OrdinalIgnoreCase));
            BookIdentifier? md5Id = book.Identifiers.FirstOrDefault(identifier =>
                identifier.Scheme.Equals("MD5", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(scraperId?.Value))
            {
                resourceId = scraperId.Value;
            }
            else if (!string.IsNullOrWhiteSpace(md5Id?.Value))
            {
                resourceId = md5Id.Value;
            }
            else if (book.Source == BookCatalogSource.AnnasArchive)
            {
                resourceId = book.Id;
            }
            else
            {
                // 2. If it's a book from Google Books / Open Library, search on Anna's Archive by ISBN or Title+Author
                string searchQuery = string.Empty;
                var isbn = book.Identifiers.FirstOrDefault(i => 
                    i.Scheme.Equals("ISBN-13", StringComparison.OrdinalIgnoreCase) || 
                    i.Scheme.Equals("ISBN-10", StringComparison.OrdinalIgnoreCase));
                
                if (isbn != null && !string.IsNullOrWhiteSpace(isbn.Value))
                {
                    searchQuery = isbn.Value;
                }
                else
                {
                    string author = book.Authors.FirstOrDefault() ?? string.Empty;
                    searchQuery = string.IsNullOrWhiteSpace(author) ? book.Title : $"{book.Title} {author}";
                }

                if (!string.IsNullOrWhiteSpace(searchQuery))
                {
                    AppLogger.Log($"[AnnasArchiveBookProvider] Matching search for assets: {searchQuery}");
                    var searchResults = await _engine.SearchAsync(searchQuery, GetMirrorUrl(), cancellationToken);
                    var matched = searchResults.FirstOrDefault();
                    if (matched != null)
                    {
                        resourceId = !string.IsNullOrWhiteSpace(matched.Id)
                            ? matched.Id
                            : matched.Md5;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(resourceId))
            {
                AppLogger.Log($"[AnnasArchiveBookProvider] Could not find a scraper resource id for book: {book.Title}", "INFO");
                return Array.Empty<BookAsset>();
            }

            string mirror = GetMirrorUrl();
            var links = await _engine.ResolveAsync(resourceId, mirror, cancellationToken);

            var assets = new List<BookAsset>();
            foreach (var link in links)
            {
                if (!Uri.TryCreate(link.Url, UriKind.Absolute, out Uri? location) ||
                    location.Scheme is not ("http" or "https"))
                {
                    continue;
                }

                BookFileFormat format = InferFormat(link, book);
                assets.Add(new BookAsset
                {
                    Id = $"annas:{resourceId}:{link.Name}",
                    BookId = book.Id,
                    DisplayName = $"{book.Title} ({link.Name})",
                    Format = format,
                    Access = BookAccessKind.PublicDomain,
                    Location = location.AbsoluteUri,
                    SourceLabel = "Anna's Archive",
                    RightsStatement = "Open access via Anna's Archive shadow library indexes.",
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
            if (!string.IsNullOrWhiteSpace(result.Md5))
            {
                identifiers.Add(new BookIdentifier { Scheme = "MD5", Value = result.Md5 });
            }

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
            string evidence = string.Join(
                ' ',
                link.Name,
                link.Url,
                book.Identifiers.FirstOrDefault(identifier =>
                    identifier.Scheme.Equals("FORMAT", StringComparison.OrdinalIgnoreCase))?.Value,
                book.Description);
            return evidence.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                ? BookFileFormat.Pdf
                : evidence.Contains("epub", StringComparison.OrdinalIgnoreCase)
                    ? BookFileFormat.Epub
                    : BookFileFormat.Unknown;
        }

        private static string NormalizeLanguage(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) return string.Empty;
            string l = lang.Trim().ToLowerInvariant();
            if (l.StartsWith("en")) return "en";
            if (l.StartsWith("ar")) return "ar";
            if (l.StartsWith("fr")) return "fr";
            return l;
        }
    }
}
