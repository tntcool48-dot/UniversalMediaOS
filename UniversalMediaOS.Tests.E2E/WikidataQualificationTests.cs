using System.IO;
using System.Net;
using System.Net.Http;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class WikidataQualificationTests
{
    [Fact]
    public async Task RecordedTypedDuneSearchKeepsBothRemakes()
    {
        var handler = new Handler(_ => Fixture("dune"));
        using var http = new HttpClient(handler);
        var page = await new WikidataMetadataClient(new(http)).GetPageAsync(
            new(AudiovisualMediaKind.Movie, AudiovisualCatalogMode.Search, "Dune"));
        Assert.Contains(page.Items, i => i.Identity.ImdbId == "tt0087182" && i.Identity.Year == 1984);
        Assert.Contains(page.Items, i => i.Identity.ImdbId == "tt1160419" && i.Identity.Year == 2021);
        Assert.All(page.Items, i => Assert.Equal(AudiovisualContentForm.Feature, i.Identity.ContentForm));
        Assert.Equal(2, handler.Uris.Count);
        Assert.Contains("haswbstatement:P31=Q11424|P31=Q202866", Uri.UnescapeDataString(handler.Uris[0].Query));
    }

    [Fact]
    public async Task RecordedBrowseUsesRealContinuationAndBatchEnrichment()
    {
        var handler = new Handler(uri => Fixture(Uri.UnescapeDataString(uri.Query).Contains("sroffset=5") ? "films-page-2" : "films-page-1"));
        using var http = new HttpClient(handler);
        var client = new WikidataMetadataClient(new(http));
        var request = new AudiovisualCatalogRequest(AudiovisualMediaKind.Movie);
        var first = await client.GetPageAsync(request);
        Assert.NotEmpty(first.Items);
        Assert.NotNull(first.NextToken);
        var second = await client.GetPageAsync(request with { ContinuationToken = first.NextToken });
        Assert.NotEmpty(second.Items);
        Assert.Empty(first.Items.Select(i => i.Identity.PrimaryId).Intersect(second.Items.Select(i => i.Identity.PrimaryId)));
        Assert.Equal(4, handler.Uris.Count);
        Assert.All(handler.Uris.Where(u => u.Query.Contains("srlimit")), u => Assert.Contains("srlimit=5", u.Query));
    }

    [Fact]
    public async Task SharedLanguageLabelRepairsToyStoryAndAnimationComesFromClaims()
    {
        using var http = new HttpClient(new Handler(_ => Fixture("toy")));
        var page = await new WikidataMetadataClient(new(http)).GetPageAsync(
            new(AudiovisualMediaKind.Cartoon, AudiovisualCatalogMode.Search, "Toy Story"));
        Assert.Contains(page.Items, i => i.Identity.PrimaryId!.Value == "Q171048" && i.Title == "Toy Story");
        Assert.All(page.Items, i =>
        {
            Assert.True(i.Identity.IsAnimated);
            Assert.Null(i.Identity.TmdbId);
            Assert.Empty(i.OriginalLanguage);
        });
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":\"maxlag\"}}", ProviderOutcomeStatus.RateLimited)]
    [InlineData("{\"query\":{\"search\":[]}}", ProviderOutcomeStatus.Success)]
    [InlineData("{\"query\":{\"search\":[{\"title\":\"not-an-id\"}]}}", ProviderOutcomeStatus.InvalidResponse)]
    [InlineData("{\"query\":{\"search\":[]},\"continue\":{\"sroffset\":0}}", ProviderOutcomeStatus.InvalidResponse)]
    public async Task ApiErrorsEmptyResultsAndBrokenCursorsStayDistinct(string search, ProviderOutcomeStatus expected)
    {
        using var http = new HttpClient(new Handler(_ => (search, "{}")));
        var result = await new WikidataMetadataClient(new(http)).GetPageAsync(new(AudiovisualMediaKind.Movie));
        Assert.Empty(result.Items);
        Assert.Equal(expected, result.Outcomes[^1].Status);
    }

    [Fact]
    public async Task EntityFailureDoesNotAdvancePageAndCancellationStopsBeforeTransport()
    {
        var handler = new Handler(_ => (Fixture("dune").Search, "{\"error\":{\"code\":\"maxlag\"}}"));
        using var http = new HttpClient(handler);
        var client = new WikidataMetadataClient(new(http));
        var page = await client.GetPageAsync(new(AudiovisualMediaKind.Movie));
        Assert.True(page.IsPartial);
        Assert.Null(page.NextToken);
        Assert.Equal(ProviderOutcomeStatus.RateLimited, page.Outcomes[^1].Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetPageAsync(
            new(AudiovisualMediaKind.Movie), new CancellationToken(true)));
        Assert.Equal(2, handler.Uris.Count);
    }

    [Fact]
    public async Task TokensRejectChangedQueryAndUnsupportedScopeDoesNotFetch()
    {
        var handler = new Handler(_ => Fixture("dune"));
        using var http = new HttpClient(handler);
        var client = new WikidataMetadataClient(new(http));
        var request = new AudiovisualCatalogRequest(AudiovisualMediaKind.Movie, AudiovisualCatalogMode.Search, "Dune");
        var page = await client.GetPageAsync(request);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetPageAsync(request with { Query = "Matrix", ContinuationToken = page.NextToken }));
        Assert.Equal(ProviderOutcomeStatus.Unsupported, (await client.GetPageAsync(new(AudiovisualMediaKind.Television))).Outcomes[0].Status);
        Assert.Equal(2, handler.Uris.Count);
    }

    private static (string Search, string Entities) Fixture(string name) => (Read(name, "search"), Read(name, "entities"));

    [Fact]
    public async Task NonFilmAndDeprecatedClaimsCannotEstablishFilmOrArtwork()
    {
        const string search = """{"query":{"search":[{"title":"Q1"},{"title":"Q2"}]},"continue":{"sroffset":2}}""";
        const string entities = """
        {"entities":{
          "Q1":{"id":"Q1","labels":{"en":{"value":"Game"}},"claims":{"P31":[
            {"rank":"deprecated","mainsnak":{"snaktype":"value","datavalue":{"value":{"id":"Q11424"}}}}]}},
          "Q2":{"id":"Q2","labels":{"en":{"value":"Film"}},"claims":{"P31":[
            {"rank":"normal","mainsnak":{"snaktype":"value","datavalue":{"value":{"id":"Q11424"}}}}],
            "P3383":[{"rank":"deprecated","mainsnak":{"snaktype":"value","datavalue":{"value":"Wrong poster.jpg"}}}]}}
        }}
        """;
        using var http = new HttpClient(new Handler(_ => (search, entities)));
        var page = await new WikidataMetadataClient(new(http)).GetPageAsync(new(AudiovisualMediaKind.Movie));
        Assert.Equal("Film", Assert.Single(page.Items).Title);
        Assert.Empty(page.Items[0].PosterUrl);
        Assert.True(page.IsPartial);
        Assert.NotNull(page.NextToken);
    }

    private static string Read(string name, string part)
    {
        using var stream = typeof(WikidataQualificationTests).Assembly.GetManifestResourceStream(
            $"UniversalMediaOS.Tests.E2E.Fixtures.Metadata.wikidata-{name}-{part}.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class Handler(Func<Uri, (string Search, string Entities)> responses) : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];
        private string _entities = "{}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Uris.Add(uri);
            string body;
            if (uri.Query.Contains("wbgetentities")) body = _entities;
            else { var response = responses(uri); body = response.Search; _entities = response.Entities; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
