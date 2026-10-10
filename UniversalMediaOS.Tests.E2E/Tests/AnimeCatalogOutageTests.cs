using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class AnimeCatalogOutageTests
{
    [Fact]
    public async Task MissingPageCannotBeReportedAsEmptyCatalog()
    {
        using var fixture = new Fixture();
        fixture.Handler.Response = (_, _) => Task.FromResult(Json("{\"data\":{}}"));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Catalog.SearchAnimePageAsync("requested"));
    }

    [Fact]
    public async Task RecommendationFailureRetainsPreviouslyLoadedCards()
    {
        using var fixture = new Fixture();
        fixture.View.SearchQuery = "original";
        await fixture.View.SearchCommand.ExecuteAsync(null);
        var original = Assert.Single(fixture.View.SearchResults);
        fixture.Handler.Response = (_, _) => Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable));
        fixture.View.SearchQuery = "";
        await fixture.View.SearchCommand.ExecuteAsync(null);
        Assert.Same(original, Assert.Single(fixture.View.SearchResults));
        Assert.Contains("unavailable", fixture.View.ResultsDescription);
        Assert.False(fixture.View.IsSearching);
        Assert.False(fixture.View.HasNoResults);
        await fixture.View.LoadMoreAnimeCommand.ExecuteAsync(null);
        Assert.Equal(2, fixture.Handler.Queries.Count);
    }

    [Fact]
    public async Task FailedNewQueryCannotAppendItsNextPageToRetainedCardsAndRetryStartsAtOne()
    {
        using var fixture = new Fixture();
        fixture.View.SearchQuery = "original";
        await fixture.View.SearchCommand.ExecuteAsync(null);
        var original = Assert.Single(fixture.View.SearchResults);
        fixture.Handler.Response = (_, _) => Task.FromResult(Json("{\"data\":{}}"));
        fixture.View.SearchQuery = "replacement";
        await fixture.View.SearchCommand.ExecuteAsync(null);
        Assert.Same(original, Assert.Single(fixture.View.SearchResults));
        Assert.Equal(421, original.Id);
        Assert.Equal(4210, original.IdMal);
        Assert.False(original.DubAvailabilityChecked);
        await fixture.View.LoadMoreAnimeCommand.ExecuteAsync(null);
        Assert.Equal(2, fixture.Handler.Queries.Count);
        fixture.Handler.Response = (_, _) => Task.FromResult(Json(Page(730)));
        await fixture.View.SearchCommand.ExecuteAsync(null);
        Assert.Equal(730, Assert.Single(fixture.View.SearchResults).Id);
        Assert.Equal(("replacement", 1), fixture.Handler.Queries.Last());
        Assert.False(fixture.View.HasNoResults);
        Assert.DoesNotContain("unavailable", fixture.View.ResultsDescription);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshOutageOrProviderTimeoutDoesNotClaimNoMatchesOrCallerCancellation(bool timeout)
    {
        using var fixture = new Fixture();
        fixture.Handler.Response = (_, _) => timeout
            ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("Provider-owned timeout"))
            : Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable));
        fixture.View.SearchQuery = "requested";
        await fixture.View.SearchCommand.ExecuteAsync(null);
        Assert.Empty(fixture.View.SearchResults);
        Assert.Contains("unavailable", fixture.View.ResultsDescription);
        Assert.DoesNotContain("canceled", fixture.View.ResultsDescription);
        Assert.False(fixture.View.HasNoResults);
        Assert.False(fixture.View.IsSearching);
    }

    [Fact]
    public async Task SuccessfulEmptyPageStillClearsPreviousCardsAndShowsConfirmedNoResults()
    {
        using var fixture = new Fixture();
        fixture.View.SearchQuery = "original";
        await fixture.View.SearchCommand.ExecuteAsync(null);
        fixture.Handler.Response = (_, _) => Task.FromResult(Json("{\"data\":{\"Page\":{\"media\":[],\"pageInfo\":{\"hasNextPage\":false}}}}"));
        fixture.View.SearchQuery = "empty";
        await fixture.View.SearchCommand.ExecuteAsync(null);
        Assert.Empty(fixture.View.SearchResults);
        Assert.True(fixture.View.HasNoResults);
        Assert.False(fixture.View.IsSearching);
        Assert.DoesNotContain("unavailable", fixture.View.ResultsDescription);
    }

    [Fact]
    public async Task CallerCancelDetachesAndRetainsCardsWithoutPagingTheCanceledQuery()
    {
        using var fixture = new Fixture();
        fixture.View.SearchQuery = "original";
        await fixture.View.SearchCommand.ExecuteAsync(null);
        var original = Assert.Single(fixture.View.SearchResults);
        var blocked = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Response = (_, _) => blocked.Task;
        fixture.View.SearchQuery = "canceled";
        Task lookup = fixture.View.SearchCommand.ExecuteAsync(null);
        fixture.View.CancelActiveSearchCommand.Execute(null);
        await lookup.WaitAsync(TimeSpan.FromSeconds(1));
        blocked.TrySetResult(Json(Page(999)));
        Assert.Same(original, Assert.Single(fixture.View.SearchResults));
        Assert.Contains("canceled", fixture.View.ResultsDescription);
        Assert.DoesNotContain("unavailable", fixture.View.ResultsDescription);
        Assert.False(fixture.View.IsSearching);
        await fixture.View.LoadMoreAnimeCommand.ExecuteAsync(null);
        Assert.Equal(2, fixture.Handler.Queries.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongRateLimitDoesNotSpinOrRetryBeforeTheServerAllowsIt(bool date)
    {
        using var fixture = new Fixture();
        fixture.Handler.Response = (_, _) =>
        {
            var response = Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = date
                ? new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(1))
                : new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        };
        fixture.View.SearchQuery = "requested";
        await fixture.View.SearchCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Single(fixture.Handler.Queries);
        Assert.Contains("unavailable", fixture.View.ResultsDescription);
        Assert.False(fixture.View.IsSearching);
        Assert.False(fixture.View.HasNoResults);
    }

    [Fact]
    public async Task RetiredInitializationCannotChangeTheSuccessfulNewQueryOutcome()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Response = (_, _) =>
        {
            if (fixture.Handler.Queries.Count == 1)
            {
                started.TrySetResult();
                return old.Task;
            }
            return Task.FromResult(Json("{\"data\":{\"Page\":{\"media\":[],\"pageInfo\":{\"hasNextPage\":false}}}}"));
        };
        fixture.View.Initialize();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        fixture.View.SearchQuery = "current";
        await fixture.View.SearchCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(1));
        old.TrySetException(new HttpRequestException("Retired initialization failed"));
        Assert.Empty(fixture.View.SearchResults);
        Assert.True(fixture.View.HasNoResults);
        Assert.False(fixture.View.IsSearching);
        Assert.Equal("AniList results for \"current\"", fixture.View.ResultsDescription);
    }

    [Fact]
    public async Task AllowedRateLimitBackoffStillRetriesOnceAndRetainsTheCatalogIdentity()
    {
        using var fixture = new Fixture();
        int requests = 0;
        fixture.Handler.Response = (_, _) =>
        {
            if (Interlocked.Increment(ref requests) > 1) return Task.FromResult(Json(Page(421)));
            var response = Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        };
        fixture.View.SearchQuery = "requested";
        await fixture.View.SearchCommand.ExecuteAsync(null);
        Assert.Equal(2, fixture.Handler.Queries.Count);
        Assert.Equal(421, Assert.Single(fixture.View.SearchResults).Id);
        Assert.False(fixture.View.IsSearching);
        Assert.DoesNotContain("unavailable", fixture.View.ResultsDescription);
    }

    [Fact]
    public void ActualCatalogKeepsItsCardThroughOutageAndSuccessfulRetry()
    {
        int unavailable = 0;
        using var fixture = new AppFixture(mangaPagePng: null, aniListFeed: request =>
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            if (!document.RootElement.TryGetProperty("variables", out var variables))
                return (200, "{\"data\":{\"GenreCollection\":[],\"MediaTagCollection\":[]}}");
            bool isSearch = variables.TryGetProperty("search", out var query) && query.GetString() == "replacement";
            return Volatile.Read(ref unavailable) == 1 ? (503, "{}") : (200, Page(isSearch ? 730 : 421, hasNextPage: false));
        });
        Window Window() => fixture.MainWindow;
        bool Visible(string name) => Window().FindAllDescendants(cf => cf.ByName(name)).Any(element => !element.IsOffscreen);
        void Wait(Func<bool> condition, string reason) => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), reason);
        Wait(() => Visible("Retained Catalog Anime"), "The original catalog card must render.");
        Window().FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))!.AsTextBox().Text = "replacement";
        Interlocked.Exchange(ref unavailable, 1);
        Window().FindFirstDescendant(cf => cf.ByName("ExecuteSearch"))!.AsButton().Invoke();
        Wait(() => Visible("Anime catalog unavailable. Previous results retained; retry search."), "The real failed HTTP lookup must expose unavailable state.");
        Assert.True(Visible("Retained Catalog Anime"));
        Assert.False(Visible("No anime found"));
        Assert.False(Visible("Cancel search"));
        Interlocked.Exchange(ref unavailable, 0);
        Window().FindFirstDescendant(cf => cf.ByName("ExecuteSearch"))!.AsButton().Invoke();
        Wait(() => Visible("Recovered Catalog Anime") && Visible("AniList results for \"replacement\"") && !Visible("Cancel search"),
            "Successful retry must render the recovered response and restore the exact query state.");
        Assert.False(Visible("Retained Catalog Anime"));
        Assert.False(Visible("Anime catalog unavailable. Previous results retained; retry search."));
        Assert.False(fixture.App.HasExited);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(body) };

    private static string Page(int id, bool hasNextPage = true) => JsonSerializer.Serialize(new { data = new { Page = new
    {
        pageInfo = new { hasNextPage },
        media = new[] { new { id, idMal = id * 10, format = "TV", episodes = 12,
            title = new { english = id == 421 ? "Retained Catalog Anime" : "Recovered Catalog Anime",
                romaji = id == 421 ? "Retained Catalog Anime" : "Recovered Catalog Anime" },
            coverImage = new { extraLarge = "" } } }
    } } });

    private sealed class Handler : HttpMessageHandler
    {
        public ConcurrentQueue<(string Query, int Page)> Queries { get; } = new();
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Response = (_, _) => Task.FromResult(Json(Page(421)));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (!document.RootElement.TryGetProperty("variables", out var variables))
                return Json("{\"data\":{\"GenreCollection\":[],\"MediaTagCollection\":[]}}");
            Queries.Enqueue((variables.TryGetProperty("search", out var query) ? query.GetString()! : "", variables.GetProperty("page").GetInt32()));
            return await Response(request, token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", "AnimeCatalog-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _client;
        public Handler Handler { get; } = new();
        public FuzzyShieldSearch Catalog { get; }
        public SearchViewModel View { get; }
        public Fixture()
        {
            Directory.CreateDirectory(_root);
            string path = Path.Combine(_root, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string,string>
                { ["AniListUrl"] = "https://catalog.invalid/graphql", ["DubAvailabilityProviders"] = "[]" }));
            var config = new DomainHotSwapper(path);
            _client = new(Handler);
            Catalog = new(config, _client);
            View = new(Catalog, new DubAvailabilityService(config), new FavoriteMediaService(Path.Combine(_root, "favorites.json")));
        }
        public void Dispose()
        {
            View.Dispose();
            _client.Dispose();
            Assert.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests")) + Path.DirectorySeparatorChar,
                Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
            Assert.Null(new DirectoryInfo(_root).LinkTarget);
            Directory.Delete(_root, recursive: true);
        }
    }
}
