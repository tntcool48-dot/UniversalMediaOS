using System.IO;
using System.Net;
using System.Net.Http;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class TvmazeQualificationTests
{
    [Fact]
    public async Task RecordedSearchKeepsRemakesAndPagesWithoutRepeatedRequests()
    {
        var handler = new Handler(Fixture("tvmaze-office"));
        using var http = new HttpClient(handler);
        var client = new TvmazeMetadataClient(new(http));
        var request = new AudiovisualCatalogRequest(AudiovisualMediaKind.Television, AudiovisualCatalogMode.Search, "the office", 1);
        var first = await client.GetPageAsync(request);
        var second = await client.GetPageAsync(request with { ContinuationToken = first.NextToken });
        Assert.Equal("526", Assert.Single(first.Items).Identity.PrimaryId!.Value);
        Assert.Equal(2005, first.Items[0].Identity.Year);
        Assert.Equal("1292", Assert.Single(second.Items).Identity.PrimaryId!.Value);
        Assert.Equal(2001, second.Items[0].Identity.Year);
        Assert.All(first.Items.Concat(second.Items), item =>
        {
            Assert.Equal(AudiovisualContentForm.Series, item.Identity.ContentForm);
            Assert.Null(item.Identity.TmdbId);
            Assert.Empty(item.OriginalLanguage);
        });
        int count = 2;
        while (second.NextToken != null)
        {
            second = await client.GetPageAsync(request with { ContinuationToken = second.NextToken });
            count += second.Items.Count;
            Assert.True(count <= 10);
        }
        Assert.Equal(10, count);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("api.tvmaze.com", handler.LastUri!.Host);
        Assert.Equal("?q=the%20office", handler.LastUri.Query);
    }

    [Fact]
    public async Task CartoonSearchUsesObservedAnimationType()
    {
        using var http = new HttpClient(new Handler(Fixture("tvmaze-avatar")));
        var page = await new TvmazeMetadataClient(new(http)).GetPageAsync(
            new(AudiovisualMediaKind.Cartoon, AudiovisualCatalogMode.Search, "avatar"));
        Assert.NotEmpty(page.Items);
        Assert.Contains(page.Items, item => item.Title == "Avatar: The Last Airbender");
        Assert.All(page.Items, item => Assert.True(item.Identity.IsAnimated));
        Assert.DoesNotContain(page.Items, item => item.Identity.PrimaryId!.Value == "38852");
    }

    [Fact]
    public async Task UnsupportedRequestsAndCancellationDoNotStartTransport()
    {
        var handler = new Handler("[]");
        using var http = new HttpClient(handler);
        var client = new TvmazeMetadataClient(new(http));
        foreach (var request in new[] {
            new AudiovisualCatalogRequest(AudiovisualMediaKind.Movie, AudiovisualCatalogMode.Search, "matrix"),
            new AudiovisualCatalogRequest(AudiovisualMediaKind.Television, AudiovisualCatalogMode.Search, "office", Locale: "ar") })
            Assert.Equal(ProviderOutcomeStatus.Unsupported, (await client.GetPageAsync(request)).Outcomes[0].Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetPageAsync(
            new(AudiovisualMediaKind.Television), new CancellationToken(true)));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("[]", ProviderOutcomeStatus.Success)]
    [InlineData("{}", ProviderOutcomeStatus.InvalidResponse)]
    [InlineData("[{\"show\":{\"id\":0,\"name\":\"bad\"}}]", ProviderOutcomeStatus.InvalidResponse)]
    [InlineData("[{\"show\":null}]", ProviderOutcomeStatus.InvalidResponse)]
    public async Task EmptyAndMalformedResultsAreDistinct(string body, ProviderOutcomeStatus expected)
    {
        using var http = new HttpClient(new Handler(body));
        var page = await new TvmazeMetadataClient(new(http)).GetPageAsync(
            new(AudiovisualMediaKind.Television, AudiovisualCatalogMode.Search, "office"));
        Assert.Empty(page.Items);
        Assert.Equal(expected, page.Outcomes[0].Status);
    }

    [Fact]
    public async Task RateLimitAndChangedQueryAreNotSilentlyEmptySuccess()
    {
        using var failedHttp = new HttpClient(new Handler("[]", HttpStatusCode.TooManyRequests));
        var failed = await new TvmazeMetadataClient(new(failedHttp)).GetPageAsync(
            new(AudiovisualMediaKind.Television, AudiovisualCatalogMode.Search, "office"));
        Assert.Equal(ProviderOutcomeStatus.RateLimited, failed.Outcomes[0].Status);
        using var http = new HttpClient(new Handler(Fixture("tvmaze-office")));
        var client = new TvmazeMetadataClient(new(http));
        var request = new AudiovisualCatalogRequest(AudiovisualMediaKind.Television, AudiovisualCatalogMode.Search, "office", 1);
        var first = await client.GetPageAsync(request);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetPageAsync(request with
        { Query = "avatar", ContinuationToken = first.NextToken }));
    }

    private static string Fixture(string name)
    {
        using var stream = typeof(TvmazeQualificationTests).Assembly.GetManifestResourceStream(
            $"UniversalMediaOS.Tests.E2E.Fixtures.Metadata.{name}.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
