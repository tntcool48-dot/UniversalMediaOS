using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class MangaRequestOutcomeTests
{
    [Theory]
    [InlineData("recommendations")]
    [InlineData("search")]
    [InlineData("chapters")]
    [InlineData("pages")]
    public async Task FailedRequestsDoNotBecomeVerifiedEmptyResultsAndCanRetry(string operation)
    {
        await using var fixture = new MangaServer();
        var service = new MangaService(fixture.Config);
        Task<int> Fetch() => FetchCountAsync(service, operation);
        var failure = await Assert.ThrowsAsync<HttpRequestException>(Fetch);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Contains("UniversalMediaOS/", fixture.LastUserAgent);

        fixture.Status = 200;
        Assert.Equal(0, await Fetch()); // A successful empty response is different from a failed request.
    }

    private static async Task<int> FetchCountAsync(MangaService service, string operation) => operation switch
    {
        "recommendations" => (await service.GetRecommendedMangaAsync()).Count,
        "search" => (await service.SearchMangaAsync("fixture")).Count,
        "chapters" => (await service.GetChaptersAsync("fixture-id")).Count,
        "pages" => (await service.GetPageUrlsAsync("fixture-chapter")).Count,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    [Fact]
    public async Task SearchFailureRetainsUsableCardsAndReportsFailure()
    {
        await using var fixture = new MangaServer();
        using var viewModel = new MangaViewModel(new(fixture.Config));
        var retained = new MangaSearchResult { Id = "retained", Title = "Previous exact result" };
        viewModel.MangaResults.Add(retained);
        viewModel.SearchQuery = "new query";
        await viewModel.SearchMangaCommand.ExecuteAsync(null);
        Assert.Same(retained, Assert.Single(viewModel.MangaResults));
        Assert.Equal("MangaDex search failed; showing previous results", viewModel.ResultsDescription);
        Assert.False(viewModel.IsSearching);
    }

    [Fact]
    public async Task ChapterFailureIsDifferentFromSuccessfulEmptyLookupAndCanRetry()
    {
        await using var fixture = new MangaServer();
        using var viewModel = new MangaViewModel(new(fixture.Config));
        var manga = new MangaSearchResult { Id = "fixture", Title = "Exact fixture" };
        await viewModel.ReadCommand.ExecuteAsync(manga);
        Assert.Equal(1, viewModel.CurrentViewMode);
        Assert.Equal("Could not load chapters. Go back and retry.", viewModel.ReaderStatus);
        Assert.False(viewModel.IsLoadingChapters);

        fixture.Status = 200;
        await viewModel.ReadCommand.ExecuteAsync(manga);
        Assert.Empty(viewModel.Chapters);
        Assert.Equal("No English chapters are available for this manga.", viewModel.ReaderStatus);
    }

    [Fact]
    public async Task PageFailureRetainsTheSelectedMangasChapterChoicesForRetry()
    {
        await using var fixture = new MangaServer();
        using var viewModel = new MangaViewModel(new(fixture.Config));
        viewModel.SelectedManga = new() { Id = "fixture", Title = "Exact fixture" };
        var chapter = new MangaChapter { Id = "fixture-chapter", ChapterNumber = "1", Pages = 2 };
        viewModel.Chapters.Add(chapter);
        await viewModel.SelectChapterCommand.ExecuteAsync(chapter);
        Assert.Equal(1, viewModel.CurrentViewMode);
        Assert.Same(chapter, Assert.Single(viewModel.Chapters));
        Assert.Equal("Could not load chapter pages. Select a chapter to retry.", viewModel.ReaderStatus);
        Assert.Empty(viewModel.PageUrls);
        Assert.False(viewModel.IsLoadingPages);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("malformed")]
    [InlineData("premature-empty")]
    public async Task LaterChapterFailureRetainsUsableChoicesReportsIncompleteAndCanRetry(string failure)
    {
        await using var fixture = new MangaServer();
        var offsets = new List<int>();
        bool retry = false;
        string Feed(int first, int count) => JsonSerializer.Serialize(new
        {
            total = 101,
            data = Enumerable.Range(first, count).Select(number => new
            {
                id = $"exact-chapter-{number}",
                attributes = new { chapter = number.ToString(), title = $"Chapter {number}", pages = 2 }
            })
        });
        fixture.Reply = request =>
        {
            if (request.Url!.AbsolutePath.Contains("/at-home/", StringComparison.Ordinal))
                return (200, "{\"baseUrl\":\"http://127.0.0.1\",\"chapter\":{\"hash\":\"fixture\",\"data\":[\"page.png\"]}}");
            int offset = int.Parse(request.QueryString["offset"]!);
            offsets.Add(offset);
            if (offset == 0) return (200, Feed(1, 100));
            if (retry) return (200, Feed(101, 1));
            return failure switch
            {
                "http" => (503, "{\"error\":\"unavailable\"}"),
                "malformed" => (200, "{invalid-json"),
                _ => (200, Feed(101, 0))
            };
        };
        using var viewModel = new MangaViewModel(new(fixture.Config));
        var manga = new MangaSearchResult { Id = "exact-manga", Title = "Exact same-title work" };
        await viewModel.ReadCommand.ExecuteAsync(manga);
        Assert.Equal(Enumerable.Range(1, 100).Select(number => $"exact-chapter-{number}"),
            viewModel.Chapters.Select(chapter => chapter.Id));
        Assert.Equal("Some chapters could not load. Showing available chapters. Go back and retry.", viewModel.ReaderStatus);
        Assert.Equal(new[] { 0, 100 }, offsets);
        Assert.Same(manga, viewModel.SelectedManga);
        Assert.Equal(1, viewModel.CurrentViewMode);
        Assert.False(viewModel.IsLoadingChapters);

        await viewModel.SelectChapterCommand.ExecuteAsync(viewModel.Chapters[0]);
        Assert.Equal(2, viewModel.CurrentViewMode);
        viewModel.GoBackCommand.Execute(null);
        Assert.Equal(1, viewModel.CurrentViewMode);
        Assert.Equal("Some chapters could not load. Showing available chapters. Go back and retry.", viewModel.ReaderStatus);

        viewModel.GoBackCommand.Execute(null);
        retry = true;
        await viewModel.ReadCommand.ExecuteAsync(manga);
        Assert.Equal(Enumerable.Range(1, 101).Select(number => $"exact-chapter-{number}"),
            viewModel.Chapters.Select(chapter => chapter.Id));
        Assert.Empty(viewModel.ReaderStatus);
        Assert.Equal(new[] { 0, 100, 0, 100 }, offsets);
    }

    [Fact]
    public async Task ShortChapterResponseAdvancesByActualCountWithoutSkippingUnits()
    {
        await using var fixture = new MangaServer();
        var offsets = new List<int>();
        fixture.Reply = request =>
        {
            int offset = int.Parse(request.QueryString["offset"]!);
            offsets.Add(offset);
            return (200, JsonSerializer.Serialize(new
            {
                total = 3,
                data = Enumerable.Range(offset + 1, Math.Min(2, Math.Max(0, 3 - offset))).Select(number => new
                {
                    id = $"short-page-chapter-{number}",
                    attributes = new { chapter = number.ToString(), title = $"Chapter {number}", pages = 2 }
                })
            }));
        };
        var chapters = await new MangaService(fixture.Config).GetChaptersAsync("exact-work");
        Assert.Equal(new[] { "short-page-chapter-1", "short-page-chapter-2", "short-page-chapter-3" },
            chapters.Select(chapter => chapter.Id));
        Assert.Equal(new[] { 0, 2 }, offsets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackNavigationDoesNotRestoreAnOldFailedOrPartialLookup(bool partial)
    {
        var service = new DelayedFailureService();
        using var viewModel = new MangaViewModel(service);
        Task read = viewModel.ReadCommand.ExecuteAsync(new MangaSearchResult { Id = "old-work", Title = "Old work" });
        viewModel.GoBackCommand.Execute(null);
        Exception error = new HttpRequestException("Fixture failure");
        if (partial)
            error = new IncompleteMangaChaptersException(new[] { new MangaChapter { Id = "old-unit", ChapterNumber = "1" } }, error);
        service.Result.SetException(error);
        await read;
        Assert.Empty(viewModel.Chapters);
        Assert.Empty(viewModel.ReaderStatus);
        Assert.Null(viewModel.SelectedManga);
        Assert.Equal(0, viewModel.CurrentViewMode);
    }

    private sealed class DelayedFailureService : MangaService
    {
        public TaskCompletionSource<List<MangaChapter>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<List<MangaChapter>> GetChaptersAsync(string mangaId, CancellationToken token = default) => Result.Task;
    }

    private sealed class MangaServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly string _root;
        public int Status = 503;
        public Func<HttpListenerRequest, (int Status, string Body)>? Reply;
        public string LastUserAgent = string.Empty;
        public DomainHotSwapper Config { get; }

        public MangaServer()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            string baseUrl = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(baseUrl);
            _listener.Start();
            _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", "MangaRequest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            string configPath = Path.Combine(_root, "config.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(new Dictionary<string, string> { ["MangaDexUrl"] = baseUrl }));
            Config = new(configPath);
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
                    catch (OperationCanceledException) { break; }
                    LastUserAgent = context.Request.UserAgent ?? string.Empty;
                    var reply = Reply?.Invoke(context.Request) ?? (Status, "{\"data\":[],\"total\":0,\"baseUrl\":\"http://127.0.0.1\",\"chapter\":{\"hash\":\"empty\",\"data\":[]}}");
                    context.Response.StatusCode = reply.Item1;
                    context.Response.ContentType = "application/json";
                    byte[] body = Encoding.UTF8.GetBytes(reply.Item2);
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _loop.WaitAsync(TimeSpan.FromSeconds(3));
            _listener.Close();
            _stop.Dispose();
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests")) + Path.DirectorySeparatorChar;
            Assert.StartsWith(parent, Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
            Assert.Null(new DirectoryInfo(_root).LinkTarget);
            Directory.Delete(_root, true);
        }
    }
}
