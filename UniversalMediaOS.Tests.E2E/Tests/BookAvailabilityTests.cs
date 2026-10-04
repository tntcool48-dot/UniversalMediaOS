using System.IO;
using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.OtherMedia.Books;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class BookAvailabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "umos-book-availability-" + Guid.NewGuid().ToString("N"));
    private static readonly BookRecord Selected = new()
    {
        Id = new string('a', 32), Source = BookCatalogSource.AnnasArchive, Title = "Selected edition",
        Authors = ["Known Author"], Language = "en",
        Identifiers = [new() { Scheme = "MD5", Value = new string('a', 32) }, new() { Scheme = "FORMAT", Value = "epub" }]
    };
    private static readonly BookAsset Attached = new()
    {
        Id = "local:attached", BookId = Selected.Id, Format = BookFileFormat.Epub,
        Access = BookAccessKind.Local, Location = "attached.epub"
    };
    public BookAvailabilityTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false, false, "annas:matched")]
    [InlineData(true, false, "local:attached")]
    [InlineData(false, true, "archive:matched")]
    public async Task Details_PrefersSavedThenLocalThenAnnaEdition(bool local, bool saved, string expected)
    {
        using var client = Client((_, _) => Task.FromResult(Json("""{"response":{"docs":[]}}""")));
        var archive = Attached with { Id = "archive:matched", Access = BookAccessKind.OpenLicense,
            IsDownloadAllowed = true, SourceLabel = "Internet Archive" };
        var anna = archive with { Id = "annas:matched", Format = BookFileFormat.Pdf, SourceLabel = "Anna's Archive" };
        var store = new Progress { GetAll = _ => Task.FromResult<IReadOnlyList<BookReadingProgress>>(
            saved ? [new() { BookId = Selected.Id, AssetId = archive.Id }] : []) };
        using var vm = Details(client, new Engine(), store);
        await vm.InitializeAsync(Selected with { Assets = local ? [archive, anna, Attached] : [archive, anna] });
        var recipient = new object();
        NavigateToBookReaderMessage? received = null;
        WeakReferenceMessenger.Default.Register<NavigateToBookReaderMessage>(recipient, (_, message) => received = message);
        try
        {
            vm.ReadPreferredCommand.Execute(null);
            Assert.Equal(expected, received?.Asset.Id);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }

    [Fact]
    public async Task Details_PublishesAvailableEditionBeforeSlowProvider_AndRetainsItOnFailure()
    {
        var resolve = NewLinks();
        using var client = Client((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.Contains("metadata")
            ? """{"metadata":{"rights":"Public domain"},"files":[{"name":"selected.epub","format":"EPUB"}]}"""
            : """{"response":{"docs":[{"identifier":"selected"}]}}""")));
        using var vm = Details(client, new Engine { Resolve = _ => resolve.Task });
        Task loading = vm.InitializeAsync(Selected);
        Assert.True(vm.IsLoading);
        Assert.Single(vm.Assets);
        Assert.True(vm.ReadPreferredCommand.CanExecute(null));
        Assert.Contains("checking another catalog", vm.AvailabilityMessage);
        resolve.SetException(new HttpRequestException("provider unavailable"));
        await loading.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(vm.Assets);
        Assert.False(vm.IsLoading);
        Assert.Contains("Anna's Archive unavailable", vm.ErrorMessage);
        Assert.Contains("Lookup incomplete", vm.AvailabilityMessage);
        Assert.DoesNotContain("readable", vm.AvailabilityMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Details_ProviderFailureOrTimeout_IsNotNoEdition(bool timeout)
    {
        using var client = Client((_, _) => Task.FromResult(Json("""{"response":{"docs":[]}}""")));
        using var vm = Details(client, new Engine { Resolve = _ => Task.FromException<BookScraperResolveResult[]>(
            timeout ? new TimeoutException() : new HttpRequestException()) });
        await vm.InitializeAsync(Selected);
        Assert.Empty(vm.Assets);
        Assert.False(vm.IsLoading);
        Assert.Contains("lookup incomplete", vm.AvailabilityMessage);
        Assert.Contains(timeout ? "timed out" : "unavailable", vm.ErrorMessage);
        Assert.DoesNotContain("canceled", vm.AvailabilityMessage);
        Assert.False(vm.ReadPreferredCommand.CanExecute(null));
    }

    [Fact]
    public async Task Details_CancelRetainsAttachedAssets_AndLateFailureCannotReplaceNewSelection()
    {
        var resolve = NewLinks();
        using var client = Client((_, _) => Task.FromResult(Json("""{"response":{"docs":[]}}""")));
        using var vm = Details(client, new Engine { Resolve = _ => resolve.Task });
        Task old = vm.InitializeAsync(Selected with { Assets = [Attached] });
        vm.CancelLookupCommand.Execute(null);
        await old.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.Single(vm.Assets);
        Assert.Contains("canceled", vm.AvailabilityMessage);
        BookRecord next = Selected with { Id = "next", Source = BookCatalogSource.Local, Assets = [Attached with { Id = "local:next" }] };
        await vm.InitializeAsync(next);
        resolve.SetException(new HttpRequestException("old provider failed late"));
        Assert.Equal("next", vm.Book!.Id);
        Assert.Equal("local:next", Assert.Single(vm.Assets).Id);
        Assert.Empty(vm.ErrorMessage);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Details_LateHistoryFromOldSelectionCannotReplaceCurrentProgress()
    {
        var history = new TaskCompletionSource<IReadOnlyList<BookReadingProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Progress { GetAll = _ => history.Task };
        using var client = Client((_, _) => Task.FromResult(Json("""{"response":{"docs":[]}}""")));
        using var vm = Details(client, new Engine(), store);
        Task old = vm.InitializeAsync(Selected);
        store.GetAll = _ => Task.FromResult<IReadOnlyList<BookReadingProgress>>([]);
        await vm.InitializeAsync(Selected with { Id = "new", Source = BookCatalogSource.Local, Assets = [Attached] });
        await old.WaitAsync(TimeSpan.FromMilliseconds(500));
        history.SetResult([new() { BookId = Selected.Id, Fraction = .8 }]);
        Assert.Equal("new", vm.Book!.Id);
        Assert.Null(vm.ReadingProgress);
        Assert.False(vm.IsLoading);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archive_PartialMetadataFailure_RetainsGoodFileAndReportsIncomplete(bool readFailure)
    {
        using var client = Client((request, _) => request.RequestUri!.AbsolutePath.EndsWith("failed") && readFailure
            ? Task.FromException<HttpResponseMessage>(new IOException("provider body failed"))
            : Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("failed")
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json(request.RequestUri.AbsolutePath.Contains("metadata")
                ? """{"metadata":{"rights":"Public domain"},"files":[{"name":"good.pdf","format":"PDF"}]}"""
                : """{"response":{"docs":[{"identifier":"good"},{"identifier":"failed"}]}}""")));
        var archive = Archive(client);
        BookAssetSearchResult result = await archive.FindAssetsWithOutcomeAsync(Selected);
        Assert.Single(result.Assets);
        Assert.Equal(BookSearchOutcome.Unavailable, result.Outcome);
        Assert.Single(await archive.FindAssetsAsync(Selected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archive_BodyDeadlineIncludesSearchAndMetadata(bool metadata)
    {
        using var client = Client((request, token) => Task.FromResult(metadata && !request.RequestUri!.AbsolutePath.Contains("metadata")
            ? Json("""{"response":{"docs":[{"identifier":"selected"}]}}""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody()) }));
        client.Timeout = TimeSpan.FromMilliseconds(100);
        BookAssetSearchResult result = await Archive(client).FindAssetsWithOutcomeAsync(Selected).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(BookSearchOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task Archive_CallerCancellation_RemainsCancellation()
    {
        using var client = Client((_, token) => Task.FromCanceled<HttpResponseMessage>(token));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Archive(client).FindAssetsWithOutcomeAsync(Selected, cancellation.Token));
    }

    private BookDetailsViewModel Details(HttpClient client, Engine engine, Progress? progress = null) => new(
        new BookCatalogService([new Catalog()]), Archive(client),
        new AnnasArchiveBookProvider(engine, new DomainHotSwapper(Path.Combine(_root, "config.json"))), progress ?? new());
    private static InternetArchiveBookService Archive(HttpClient client) => new(client, new Uri("https://archive.test/advancedsearch.php"), new Uri("https://archive.test/metadata/"));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => new(new Handler(response));
    private static TaskCompletionSource<BookScraperResolveResult[]> NewLinks() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Dispose() => Directory.Delete(_root, true);
    private sealed class Catalog : IBookCatalogProvider
    {
        public BookCatalogSource Source => BookCatalogSource.AnnasArchive;
        public Task<BookSearchPage> SearchAsync(string query, int offset = 0, int limit = 24, CancellationToken cancellationToken = default) => Task.FromResult(new BookSearchPage());
        public Task<BookRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<BookRecord?>(null);
    }
    private sealed class Engine() : BookScraperEngine(new PythonBootstrapper())
    {
        public Func<CancellationToken, Task<BookScraperResolveResult[]>> Resolve { get; set; } = _ => Task.FromResult<BookScraperResolveResult[]>([]);
        public override Task<BookScraperResolveResult[]> ResolveAsync(string md5, string mirrorUrl, CancellationToken token = default) => Resolve(token);
    }
    private sealed class Progress : IReadingProgressStore
    {
        public Func<CancellationToken, Task<IReadOnlyList<BookReadingProgress>>> GetAll { get; set; } = _ => Task.FromResult<IReadOnlyList<BookReadingProgress>>([]);
        public Task<IReadOnlyList<BookReadingProgress>> GetAllAsync(CancellationToken cancellationToken = default) => GetAll(cancellationToken);
        public Task<BookReadingProgress?> GetAsync(string bookId, string assetId, CancellationToken cancellationToken = default) => Task.FromResult<BookReadingProgress?>(null);
        public Task SaveAsync(BookReadingProgress progress, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(string bookId, string assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }
    private sealed class StalledBody : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
