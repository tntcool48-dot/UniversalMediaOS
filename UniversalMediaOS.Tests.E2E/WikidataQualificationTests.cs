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
        Assert.Contains("haswbstatement:P31=Q11424", Uri.UnescapeDataString(handler.Uris[0].Query));
        Assert.Contains("P31=Q202866", Uri.UnescapeDataString(handler.Uris[0].Query));
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
    [InlineData(AudiovisualMediaKind.Movie)]
    [InlineData(AudiovisualMediaKind.Cartoon)]
    public async Task RecordedYourNameFindsTheExactAnimeFilm(AudiovisualMediaKind kind)
    {
        var handler = new Handler(_ => Fixture("your-name"));
        using var http = new HttpClient(handler);
        var page = await new WikidataMetadataClient(new(http)).GetPageAsync(
            new(kind, AudiovisualCatalogMode.Search, "Your Name"));
        var film = Assert.Single(page.Items, i => i.Identity.PrimaryId!.Value == "Q21697406");
        Assert.Equal("Your Name", film.Title);
        Assert.Equal(2016, film.Identity.Year);
        Assert.Equal("tt5311514", film.Identity.ImdbId);
        Assert.True(film.Identity.IsAnimated);
        Assert.Equal(kind, film.Identity.Kind);
        Assert.Null(film.Identity.TmdbId);
        Assert.Empty(film.OriginalLanguage);
        Assert.Equal(2, handler.Uris.Count);
        Assert.Contains("P31=Q20650540", Uri.UnescapeDataString(handler.Uris[0].Query));
        Assert.Contains("srlimit=5", handler.Uris[0].Query);
        if (kind == AudiovisualMediaKind.Cartoon) Assert.Single(page.Items);
        else Assert.Equal(5, page.Items.Count);
    }

    [Theory]
    [InlineData(AudiovisualMediaKind.Movie)]
    [InlineData(AudiovisualMediaKind.Cartoon)]
    public async Task RecordedAnimatedFeaturesRetainIdsYearsAndAnimation(AudiovisualMediaKind kind)
    {
        var handler = new Handler(_ => Fixture("animated-features"));
        using var http = new HttpClient(handler);
        var page = await new WikidataMetadataClient(new(http)).GetPageAsync(new(kind));
        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, i => i.Identity.PrimaryId!.Value == "Q1051023" &&
            i.Identity.ImdbId == "tt1142977" && i.Identity.Year == 2012);
        Assert.Contains(page.Items, i => i.Identity.PrimaryId!.Value == "Q16246692" &&
            i.Identity.ImdbId == "tt3183630" && i.Identity.Year == 2013);
        Assert.All(page.Items, i =>
        {
            Assert.True(i.Identity.IsAnimated);
            Assert.Empty(i.OriginalLanguage);
        });
        Assert.Contains("P31=Q29168811", Uri.UnescapeDataString(handler.Uris[0].Query));
    }

    [Theory]
    [InlineData(AudiovisualMediaKind.Movie)]
    [InlineData(AudiovisualMediaKind.Cartoon)]
    public async Task RecordedFeatureFilmsDoNotInventAnimationOrLanguage(AudiovisualMediaKind kind)
    {
        var handler = new Handler(_ => Fixture("feature-films"));
        using var http = new HttpClient(handler);
        var page = await new WikidataMetadataClient(new(http)).GetPageAsync(new(kind));
        if (kind == AudiovisualMediaKind.Cartoon)
        {
            Assert.Empty(page.Items);
            Assert.DoesNotContain("P31=Q24869", Uri.UnescapeDataString(handler.Uris[0].Query));
            return;
        }
        Assert.Equal(5, page.Items.Count);
        Assert.Contains(page.Items, i => i.Identity.PrimaryId!.Value == "Q137395955" && i.Title == "Froggie");
        Assert.All(page.Items, i =>
        {
            Assert.Null(i.Identity.IsAnimated);
            Assert.Empty(i.OriginalLanguage);
            Assert.Equal(AudiovisualContentForm.Feature, i.Identity.ContentForm);
        });
        Assert.Contains("P31=Q24869", Uri.UnescapeDataString(handler.Uris[0].Query));
    }

    [Theory]
    [InlineData("Q7725634", "normal")]
    [InlineData("Q134556", "normal")]
    [InlineData("Q24869", "deprecated")]
    [InlineData("Q20650540", "deprecated")]
    [InlineData("Q29168811", "deprecated")]
    public async Task SameTitleAndImdbCannotReplaceQualifiedFilmClaims(string type, string rank)
    {
        const string search = """{"query":{"search":[{"title":"Q1"}]}}""";
        string entities = """
        {"entities":{"Q1":{"id":"Q1","labels":{"en":{"value":"Your Name"}},"claims":{
          "P31":[{"rank":"$rank","mainsnak":{"snaktype":"value","datavalue":{"value":{"id":"$type"}}}}],
          "P345":[{"rank":"normal","mainsnak":{"snaktype":"value","datavalue":{"value":"tt5311514"}}}]
        }}}}
        """.Replace("$rank", rank).Replace("$type", type);
        using var http = new HttpClient(new Handler(_ => (search, entities)));
        var page = await new WikidataMetadataClient(new(http)).GetPageAsync(new(AudiovisualMediaKind.Movie));
        Assert.Empty(page.Items);
        Assert.True(page.IsPartial);
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

    [Theory]
    [InlineData(AudiovisualMediaKind.Movie)]
    [InlineData(AudiovisualMediaKind.Cartoon)]
    public async Task RecordedAnimatedShortFindsExactFilmWithoutInventingSpokenLanguage(AudiovisualMediaKind kind)
    {
        var handler = new Handler(_ => Fixture("animated-short"));
        using var http = new HttpClient(handler);
        var page = await new WikidataMetadataClient(new(http)).GetPageAsync(
            new(kind, AudiovisualCatalogMode.Search, "Big Buck Bunny"));
        var film = Assert.Single(page.Items);
        Assert.Equal("Q282456", film.Identity.PrimaryId!.Value);
        Assert.Equal("Big Buck Bunny", film.Title);
        Assert.Equal(2008, film.Identity.Year);
        Assert.Equal("tt1254207", film.Identity.ImdbId);
        Assert.True(film.Identity.IsAnimated);
        Assert.Equal(kind, film.Identity.Kind);
        Assert.Null(film.Identity.TmdbId);
        Assert.Empty(film.OriginalLanguage);
        Assert.Contains("Big%20buck%20bunny%20poster%20big.jpg", film.PosterUrl);
        Assert.Equal(2, handler.Uris.Count);
        Assert.Contains("P31=Q17517379", Uri.UnescapeDataString(handler.Uris[0].Query));
        Assert.Null(page.NextToken);
    }

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
