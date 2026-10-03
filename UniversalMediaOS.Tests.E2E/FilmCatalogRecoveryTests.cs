using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class FilmCatalogRecoveryTests
{
    private const string Search = """{"query":{"search":[{"title":"Q60834962"},{"title":"Q114819"}]},"continue":{"sroffset":2}}""";
    private static string Entities => JsonSerializer.Serialize(new { entities = new Dictionary<string, object>
    {
        ["Q60834962"] = Entity("Q60834962", "Dune", 2021, "tt1160419"),
        ["Q114819"] = Entity("Q114819", "Dune", 1984, "tt0087182")
    } });

    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("saved-key", false)]
    [InlineData("saved-key", true)]
    public async Task MovieCatalogUsesTypedFilmsAndExactIdPostersRegardlessOfLegacySettings(string key, bool archive)
    {
        using var profile = new Config(key, archive);
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var service = new MovieService(profile.Value, http);
        var page = await service.GetPageAsync(new(AudiovisualMediaKind.Movie, AudiovisualCatalogMode.Search, "Dune"));
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(new int?[] { 2021, 1984 }, page.Items.Select(i => i.Identity.Year));
        Assert.All(page.Items, item =>
        {
            Assert.Equal(AudiovisualContentForm.Feature, item.Identity.ContentForm);
            Assert.Equal("wikidata", item.Identity.PrimaryId!.Provider);
            Assert.Null(item.Identity.TmdbId);
            Assert.Equal("https://m.media-amazon.com/" + item.Identity.ImdbId + ".jpg", item.PosterUrl);
            Assert.Equal("A film", item.Overview);
            var card = new AudiovisualCardViewModel(item);
            Assert.Contains(item.Identity.PrimaryId.Value, card.MetadataAttributionUrl!);
            Assert.Contains(item.Identity.ImdbId, card.PosterAttributionUrl!);
        });
        Assert.NotNull(page.NextToken);
        Assert.All(handler.Uris.Where(uri => uri.Host == "www.wikidata.org"), uri => Assert.DoesNotContain("maxlag", uri.Query));
        Assert.DoesNotContain(handler.Uris, uri => uri.Host.Contains("themoviedb", StringComparison.Ordinal) || uri.Host == "archive.org");
        int calls = handler.Uris.Count;
        var repeated = await service.GetPageAsync(new(AudiovisualMediaKind.Movie, AudiovisualCatalogMode.Search, "Dune"));
        Assert.Equal(page.Items, repeated.Items);
        Assert.Equal(calls, handler.Uris.Count);
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("series")]
    [InlineData("untrusted-host")]
    [InlineData("broken-json")]
    public async Task BadArtworkCannotReplaceIdentityOrHideValidFilms(string mode)
    {
        var handler = new Handler { Poster = (uri, _) => Task.FromResult(Json(mode switch
        {
            "wrong-id" => Poster("tt15239678"),
            "series" => Poster(Path.GetFileNameWithoutExtension(uri.AbsolutePath), "tvSeries"),
            "untrusted-host" => Poster(Path.GetFileNameWithoutExtension(uri.AbsolutePath), url: "https://untrusted.example/poster.jpg"),
            _ => "not-json"
        })) };
        using var http = new HttpClient(handler);
        var result = await new WikidataMetadataClient(new(http), includePosters: true).GetPageAsync(new(AudiovisualMediaKind.Movie));
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.Empty(item.PosterUrl));
        Assert.NotNull(result.NextToken);
        Assert.True(result.IsPartial);
    }

    [Fact]
    public async Task PosterDeadlineRetainsFilmsAndContinuationWithoutCancellingTheCaller()
    {
        bool posterCancelled = false;
        var handler = new Handler { Poster = async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { posterCancelled = token.IsCancellationRequested; throw; }
            return Json("{}");
        } };
        using var http = new HttpClient(handler);
        using var caller = new CancellationTokenSource();
        var client = new WikidataMetadataClient(new(http), includePosters: true, posterBudget: TimeSpan.FromMilliseconds(50));
        var page = await client.GetPageAsync(new(AudiovisualMediaKind.Movie), caller.Token);
        Assert.True(posterCancelled);
        Assert.False(caller.IsCancellationRequested);
        Assert.Equal(2, page.Items.Count);
        Assert.NotNull(page.NextToken);
        Assert.True(page.IsPartial);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExactIdArtworkReplacesCommonsRedirectButFailureRetainsTheExplicitPoster(bool available)
    {
        string payload = JsonSerializer.Serialize(new { entities = new Dictionary<string, object>
        {
            ["Q60834962"] = Entity("Q60834962", "Dune", 2021, "tt1160419", "Dune poster.jpg"),
            ["Q114819"] = Entity("Q114819", "Dune", 1984, "tt0087182", "Dune 1984 poster.jpg")
        } });
        var handler = new Handler { EntityPayload = payload, Poster = (uri, _) => Task.FromResult(
            available ? Json(Poster(Path.GetFileNameWithoutExtension(uri.AbsolutePath)))
                : new HttpResponseMessage(HttpStatusCode.NotFound)) };
        using var http = new HttpClient(handler);
        var page = await new WikidataMetadataClient(new(http), includePosters: true).GetPageAsync(new(AudiovisualMediaKind.Movie));
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, item => Assert.Equal(available ? "m.media-amazon.com" : "commons.wikimedia.org",
            new Uri(item.PosterUrl).Host));
        Assert.Equal(new int?[] { 2021, 1984 }, page.Items.Select(item => item.Identity.Year));
        Assert.NotNull(page.NextToken);
    }

    [Fact]
    public async Task CallerCancellationDuringArtworkStillCancelsTheCatalogRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler { Poster = async (_, token) =>
        { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json("{}"); } };
        using var http = new HttpClient(handler);
        using var caller = new CancellationTokenSource();
        var task = new WikidataMetadataClient(new(http), includePosters: true).GetPageAsync(new(AudiovisualMediaKind.Movie), caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task MovieProviderFailureStaysVisibleWithoutFallingBackToGeneralArchiveVideos()
    {
        using var profile = new Config("saved-key", true);
        var handler = new Handler { MetadataStatus = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(handler);
        var page = await new MovieService(profile.Value, http).GetPageAsync(new(AudiovisualMediaKind.Movie));
        Assert.Empty(page.Items);
        Assert.Equal(ProviderOutcomeStatus.Unavailable, Assert.Single(page.Outcomes).Status);
        Assert.Equal("www.wikidata.org", Assert.Single(handler.Uris).Host);
    }

    private static object Entity(string id, string title, int year, string imdb, string poster = "") => new
    {
        id, labels = new { en = new { value = title } }, descriptions = new { en = new { value = "A film" } },
        claims = new Dictionary<string, object>
        { ["P31"] = Claim(new { id = "Q11424" }), ["P345"] = Claim(imdb), ["P577"] = Claim(new { time = $"+{year}-01-01T00:00:00Z", precision = 9 }), ["P3383"] = Claim(poster) }
    };
    private static object Claim(object value) => new[] { new { rank = "normal", mainsnak = new { snaktype = "value", datavalue = new { value } } } };
    private static string Poster(string id, string type = "movie", string? url = null) => JsonSerializer.Serialize(new
    { d = new[] { new { id, qid = type, i = new { imageUrl = url ?? "https://m.media-amazon.com/" + id + ".jpg" } } } });
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];
        public HttpStatusCode MetadataStatus { get; init; } = HttpStatusCode.OK;
        public string EntityPayload { get; init; } = Entities;
        public Func<Uri, CancellationToken, Task<HttpResponseMessage>>? Poster { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Uris.Add(uri);
            Assert.Null(request.Headers.Authorization);
            if (uri.Host == "v3.sg.media-imdb.com")
                return Poster?.Invoke(uri, token) ?? Task.FromResult(Json(FilmCatalogRecoveryTests.Poster(Path.GetFileNameWithoutExtension(uri.AbsolutePath))));
            Assert.Equal("www.wikidata.org", uri.Host);
            return Task.FromResult(new HttpResponseMessage(MetadataStatus)
            { Content = new StringContent(uri.Query.Contains("wbgetentities", StringComparison.Ordinal) ? EntityPayload : Search) });
        }
    }

    private sealed class Config : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.FilmRecovery", Guid.NewGuid().ToString("N"));
        public DomainHotSwapper Value { get; }
        public Config(string key, bool archive)
        {
            Directory.CreateDirectory(_root);
            string path = Path.Combine(_root, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string>
            { [AudiovisualOptions.TmdbApiKeyConfigKey] = key, [AudiovisualOptions.InternetArchiveEnabledConfigKey] = archive.ToString() }));
            Value = new(path);
        }
        public void Dispose()
        {
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.FilmRecovery")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true);
        }
    }
}
