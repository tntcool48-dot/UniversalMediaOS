using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using UniversalMediaOS.Core.OtherMedia.Books;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class BookProgressiveSearchTests
{
    [Fact]
    public async Task FastPage_IsVisibleBeforeSlowProvider_AndRetainedAfterTimeout()
    {
        var slow = NewCompletion();
        var service = new BookCatalogService([
            Provider(BookCatalogSource.OpenLibrary, (_, _) => Task.FromResult(Page("fast"))),
            Provider(BookCatalogSource.AnnasArchive, (_, _) => slow.Task)]);
        using var vm = ViewModel(service);
        vm.SearchQuery = "Book";
        var clock = Stopwatch.StartNew();
        Task search = vm.SearchBooksCommand.ExecuteAsync(null);
        Assert.Equal("fast", Assert.Single(vm.Books).Id);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.True(vm.IsBusy);
        Assert.Contains("Checking 1 more catalog", vm.StatusMessage);
        slow.SetException(new OperationCanceledException("provider-owned timeout"));
        await search.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(vm.IsBusy);
        Assert.Equal("fast", Assert.Single(vm.Books).Id);
        Assert.Contains("Anna's Archive timed out", vm.ErrorMessage);
        Assert.Contains("Partial results retained", vm.StatusMessage);
    }

    [Fact]
    public async Task DuplicateEnrichment_PreservesCardOrder_AndCollectorIsDeterministic()
    {
        BookRecord first = Book("first") with { Identifiers = [new() { Scheme = "ISBN-13", Value = "9780140328721" }] };
        BookRecord duplicate = first with { Id = "richer", Source = BookCatalogSource.GoogleBooks, Description = "Independent description" };
        BookRecord another = Book("another") with { Title = "Other Book" };
        var slow = NewCompletion();
        var service = new BookCatalogService([
            Provider(BookCatalogSource.OpenLibrary, (_, _) => Task.FromResult(new BookSearchPage { Items = [first, another], Total = 2 })),
            Provider(BookCatalogSource.GoogleBooks, (_, _) => slow.Task)]);
        using var vm = ViewModel(service);
        vm.SearchQuery = "Other";
        Task search = vm.SearchBooksCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "another", "first" }, vm.Books.Select(item => item.Id));
        slow.SetResult(new BookSearchPage { Items = [duplicate], Total = 1 });
        await search;
        Assert.Equal(new[] { "another", "richer" }, vm.Books.Select(item => item.Id));
        Assert.Equal("Independent description", vm.Books[1].Description);
        Assert.Equal(3, vm.TotalAvailable);
        BookSearchPage collected = await service.SearchAsync("Other");
        Assert.Equal(2, collected.Items.Count);
        Assert.Equal(vm.Books.Select(item => item.Id), collected.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task ExplicitCancel_DetachesImmediately_FromProviderIgnoringCancellation()
    {
        var slow = NewCompletion();
        CancellationToken ownedToken = default;
        using var vm = ViewModel(new BookCatalogService([
            Provider(BookCatalogSource.OpenLibrary, (_, _) => Task.FromResult(Page("fast"))),
            Provider(BookCatalogSource.AnnasArchive, (_, token) => { ownedToken = token; return slow.Task; })]));
        vm.SearchQuery = "Book";
        Task search = vm.SearchBooksCommand.ExecuteAsync(null);
        vm.CancelSearchCommand.Execute(null);
        await search.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.True(ownedToken.IsCancellationRequested);
        Assert.False(vm.IsBusy);
        Assert.Contains("Search canceled", vm.StatusMessage);
        Assert.Empty(vm.ErrorMessage);
        slow.SetResult(Page("late"));
        Assert.Equal("fast", Assert.Single(vm.Books).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewQuery_RejectsLateOldResultsAndErrors(bool failure)
    {
        var old = NewCompletion();
        using var vm = ViewModel(new BookCatalogService([
            Provider(BookCatalogSource.OpenLibrary, (query, _) => query == "old" ? old.Task : Task.FromResult(Page("new")))]));
        vm.SearchQuery = "old";
        Task oldSearch = vm.SearchBooksCommand.ExecuteAsync(null);
        vm.SearchQuery = "new";
        await vm.SearchBooksCommand.ExecuteAsync(null);
        await oldSearch.WaitAsync(TimeSpan.FromMilliseconds(500));
        if (failure) old.SetException(new HttpRequestException("old query failed"));
        else old.SetResult(Page("old"));
        Assert.Equal("new", Assert.Single(vm.Books).Id);
        Assert.Equal("Results for “new”", vm.StatusMessage);
        Assert.Empty(vm.ErrorMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Import_CancelsOlderSearch_AndCanceledImportPreservesFilesAndResults()
    {
        string path = Path.Combine(Path.GetTempPath(), $"book-progressive-{Guid.NewGuid():N}.pdf");
        var slow = NewCompletion();
        using var vm = ViewModel(new BookCatalogService([
            Provider(BookCatalogSource.OpenLibrary, (_, _) => slow.Task)]));
        await File.WriteAllTextAsync(path, "%PDF-1.4\nowned test book\n%%EOF");
        try
        {
            vm.Books.Add(Book("previous"));
            vm.SearchQuery = "old";
            Task search = vm.SearchBooksCommand.ExecuteAsync(null);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Null(await vm.ImportLocalFileAsync(path, cancellation.Token));
            await search.WaitAsync(TimeSpan.FromMilliseconds(500));
            slow.SetResult(Page("old"));
            Assert.Equal("previous", Assert.Single(vm.Books).Id);
            Assert.Equal("Import canceled. Existing results retained.", vm.StatusMessage);
            Assert.False(vm.IsBusy);
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task InitialBrowse_CanBeCanceled_AndIteratorDisposalCancelsOwnedWork()
    {
        var slow = NewCompletion();
        CancellationToken ownedToken = default;
        var service = new BookCatalogService([
            Provider(BookCatalogSource.OpenLibrary, (_, _) => Task.FromResult(Page("fast"))),
            Provider(BookCatalogSource.AnnasArchive, (_, token) => { ownedToken = token; return slow.Task; })]);
        using var vm = ViewModel(service);
        Task browse = vm.InitializeAsync();
        Assert.True(vm.CancelSearchCommand.CanExecute(null));
        vm.CancelSearchCommand.Execute(null);
        await browse.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.False(vm.IsBusy);
        Assert.True(ownedToken.IsCancellationRequested);
        await using (var iterator = service.SearchUpdatesAsync("Book").GetAsyncEnumerator())
            Assert.True(await iterator.MoveNextAsync());
        Assert.True(ownedToken.IsCancellationRequested);
        slow.SetResult(Page("late"));
    }

    [Theory]
    [InlineData(BookSearchOutcome.Completed)]
    [InlineData(BookSearchOutcome.Unavailable)]
    [InlineData(BookSearchOutcome.TimedOut)]
    public async Task EmptyResults_DoNotMisrepresentProviderFailure(BookSearchOutcome outcome)
    {
        using var vm = ViewModel(new BookCatalogService([
            Provider(BookCatalogSource.OpenLibrary, (_, _) => Task.FromResult(new BookSearchPage { Outcome = outcome }))]));
        vm.Books.Add(Book("previous"));
        vm.SearchQuery = "missing";
        await vm.SearchBooksCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy);
        if (outcome == BookSearchOutcome.Completed)
        {
            Assert.Empty(vm.Books);
            Assert.Equal("No matching books were found.", vm.StatusMessage);
            Assert.Empty(vm.ErrorMessage);
        }
        else
        {
            Assert.Equal("previous", Assert.Single(vm.Books).Id);
            Assert.Contains("Search incomplete", vm.StatusMessage);
            Assert.NotEmpty(vm.ErrorMessage);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpProvider_DeadlineIncludesBodyParsing_AndDisposesResponse(bool google)
    {
        var body = new StalledBody();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(body) }))) { Timeout = TimeSpan.FromMilliseconds(100) };
        IBookCatalogProvider provider = HttpProvider(client, google);
        var clock = Stopwatch.StartNew();
        BookSearchPage result = await provider.SearchAsync("Book").WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(BookSearchOutcome.TimedOut, result.Outcome);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(600));
        Assert.True(body.Canceled);
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpProvider_CallerCancellationIsNotProviderTimeout(bool google)
    {
        var body = new StalledBody();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(body) }))) { Timeout = TimeSpan.FromSeconds(30) };
        using var cancellation = new CancellationTokenSource();
        Task<BookSearchPage> search = HttpProvider(client, google).SearchAsync("Book", cancellationToken: cancellation.Token);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await search.WaitAsync(TimeSpan.FromMilliseconds(500)));
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HttpProvider_ErrorStatusAndMalformedBodyAreUnavailable(bool google, bool malformed)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(
            malformed ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent("not json", Encoding.UTF8, "application/json") })));
        Assert.Equal(BookSearchOutcome.Unavailable, (await HttpProvider(client, google).SearchAsync("Book")).Outcome);
    }

    private static IBookCatalogProvider HttpProvider(HttpClient client, bool google) => google
        ? new GoogleBooksBookProvider(client, baseUri: new Uri("https://books.test/"))
        : new OpenLibraryBookProvider(client, new Uri("https://books.test/"));
    private static TaskCompletionSource<BookSearchPage> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static BookRecord Book(string id) => new() { Id = id, Title = "Book", Source = BookCatalogSource.OpenLibrary };
    private static BookSearchPage Page(string id) => new() { Items = [Book(id)], Total = 1 };
    private static DelegateProvider Provider(BookCatalogSource source, Func<string, CancellationToken, Task<BookSearchPage>> search) => new(source, search);
    private static BookBrowseViewModel ViewModel(BookCatalogService service) => new(service, new LocalBookImportService(), new EmptyProgress());

    private sealed class DelegateProvider(BookCatalogSource source, Func<string, CancellationToken, Task<BookSearchPage>> search) : IBookCatalogProvider
    {
        public BookCatalogSource Source => source;
        public Task<BookSearchPage> SearchAsync(string query, int offset = 0, int limit = 24, CancellationToken cancellationToken = default) => search(query, cancellationToken);
        public Task<BookRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<BookRecord?>(null);
    }
    private sealed class EmptyProgress : IReadingProgressStore
    {
        public Task<BookReadingProgress?> GetAsync(string bookId, string assetId, CancellationToken cancellationToken = default) => Task.FromResult<BookReadingProgress?>(null);
        public Task<IReadOnlyList<BookReadingProgress>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BookReadingProgress>>([]);
        public Task SaveAsync(BookReadingProgress progress, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(string bookId, string assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }
    private sealed class StalledBody : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Canceled { get; private set; }
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Canceled = true; throw; }
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
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
