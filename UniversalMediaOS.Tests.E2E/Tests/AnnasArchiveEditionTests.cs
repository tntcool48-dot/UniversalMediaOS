using System.IO;
using Microsoft.Extensions.DependencyInjection;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.OtherMedia.Books;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class AnnasArchiveEditionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "umos-edition-" + Guid.NewGuid().ToString("N"));
    private readonly Engine _engine = new();
    private readonly AnnasArchiveBookProvider _provider;
    private static readonly BookRecord Requested = new()
    {
        Id = "catalog-edition", Source = BookCatalogSource.GoogleBooks, Title = "Matilda",
        Authors = ["Roald Dahl"], PublishedYear = 1988, Language = "en",
        Identifiers = [new() { Scheme = "ISBN-13", Value = "9780140328721" }]
    };
    private static BookScraperSearchResult Candidate(string id = "right") => new(
        id, "Matilda", ["Roald Dahl"], "epub", "1 MB", "English", 1988, "")
        { Isbns = ["0140328726"] };

    public AnnasArchiveEditionTests()
    {
        Directory.CreateDirectory(_root);
        _provider = new(_engine, new DomainHotSwapper(Path.Combine(_root, "config.json")));
        _engine.Links = [new("Mirror", "https://books.test/selected.epub")];
    }

    [Fact]
    public async Task CrossCatalogLookup_SkipsFirstWrongEdition_AndAcceptsEquivalentIsbn10()
    {
        _engine.Results = [Candidate("wrong") with { Year = 2007 }, Candidate()];
        BookAsset asset = Assert.Single(await _provider.FindAssetsAsync(Requested));
        Assert.Equal("right", Assert.Single(_engine.Resolved));
        Assert.Equal("9780140328721", Assert.Single(_engine.Queries));
        Assert.Equal(BookFileFormat.Epub, asset.Format);
        Assert.Equal(BookAccessKind.RightsUnverified, asset.Access);
        Assert.Equal("Rights unverified", asset.AccessLabel);
        Assert.DoesNotContain("public domain", asset.RightsStatement, StringComparison.OrdinalIgnoreCase);
        Assert.True(asset.IsDownloadAllowed);
    }

    [Theory]
    [InlineData("isbn")]
    [InlineData("no_isbn")]
    [InlineData("title")]
    [InlineData("author")]
    [InlineData("no_author")]
    [InlineData("year")]
    [InlineData("no_year")]
    [InlineData("language")]
    [InlineData("no_language")]
    [InlineData("format")]
    [InlineData("hash_conflict")]
    public async Task CrossCatalogLookup_RejectsConflictingOrMissingEditionEvidence(string fault)
    {
        var candidate = Candidate();
        candidate = fault switch
        {
            "isbn" => candidate with { Isbns = ["9780131103627"] },
            "no_isbn" => candidate with { Isbns = [] },
            "title" => candidate with { Title = "Matilda Study Guide" },
            "author" => candidate with { Authors = ["Another author"] },
            "no_author" => candidate with { Authors = ["Unknown Author"] },
            "year" => candidate with { Year = 2007 },
            "no_year" => candidate with { Year = null },
            "language" => candidate with { Language = "Arabic" },
            "no_language" => candidate with { Language = "" },
            "format" => candidate with { Format = "mobi" },
            _ => candidate with { Id = new string('1', 32), Md5 = new string('2', 32) }
        };
        _engine.Results = [candidate];
        Assert.Empty(await _provider.FindAssetsAsync(Requested));
        Assert.Empty(_engine.Resolved);
    }

    [Fact]
    public async Task CrossCatalogLookup_RejectsAmbiguity_InvalidIsbn_AndRequestedFormatConflict()
    {
        _engine.Results = [Candidate("one"), Candidate("two")];
        Assert.Empty(await _provider.FindAssetsAsync(Requested));
        _engine.Results = [Candidate()];
        Assert.Empty(await _provider.FindAssetsAsync(Requested with
            { Identifiers = [new() { Scheme = "ISBN-13", Value = "9780140328722" }] }));
        Assert.Empty(await _provider.FindAssetsAsync(Requested with
            { Identifiers = Requested.Identifiers.Concat([new BookIdentifier { Scheme = "FORMAT", Value = "pdf" }]).ToArray() }));
        Assert.Empty(_engine.Resolved);
    }

    [Fact]
    public async Task LookupWithoutIsbn_RequiresKnownAuthor_AndAllRequestedEditionMetadata()
    {
        _engine.Results = [Candidate() with { Isbns = [] }];
        Assert.Single(await _provider.FindAssetsAsync(Requested with { Identifiers = [] }));
        Assert.Empty(await _provider.FindAssetsAsync(Requested with { Identifiers = [], Authors = [] }));
        Assert.Empty(await _provider.FindAssetsAsync(Requested with { Identifiers = [], Subtitle = "A study guide" }));
        Assert.Equal("right", Assert.Single(_engine.Resolved));
    }

    [Fact]
    public async Task Details_SelectExactResourceBeyondFirstResult_AndPreserveOpaqueIdCase()
    {
        _engine.Results = [Candidate("wrong"), Candidate("ExactID")];
        Assert.Equal("ExactID", (await _provider.GetByIdAsync("ExactID"))?.Id);
        Assert.Null(await _provider.GetByIdAsync("exactid"));
        _engine.Results = [Candidate(new string('a', 32))];
        Assert.NotNull(await _provider.GetByIdAsync(new string('A', 32)));
        _engine.Results = [Candidate("wrong")];
        Assert.Null(await _provider.GetByIdAsync("missing"));
    }

    [Fact]
    public async Task DirectHash_DoesNotSearch_AndConflictingStoredIdsCannotResolve()
    {
        string hash = new('a', 32);
        BookRecord book = Requested with { Identifiers = [new() { Scheme = "MD5", Value = hash }] };
        BookAsset asset = Assert.Single(await _provider.FindAssetsAsync(book));
        Assert.Equal(hash, asset.ExpectedMd5);
        Assert.Empty(_engine.Queries);
        Assert.Empty(await _provider.FindAssetsAsync(book with
        {
            Identifiers = book.Identifiers.Concat([new BookIdentifier { Scheme = "ANNA-ID", Value = new string('b', 32) }]).ToArray()
        }));
        Assert.Equal(hash, Assert.Single(_engine.Resolved));
    }

    [Fact]
    public async Task InvalidExplicitIds_DoNotBecomeWeakerSearches_AndLegacyOpaqueAnnaIdStillWorks()
    {
        _engine.Results = [Candidate()];
        Assert.Empty(await _provider.FindAssetsAsync(Requested with
            { Identifiers = [new() { Scheme = "ANNA-ID", Value = "../other" }] }));
        Assert.Empty(await _provider.FindAssetsAsync(Requested with
            { Identifiers = [new() { Scheme = "MD5", Value = "not-a-hash" }] }));
        Assert.Empty(await _provider.FindAssetsAsync(Requested with
        {
            Source = BookCatalogSource.AnnasArchive, Id = new string('b', 32),
            Identifiers = [new() { Scheme = "MD5", Value = new string('a', 32) }]
        }));
        Assert.Empty(_engine.Queries);
        var legacy = Requested with
        {
            Source = BookCatalogSource.AnnasArchive, Id = "opaque",
            Identifiers = [new() { Scheme = "ANNA-ID", Value = "opaque" }, new() { Scheme = "MD5", Value = "opaque" },
                new() { Scheme = "FORMAT", Value = "epub" }]
        };
        Assert.Equal("", Assert.Single(await _provider.FindAssetsAsync(legacy)).ExpectedMd5);
        Assert.Equal("opaque", Assert.Single(_engine.Resolved));
    }

    [Fact]
    public async Task Format_UsesSelectedItemOrExplicitLinkEvidence_AndRejectsConflictsAndQueryHints()
    {
        _engine.Results = [Candidate()];
        _engine.Links = [new("PDF mirror", "https://books.test/download?format=pdf")];
        Assert.Equal(BookFileFormat.Epub, Assert.Single(await _provider.FindAssetsAsync(Requested)).Format);
        _engine.Links = [new("Mirror", "https://books.test/download") { Format = "pdf" }];
        Assert.Empty(await _provider.FindAssetsAsync(Requested));
        _engine.Links = [new("PDF", "https://books.test/download?format=pdf")];
        Assert.Empty(await _provider.FindAssetsAsync(new BookRecord
            { Id = "selected", Source = BookCatalogSource.AnnasArchive, Title = "Book about PDFs" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_IgnoredByEngine_CannotPublishOrStartLaterResolution(bool duringResolve)
    {
        using var cancel = new CancellationTokenSource();
        _engine.Results = [Candidate()];
        if (duringResolve) _engine.OnResolve = cancel.Cancel;
        else _engine.OnSearch = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provider.FindAssetsAsync(Requested, cancel.Token));
        Assert.Equal(duringResolve ? 1 : 0, _engine.Resolved.Count);
    }

    [Fact]
    public async Task Search_ExportsRealIsbnPublisherAndMd5Evidence()
    {
        _engine.Results = [Candidate("opaque") with { Md5 = "opaque", Publisher = "Publisher" }];
        BookRecord book = Assert.Single((await _provider.SearchAsync("book")).Items);
        Assert.Equal("Publisher", book.Publisher);
        Assert.Contains(book.Identifiers, i => i.Scheme == "ISBN-13" && i.Value == "9780140328721");
        Assert.DoesNotContain(book.Identifiers, i => i.Scheme == "MD5");
    }

    [Fact]
    public async Task RecentReading_ProjectsLegacyAnnaRightsWithoutChangingSavedData_AndRetainsHash()
    {
        string path = Path.Combine(_root, "reading.json");
        var store = new JsonReadingProgressStore(path);
        await store.SaveAsync(new BookReadingProgress
        {
            BookId = "book", BookTitle = "Matilda", AssetId = "annas:old:mirror", AssetFormat = BookFileFormat.Epub,
            AssetAccess = BookAccessKind.PublicDomain, AssetLocation = "https://books.test/file.epub",
            AssetRightsStatement = "Open access", AssetDownloadAllowed = true, AssetExpectedMd5 = new string('a', 32)
        });
        string before = await File.ReadAllTextAsync(path);
        using var vm = new BookBrowseViewModel(new BookCatalogService([_provider]), new LocalBookImportService(), store);
        await vm.ShowContinueReadingCommand.ExecuteAsync(null);
        BookAsset asset = Assert.Single(Assert.Single(vm.Books).Assets);
        Assert.Equal(BookAccessKind.RightsUnverified, asset.Access);
        Assert.Equal(new string('a', 32), asset.ExpectedMd5);
        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void ProductionServices_ResolveBookDetailsAndExactlyOneAnnaCatalogProvider()
    {
        string? previous = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _root);
        try
        {
            using var services = UniversalMediaOS.WPF.App.ConfigureServices(overrides =>
                overrides.AddSingleton<BookScraperEngine>(_engine));
            using var details = services.GetRequiredService<BookDetailsViewModel>();
            Assert.NotNull(services.GetRequiredService<AnnasArchiveBookProvider>());
            Assert.Single(services.GetServices<IBookCatalogProvider>(), p => p.Source == BookCatalogSource.AnnasArchive);
        }
        finally { Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previous); }
    }

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class Engine() : BookScraperEngine(new PythonBootstrapper())
    {
        public BookScraperSearchResult[] Results { get; set; } = [];
        public BookScraperResolveResult[] Links { get; set; } = [];
        public List<string> Queries { get; } = [];
        public List<string> Resolved { get; } = [];
        public Action? OnSearch { get; set; }
        public Action? OnResolve { get; set; }
        public override Task<BookScraperSearchResult[]> SearchAsync(string query, string mirrorUrl, CancellationToken token = default)
        { Queries.Add(query); OnSearch?.Invoke(); return Task.FromResult(Results); }
        public override Task<BookScraperResolveResult[]> ResolveAsync(string id, string mirrorUrl, CancellationToken token = default)
        { Resolved.Add(id); OnResolve?.Invoke(); return Task.FromResult(Links); }
    }
}
