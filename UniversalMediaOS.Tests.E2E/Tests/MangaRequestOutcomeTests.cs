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

    private sealed class MangaServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly string _root;
        public int Status = 503;
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
                    context.Response.StatusCode = Status;
                    context.Response.ContentType = "application/json";
                    byte[] body = Encoding.UTF8.GetBytes("{\"data\":[],\"total\":0,\"baseUrl\":\"http://127.0.0.1\",\"chapter\":{\"hash\":\"empty\",\"data\":[]}}");
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
