using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class TvmazeEpisodeAndIndexTests
{
    private static AudiovisualIdentity Show => new()
    { PrimaryId = new("tvmaze", "show", "526"), ContentForm = AudiovisualContentForm.Series };

    [Fact]
    public async Task FullIndexPageSlicesPastOneHundredAndOnly404EndsTheIndex()
    {
        string firstPage = JsonSerializer.Serialize(Enumerable.Range(1, 240).Select(id => new { id, name = $"Show {id}", type = "Scripted" }));
        var handler = new Handler(uri => uri.Query == "?page=0" ? Json(firstPage) :
            uri.Query == "?page=1" ? Json("""[{"id":251,"name":"Next","type":"Scripted"}]""") : new(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var client = new TvmazeMetadataClient(new(http));
        var request = new AudiovisualCatalogRequest(AudiovisualMediaKind.Television, PageSize: 100);
        var one = await client.GetPageAsync(request);
        var two = await client.GetPageAsync(request with { ContinuationToken = one.NextToken });
        var three = await client.GetPageAsync(request with { ContinuationToken = two.NextToken });
        var four = await client.GetPageAsync(request with { ContinuationToken = three.NextToken });
        var end = await client.GetPageAsync(request with { ContinuationToken = four.NextToken });
        Assert.Equal(100, one.Items.Count);
        Assert.Equal(100, two.Items.Count);
        Assert.Equal(40, three.Items.Count);
        Assert.Equal("Next", Assert.Single(four.Items).Title);
        Assert.Empty(end.Items);
        Assert.Null(end.NextToken);
        Assert.Equal("index_complete", end.Outcomes[^1].DiagnosticCode);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task MissingResourceDoesNotBackOffOtherResources()
    {
        var handler = new Handler(uri => uri.AbsolutePath == "/missing" ? new(HttpStatusCode.NotFound) : Json("[]"));
        using var http = new HttpClient(handler);
        var requests = new ProviderRequestCoordinator(http);
        var provider = new AudiovisualProviderDefinition { Id = "test", BaseUrl = "https://example.test", RequestsPerMinute = 0 };
        var missing = await requests.FetchAsync(provider, new("https://example.test/missing"));
        var found = await requests.FetchAsync(provider, new("https://example.test/found"));
        Assert.Equal(ProviderOutcomeStatus.NotFound, missing.Outcome.Status);
        Assert.Null(missing.Outcome.RetryAtUtc);
        Assert.Equal(ProviderOutcomeStatus.Success, found.Outcome.Status);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SparseAnimationRefillIsBoundedAndPreservesContinuation()
    {
        var handler = new Handler(uri => uri.Query == "?page=3" ? Json("""[{"id":751,"name":"Animated","type":"Animation"}]""") : Json("[]"));
        using var http = new HttpClient(handler);
        var client = new TvmazeMetadataClient(new(http));
        var request = new AudiovisualCatalogRequest(AudiovisualMediaKind.Cartoon);
        var empty = await client.GetPageAsync(request);
        Assert.Empty(empty.Items);
        Assert.NotNull(empty.NextToken);
        Assert.Equal(3, handler.Calls);
        var next = await client.GetPageAsync(request with { ContinuationToken = empty.NextToken });
        Assert.Equal("Animated", Assert.Single(next.Items).Title);
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task RecordedEpisodesHaveSeparateSeasonIdentityAndSeasonFiltering()
    {
        using var stream = typeof(TvmazeEpisodeAndIndexTests).Assembly.GetManifestResourceStream(
            "UniversalMediaOS.Tests.E2E.Fixtures.Metadata.tvmaze-office-episodes.json")!;
        using var reader = new StreamReader(stream);
        string payload = reader.ReadToEnd();
        var handler = new Handler(_ => Json(payload));
        using var http = new HttpClient(handler);
        IAudiovisualEpisodeMetadataClient client = new TvmazeMetadataClient(new(http));
        var all = await client.GetUnitsAsync(Show);
        Assert.Equal(203, all.Items.Count);
        var special = Assert.Single(all.Items, e => e.Id.Value == "47839");
        Assert.True(special.IsSpecial);
        Assert.Null(special.Unit);
        var first = Assert.Single(all.Items, e => e.Unit?.SeasonNumber == 1 && e.Unit.EpisodeNumber == 1);
        var second = Assert.Single(all.Items, e => e.Unit?.SeasonNumber == 2 && e.Unit.EpisodeNumber == 1);
        Assert.NotEqual(first.Id, second.Id);
        var season = await client.GetUnitsAsync(Show, 1);
        Assert.All(season.Items, e => Assert.Equal(1, e.SeasonNumber));
        Assert.NotEmpty(season.Items);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("/shows/526/episodes", handler.LastUri!.AbsolutePath);
        Assert.Equal("?specials=1", handler.LastUri.Query);
    }

    [Fact]
    public async Task SpecialsAndUnnumberedEpisodesRetainIdsWithoutGuessedUnits()
    {
        const string payload = """[{"id":1,"name":"Special","type":"significant_special","season":1,"number":null},{"id":2,"name":"Unknown","type":"regular","season":2,"number":null}]""";
        using var http = new HttpClient(new Handler(_ => Json(payload)));
        var result = await new TvmazeMetadataClient(new(http)).GetUnitsAsync(Show);
        Assert.True(result.IsPartial);
        Assert.True(result.Items[0].IsSpecial);
        Assert.All(result.Items, e => Assert.Null(e.Unit));
        Assert.Equal("av:tvmaze:episode:1", AudiovisualIdentityKeys.CreateUnitKey(AudiovisualContentForm.Series,
            null, verifiedEpisodeId: result.Items[0].Id));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[{\"id\":1,\"season\":1,\"number\":0}]")]
    [InlineData("[{\"id\":1,\"name\":\"A\"},{\"id\":1,\"name\":\"B\"}]")]
    public async Task MalformedEpisodePayloadDoesNotBecomeEmptySuccess(string body)
    {
        using var http = new HttpClient(new Handler(_ => Json(body)));
        var result = await new TvmazeMetadataClient(new(http)).GetUnitsAsync(Show);
        Assert.Empty(result.Items);
        Assert.Equal(ProviderOutcomeStatus.InvalidResponse, result.Outcomes[0].Status);
    }

    [Fact]
    public async Task InvalidOrUnsupportedIdentityAndCancellationDoNotFetch()
    {
        var handler = new Handler(_ => Json("[]"));
        using var http = new HttpClient(handler);
        var client = new TvmazeMetadataClient(new(http));
        Assert.Equal(ProviderOutcomeStatus.Unsupported, (await client.GetUnitsAsync(Show with { ContentForm = AudiovisualContentForm.Feature })).Outcomes[0].Status);
        Assert.Equal(ProviderOutcomeStatus.InvalidResponse, (await client.GetUnitsAsync(Show with { ExternalIds = [new("tvmaze", "show", "42")] })).Outcomes[0].Status);
        Assert.Equal(ProviderOutcomeStatus.InvalidResponse, (await client.GetUnitsAsync(Show with { PrimaryId = new("tvmaze", "show", "../42") })).Outcomes[0].Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetUnitsAsync(Show, token: new CancellationToken(true)));
        Assert.Equal(0, handler.Calls);
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    [Fact]
    public async Task RouterAndTvServiceForwardSavedTvmazeEpisodeRequests()
    {
        var handler = new Handler(_ => Json("[]"));
        using var http = new HttpClient(handler);
        var requests = new ProviderRequestCoordinator(http);
        var router = new AudiovisualCatalogMetadataRouter(null, requests, new TvmazeMetadataClient(requests));
        Assert.Equal(ProviderOutcomeStatus.Success, (await router.GetUnitsAsync(Show)).Outcomes[0].Status);
        IAudiovisualEpisodeMetadataClient service = new TvService(httpClient: http);
        Assert.Equal(ProviderOutcomeStatus.Success,
            (await service.GetUnitsAsync(Show with { Kind = AudiovisualMediaKind.Television })).Outcomes[0].Status);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetUnitsAsync(Show with { Kind = AudiovisualMediaKind.Movie }));
        Assert.Equal(2, handler.Calls);
    }

    private sealed class Handler(Func<Uri, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; LastUri = request.RequestUri; return Task.FromResult(response(LastUri!)); }
    }
}
