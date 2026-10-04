using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using UniversalMediaOS.Core.OtherMedia.Books;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class BookSubsystemTests
{
    [Fact]
    public async Task OpenLibraryProvider_ParsesSearchMetadata()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            Assert.Contains("/search.json", request.RequestUri?.AbsolutePath);
            return JsonResponse(
                """
                {
                  "numFound": 12,
                  "docs": [{
                    "key": "/works/OL123W",
                    "title": "The Example Book",
                    "subtitle": "A Test",
                    "author_name": ["Ada Author"],
                    "first_publish_year": 1984,
                    "isbn": ["9781234567890"],
                    "cover_i": 42,
                    "language": ["eng"],
                    "subject": ["Testing", "Software"],
                    "publisher": ["Example Press"],
                    "number_of_pages_median": 320
                  }]
                }
                """);
        }));
        var provider = new OpenLibraryBookProvider(
            client,
            new Uri("https://openlibrary.test/"));

        BookSearchPage page = await provider.SearchAsync("example");

        BookRecord book = Assert.Single(page.Items);
        Assert.Equal(12, page.Total);
        Assert.Equal("/works/OL123W", book.Id);
        Assert.Equal("The Example Book", book.Title);
        Assert.Equal("Ada Author", Assert.Single(book.Authors));
        Assert.Equal(1984, book.PublishedYear);
        Assert.Equal(320, book.PageCount);
        Assert.Equal("en", book.Language);
        Assert.Contains(
            book.Identifiers,
            identifier => identifier.Scheme == "ISBN-13" &&
                          identifier.Value == "9781234567890");
    }

    [Fact]
    public async Task GoogleBooksProvider_OnlyCreatesDownloadAssetForPublicDomainVolume()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "totalItems": 1,
              "items": [{
                "id": "google-1",
                "volumeInfo": {
                  "title": "Open Volume",
                  "authors": ["Grace Writer"],
                  "publishedDate": "1922-05-01",
                  "description": "Public-domain test volume.",
                  "pageCount": 210,
                  "language": "en",
                  "industryIdentifiers": [{
                    "type": "ISBN_13",
                    "identifier": "9780000000002"
                  }],
                  "imageLinks": {
                    "thumbnail": "http://images.test/cover.jpg"
                  }
                },
                "accessInfo": {
                  "publicDomain": true,
                  "accessViewStatus": "FULL_PUBLIC_DOMAIN",
                  "epub": {
                    "isAvailable": true,
                    "downloadLink": "https://books.test/open.epub"
                  },
                  "pdf": {
                    "isAvailable": false
                  }
                }
              }]
            }
            """)));
        var provider = new GoogleBooksBookProvider(
            client,
            baseUri: new Uri("https://google.test/books/v1/"));

        BookRecord book = Assert.Single((await provider.SearchAsync("open")).Items);

        BookAsset asset = Assert.Single(book.Assets);
        Assert.Equal(BookFileFormat.Epub, asset.Format);
        Assert.Equal(BookAccessKind.PublicDomain, asset.Access);
        Assert.True(asset.IsDownloadAllowed);
        Assert.StartsWith("https://", book.CoverUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CatalogService_DeduplicatesByIsbnButKeepsDifferentPublicationYears()
    {
        var first = new BookRecord
        {
            Id = "one",
            Source = BookCatalogSource.OpenLibrary,
            Title = "Shared",
            Authors = ["One Author"],
            PublishedYear = 2000,
            Identifiers =
            [
                new BookIdentifier { Scheme = "ISBN-13", Value = "9781111111111" }
            ]
        };
        var duplicate = new BookRecord
        {
            Id = "two",
            Source = BookCatalogSource.GoogleBooks,
            Title = "Shared",
            Authors = ["One Author"],
            PublishedYear = 2000,
            Description = "Richer metadata",
            Identifiers =
            [
                new BookIdentifier { Scheme = "ISBN-13", Value = "9781111111111" }
            ]
        };
        var laterWork = new BookRecord
        {
            Id = "three",
            Source = BookCatalogSource.OpenLibrary,
            Title = "Same Name",
            Authors = ["Same Author"],
            PublishedYear = 2010
        };
        var earlierWork = laterWork with
        {
            Id = "four",
            Source = BookCatalogSource.GoogleBooks,
            PublishedYear = 1990
        };
        var service = new BookCatalogService(
        [
            new FakeProvider(BookCatalogSource.OpenLibrary, [first, laterWork]),
            new FakeProvider(BookCatalogSource.GoogleBooks, [duplicate, earlierWork])
        ]);

        BookSearchPage result = await service.SearchAsync("shared", limit: 10);

        Assert.Equal(3, result.Items.Count);
        BookRecord merged = Assert.Single(
            result.Items,
            book => book.Identifiers.Any(identifier =>
                identifier.Value == "9781111111111"));
        Assert.Equal("Richer metadata", merged.Description);
        Assert.Contains(result.Items, book => book.PublishedYear == 2010);
        Assert.Contains(result.Items, book => book.PublishedYear == 1990);
    }

    [Fact]
    public async Task InternetArchive_ReturnsOnlyExplicitlyOpenEpubAndPdfFiles()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/advancedsearch.php", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {"response":{"docs":[{"identifier":"open_book"}]}}
                    """);
            }

            Assert.Equal("/metadata/open_book", path);
            return JsonResponse(
                """
                {
                  "metadata": {
                    "identifier": "open_book",
                    "access-restricted-item": "false",
                    "licenseurl": "https://creativecommons.org/publicdomain/mark/1.0/",
                    "rights": "Public Domain"
                  },
                  "files": [
                    {"name":"open_book.epub","format":"EPUB","size":"1024"},
                    {"name":"open_book.pdf","format":"Text PDF","size":"2048"},
                    {"name":"private.pdf","format":"PDF","private":"true"},
                    {"name":"metadata.xml","format":"Metadata"}
                  ]
                }
                """);
        }));
        var service = new InternetArchiveBookService(
            client,
            new Uri("https://archive.test/advancedsearch.php"),
            new Uri("https://archive.test/metadata/"));
        var book = new BookRecord
        {
            Id = "catalog-book",
            Title = "Open Book",
            Authors = ["Public Author"],
            Identifiers =
            [
                new BookIdentifier { Scheme = "ISBN-13", Value = "9781234567890" }
            ]
        };

        IReadOnlyList<BookAsset> assets = await service.FindAssetsAsync(book);

        Assert.Equal(2, assets.Count);
        Assert.All(assets, asset =>
        {
            Assert.Equal(BookAccessKind.PublicDomain, asset.Access);
            Assert.True(asset.IsDownloadAllowed);
            Assert.StartsWith(
                "https://archive.test/download/open_book/",
                asset.Location,
                StringComparison.Ordinal);
        });
        Assert.Contains(assets, asset => asset.Format == BookFileFormat.Epub);
        Assert.Contains(assets, asset => asset.Format == BookFileFormat.Pdf);
    }

    [Fact]
    public async Task InternetArchive_RejectsRecordWithoutExplicitOpenRights()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith(
                    "/advancedsearch.php",
                    StringComparison.Ordinal) == true)
            {
                return JsonResponse(
                    """{"response":{"docs":[{"identifier":"unclear_book"}]}}""");
            }

            return JsonResponse(
                """
                {
                  "metadata": {
                    "access-restricted-item": "false",
                    "rights": "Copyright holder not identified"
                  },
                  "files": [
                    {"name":"unclear_book.pdf","format":"PDF"}
                  ]
                }
                """);
        }));
        var service = new InternetArchiveBookService(
            client,
            new Uri("https://archive.test/advancedsearch.php"),
            new Uri("https://archive.test/metadata/"));

        IReadOnlyList<BookAsset> assets = await service.FindAssetsAsync(new BookRecord
        {
            Id = "book",
            Title = "Unclear Book"
        });

        Assert.Empty(assets);
    }

    [Fact]
    public async Task LocalEpubImport_ParsesMetadataAndReaderPreservesSpineOrder()
    {
        string root = CreateTempDirectory();
        string epub = Path.Combine(root, "ordered.epub");
        string cache = Path.Combine(root, "cache");
        CreateEpub(
            epub,
            title: "Imported Regression Book",
            authors: ["First Author", "Second Author"],
            manifest:
            [
                ("one", "Text/chapter-1.xhtml"),
                ("two", "Text/chapter-2.xhtml#middle")
            ],
            spine: ["two", "one"]);

        try
        {
            LocalBookImportResult imported =
                await new LocalBookImportService().ImportAsync(epub);
            using var client = new HttpClient(new StubHttpMessageHandler(_ =>
                throw new Xunit.Sdk.XunitException("Local EPUB preparation made a network request.")));
            var reader = new BookReaderService(client, cache);

            BookReaderDocument document = await reader.PrepareAsync(imported.Asset);

            Assert.Equal("Imported Regression Book", imported.Book.Title);
            Assert.Equal(["First Author", "Second Author"], imported.Book.Authors);
            Assert.Equal(BookAccessKind.Local, imported.Asset.Access);
            Assert.Collection(
                document.Chapters,
                chapter => Assert.EndsWith(
                    "chapter-2.xhtml",
                    new Uri(chapter.Location).LocalPath,
                    StringComparison.OrdinalIgnoreCase),
                chapter => Assert.EndsWith(
                    "chapter-1.xhtml",
                    new Uri(chapter.Location).LocalPath,
                    StringComparison.OrdinalIgnoreCase));
            Assert.All(document.Chapters, chapter =>
                Assert.StartsWith(
                    Path.GetFullPath(cache),
                    Path.GetFullPath(new Uri(chapter.Location).LocalPath),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplacedLocalEpub_InvalidatesIdentityAndExtractionCache()
    {
        string root = CreateTempDirectory();
        string epub = Path.Combine(root, "replaceable.epub");
        string cache = Path.Combine(root, "cache");
        try
        {
            CreateEpub(
                epub,
                title: "First Edition",
                authors: ["Author"],
                manifest: [("old", "Text/old.xhtml")],
                spine: ["old"]);
            var importer = new LocalBookImportService();
            LocalBookImportResult first = await importer.ImportAsync(epub);
            using var client = new HttpClient(new StubHttpMessageHandler(_ =>
                throw new Xunit.Sdk.XunitException("Local EPUB preparation made a network request.")));
            var reader = new BookReaderService(client, cache);
            BookReaderDocument firstDocument = await reader.PrepareAsync(first.Asset);

            File.Delete(epub);
            CreateEpub(
                epub,
                title: "Second Edition",
                authors: ["Author"],
                manifest: [("new", "Text/new.xhtml")],
                spine: ["new"]);
            File.SetLastWriteTimeUtc(epub, DateTime.UtcNow.AddSeconds(2));
            LocalBookImportResult second = await importer.ImportAsync(epub);
            BookReaderDocument secondDocument = await reader.PrepareAsync(second.Asset);

            Assert.NotEqual(first.Book.Id, second.Book.Id);
            Assert.EndsWith(
                "old.xhtml",
                new Uri(Assert.Single(firstDocument.Chapters).Location).LocalPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(
                "new.xhtml",
                new Uri(Assert.Single(secondDocument.Chapters).Location).LocalPath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BookReader_RejectsEpubArchivePathTraversal()
    {
        string root = CreateTempDirectory();
        string epub = Path.Combine(root, "unsafe.epub");
        string cache = Path.Combine(root, "cache");
        string escaped = Path.Combine(root, "escaped.txt");
        CreateEpub(
            epub,
            title: "Unsafe",
            authors: [],
            manifest: [("one", "chapter.xhtml")],
            spine: ["one"],
            extraEntry: ("../../../escaped.txt", "must not escape"));
        var asset = new BookAsset
        {
            Id = "local:unsafe:epub",
            BookId = "local:unsafe",
            Format = BookFileFormat.Epub,
            Access = BookAccessKind.Local,
            Location = epub
        };

        try
        {
            using var client = new HttpClient(new StubHttpMessageHandler(_ =>
                throw new Xunit.Sdk.XunitException("Local EPUB preparation made a network request.")));
            var reader = new BookReaderService(client, cache);

            await Assert.ThrowsAsync<InvalidDataException>(() => reader.PrepareAsync(asset));
            Assert.False(File.Exists(escaped));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadingProgressStore_PersistsAndClampsProgress()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "progress.json");
        try
        {
            var first = new JsonReadingProgressStore(path);
            await first.SaveAsync(new BookReadingProgress
            {
                BookId = "book-1",
                AssetId = "asset-1",
                BookTitle = "Saved Book",
                BookAuthors = ["Saved Author"],
                AssetLocation = "C:\\Books\\saved.epub",
                AssetFormat = BookFileFormat.Epub,
                AssetAccess = BookAccessKind.Local,
                ChapterIndex = 4,
                PageNumber = 55,
                Fraction = 1.4
            });
            var second = new JsonReadingProgressStore(path);

            BookReadingProgress? restored =
                await second.GetAsync("book-1", "asset-1");

            Assert.NotNull(restored);
            Assert.Equal(1, restored.Fraction);
            Assert.Equal(100, restored.Percent);
            Assert.Equal(4, restored.ChapterIndex);
            Assert.Equal(55, restored.PageNumber);
            Assert.Equal("Saved Book", restored.BookTitle);
            Assert.Equal(["Saved Author"], restored.BookAuthors);
            Assert.Equal(BookFileFormat.Epub, restored.AssetFormat);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reader_RejectsRemoteAssetWithoutOpenLicense()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            throw new Xunit.Sdk.XunitException("Unauthorized asset should not be requested.")));
        var reader = new BookReaderService(
            client,
            Path.Combine(CreateTempDirectory(), "cache"));
        var asset = new BookAsset
        {
            Id = "remote:metadata-only",
            BookId = "book",
            Format = BookFileFormat.Pdf,
            Access = BookAccessKind.MetadataOnly,
            Location = "https://example.test/book.pdf",
            IsDownloadAllowed = false
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.PrepareAsync(asset));
    }

    [Theory]
    [InlineData("html")]
    [InlineData("truncated")]
    [InlineData("hash")]
    [InlineData("zip")]
    [InlineData("xml")]
    public async Task Reader_RejectsInvalidRemoteBookBeforePublishingCache(string fault)
    {
        string root = CreateTempDirectory();
        try
        {
            byte[] bytes = Encoding.ASCII.GetBytes(fault == "html"
                ? "<html>download failed, expected %PDF-1.7</html>" : "%PDF-1.7\nfixture\n%%EOF");
            if (fault is "zip" or "xml")
            {
                string zip = Path.Combine(root, "invalid.zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    WriteEntry(archive, "content.opf", "<package/>");
                    if (fault == "xml") WriteEntry(archive, "META-INF/container.xml", "<broken");
                }
                bytes = await File.ReadAllBytesAsync(zip);
            }
            using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            {
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new("application/pdf");
                if (fault == "truncated") content.Headers.ContentLength = bytes.Length + 9;
                return new(HttpStatusCode.OK) { Content = content };
            }));
            string cache = Path.Combine(root, "cache");
            await Assert.ThrowsAsync<InvalidDataException>(() => new BookReaderService(client, cache).PrepareAsync(new BookAsset
            {
                Id = "annas:fixture", BookId = "book", Format = fault is "zip" or "xml" ? BookFileFormat.Epub : BookFileFormat.Pdf,
                Access = BookAccessKind.RightsUnverified, Location = "https://books.test/download", IsDownloadAllowed = true,
                ExpectedMd5 = fault == "hash" ? new string('a', 32) : ""
            }));
            Assert.Empty(Directory.GetFiles(cache, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(BookFileFormat.Pdf)]
    [InlineData(BookFileFormat.Epub)]
    public async Task Reader_AvailableRightsUnverifiedFile_VerifiesHashAndReusesOnlyValidCache(BookFileFormat format)
    {
        string root = CreateTempDirectory();
        try
        {
            byte[] bytes;
            if (format == BookFileFormat.Epub)
            {
                string source = Path.Combine(root, "source.epub");
                CreateEpub(source, "Hash fixture", ["Author"], [("one", "chapter.xhtml")], ["one"]);
                bytes = await File.ReadAllBytesAsync(source);
            }
            else bytes = Encoding.ASCII.GetBytes("%PDF-1.7\nfixture\n%%EOF");
            int requests = 0;
            using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            { requests++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; }));
            string cache = Path.Combine(root, "cache");
            var reader = new BookReaderService(client, cache);
            var asset = new BookAsset
            {
                Id = "annas:fixture", BookId = "book", Format = format, Access = BookAccessKind.RightsUnverified,
                Location = "https://books.test/download", IsDownloadAllowed = true,
                ExpectedMd5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes))
            };
            BookReaderDocument first = await reader.PrepareAsync(asset);
            Assert.Equal(first.StartLocation, (await reader.PrepareAsync(asset)).StartLocation);
            Assert.Equal(1, requests);
            string downloaded = Assert.Single(Directory.GetFiles(Path.Combine(cache, "downloads")));
            await File.WriteAllTextAsync(downloaded, "<html>old poisoned cache</html>");
            await reader.PrepareAsync(asset);
            Assert.Equal(2, requests);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(downloaded));
        }
        finally { Directory.Delete(root, true); }
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "umos-books-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateEpub(
        string path,
        string title,
        IReadOnlyList<string> authors,
        IReadOnlyList<(string Id, string Href)> manifest,
        IReadOnlyList<string> spine,
        (string Path, string Content)? extraEntry = null)
    {
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(
            archive,
            "META-INF/container.xml",
            """
            <?xml version="1.0"?>
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """);
        string authorXml = string.Join(
            Environment.NewLine,
            authors.Select(author => $"<dc:creator>{author}</dc:creator>"));
        string manifestXml = string.Join(
            Environment.NewLine,
            manifest.Select(item =>
                $"<item id=\"{item.Id}\" href=\"{item.Href}\" media-type=\"application/xhtml+xml\"/>"));
        string spineXml = string.Join(
            Environment.NewLine,
            spine.Select(id => $"<itemref idref=\"{id}\"/>"));
        WriteEntry(
            archive,
            "OEBPS/content.opf",
            $$"""
            <?xml version="1.0"?>
            <package xmlns="http://www.idpf.org/2007/opf">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>{{title}}</dc:title>
                {{authorXml}}
                <dc:language>en</dc:language>
                <dc:publisher>Regression Press</dc:publisher>
                <dc:date>2024</dc:date>
              </metadata>
              <manifest>{{manifestXml}}</manifest>
              <spine>{{spineXml}}</spine>
            </package>
            """);
        foreach ((string _, string href) in manifest)
        {
            string contentPath = href.Split('#', '?')[0];
            WriteEntry(
                archive,
                "OEBPS/" + contentPath,
                "<html><body><p>chapter</p></body></html>");
        }

        if (extraEntry is { } extra)
        {
            WriteEntry(archive, extra.Path, extra.Content);
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class FakeProvider : IBookCatalogProvider
    {
        private readonly IReadOnlyList<BookRecord> _items;

        public FakeProvider(
            BookCatalogSource source,
            IReadOnlyList<BookRecord> items)
        {
            Source = source;
            _items = items;
        }

        public BookCatalogSource Source { get; }

        public Task<BookSearchPage> SearchAsync(
            string query,
            int offset = 0,
            int limit = 24,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new BookSearchPage
            {
                Items = _items,
                Total = _items.Count
            });
        }

        public Task<BookRecord?> GetByIdAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_items.FirstOrDefault(item => item.Id == id));
        }
    }

    private class FakeBookScraperEngine : BookScraperEngine
    {
        public List<BookScraperSearchResult> SearchResults { get; } = new();
        public List<BookScraperResolveResult> ResolveResults { get; } = new();

        public FakeBookScraperEngine() : base(new PythonBootstrapper())
        {
        }

        public override Task<BookScraperSearchResult[]> SearchAsync(
            string query,
            string mirrorUrl,
            CancellationToken token = default)
        {
            return Task.FromResult(SearchResults.ToArray());
        }

        public override Task<BookScraperResolveResult[]> ResolveAsync(
            string md5,
            string mirrorUrl,
            CancellationToken token = default)
        {
            return Task.FromResult(ResolveResults.ToArray());
        }
    }

    [Fact]
    public async Task AnnasArchiveBookProvider_SearchesAndFindsAssets()
    {
        var fakeEngine = new FakeBookScraperEngine();
        fakeEngine.SearchResults.Add(new BookScraperSearchResult(
            Id: "hash123",
            Title: "Scraped Book",
            Authors: new List<string> { "Scraped Author" },
            Format: "epub",
            Size: "1.2 MB",
            Language: "English",
            Year: 2021,
            Md5: "hash123"
        ));
        fakeEngine.ResolveResults.Add(new BookScraperResolveResult(
            Name: "Library Genesis",
            Url: "https://libgen.test/hash123"
        ));

        // Use a mock temp config
        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var config = new DomainHotSwapper(tempFile);
        try
        {
            var provider = new AnnasArchiveBookProvider(fakeEngine, config);

            BookSearchPage page = await provider.SearchAsync("scraped");
            BookRecord book = Assert.Single(page.Items);
            Assert.Equal("hash123", book.Id);
            Assert.Equal("Scraped Book", book.Title);
            Assert.Equal("Scraped Author", Assert.Single(book.Authors));
            Assert.Equal(2021, book.PublishedYear);

            var assets = await provider.FindAssetsAsync(book);
            BookAsset asset = Assert.Single(assets);
            Assert.Equal("https://libgen.test/hash123", asset.Location);
            Assert.Equal("Anna's Archive", asset.SourceLabel);
        }
        finally
        {
            try { File.Delete(tempFile); } catch {}
        }
    }
}
