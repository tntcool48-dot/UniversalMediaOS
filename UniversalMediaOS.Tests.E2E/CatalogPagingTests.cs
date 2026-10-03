using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class CatalogPagingTests
{
    private static AudiovisualOptions Options => new() { TmdbApiKey = "fixture-secret", TmdbBaseUri = new("https://tmdb.example/3/") };
    private const string Mixed = """{"page":1,"total_pages":2,"results":[{"id":42,"media_type":"movie","title":"Animated film","genre_ids":[16]},{"id":42,"media_type":"tv","name":"Animated series","genre_ids":[16]}]}""";

    [Fact]
    public async Task TmdbSlicesUpstreamPageWithoutLosingSameNumberMovieAndSeries()
    {
        var handler = new Handler(uri => uri.Query.Contains("page=2")
            ? Json("""{"page":2,"total_pages":2,"results":[{"id":43,"media_type":"movie","title":"Next film","genre_ids":[16]}]}""") : Json(Mixed));
        using var http = new HttpClient(handler);
        var client = new TmdbMetadataClient(Options, new(http));
        var request = new AudiovisualCatalogRequest(AudiovisualMediaKind.Cartoon, PageSize: 1);
        var first = await client.GetPageAsync(request);
        var second = await client.GetPageAsync(request with { ContinuationToken = first.NextToken });
        var third = await client.GetPageAsync(request with { ContinuationToken = second.NextToken });
        Assert.Equal("movie", Assert.Single(first.Items).Identity.PrimaryId!.Namespace);
        Assert.Equal("tv", Assert.Single(second.Items).Identity.PrimaryId!.Namespace);
        Assert.Equal("Next film", Assert.Single(third.Items).Title);
        Assert.Null(third.NextToken);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(ProviderOutcomeStatus.Success, Assert.Single(second.Outcomes).Status);
    }

    [Fact]
    public async Task TokensRejectChangedSearchLocaleKindSizeCredentialsAndTamperingBeforeTransport()
    {
        var handler = new Handler(_ => Json(Mixed));
        using var http = new HttpClient(handler);
        var requests = new ProviderRequestCoordinator(http);
        var client = new TmdbMetadataClient(Options, requests);
        var original = new AudiovisualCatalogRequest(AudiovisualMediaKind.Cartoon, AudiovisualCatalogMode.Search, "private query", 1, Locale: "en-US");
        var first = await client.GetPageAsync(original);
        var next = original with { ContinuationToken = first.NextToken };
        foreach (var changed in new[] { next with { Query = "different" }, next with { Locale = "ar" }, next with { Kind = AudiovisualMediaKind.Movie }, next with { PageSize = 2 }, next with { Mode = AudiovisualCatalogMode.Discover }, next with { ContinuationToken = next.ContinuationToken + "x" } })
            await Assert.ThrowsAsync<ArgumentException>(() => client.GetPageAsync(changed));
        await Assert.ThrowsAsync<ArgumentException>(() => new TmdbMetadataClient(Options with { TmdbApiKey = "different-key" }, requests).GetPageAsync(next));
        string tokenPayload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(first.NextToken!.Split('.')[0]));
        Assert.DoesNotContain("private query", tokenPayload);
        Assert.DoesNotContain("fixture-secret", tokenPayload);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ArchiveFilteredPageRetainsContinuationAndProviderItemId()
    {
        var handler = new Handler(uri => uri.Query.Contains("page=2")
            ? Json("""{"response":{"numFound":51,"docs":[{"identifier":"CaseSensitive-ID","title":"Cartoon","subject":["Animation"],"licenseurl":"https://creativecommons.org/licenses/by/4.0/"}]}}""")
            : Json("""{"response":{"numFound":51,"docs":[{"identifier":"unlicensed","title":"Filtered"}]}}"""));
        using var http = new HttpClient(handler);
        var client = new InternetArchiveMetadataClient(new() { InternetArchiveBaseUri = new("https://archive.example/") }, new(http));
        var request = new AudiovisualCatalogRequest(AudiovisualMediaKind.Cartoon);
        var first = await client.GetPageAsync(request);
        Assert.Empty(first.Items);
        Assert.NotNull(first.NextToken);
        Assert.Equal(ProviderOutcomeStatus.Success, first.Outcomes[0].Status);
        var second = await client.GetPageAsync(request with { ContinuationToken = first.NextToken });
        Assert.Equal(new("internetarchive", "item", "CaseSensitive-ID"), Assert.Single(second.Items).Identity.PrimaryId);
        Assert.Null(second.NextToken);
    }

    [Theory]
    [InlineData("{\"results\":[]}", ProviderOutcomeStatus.Success)]
    [InlineData("{\"error\":\"unavailable\"}", ProviderOutcomeStatus.InvalidResponse)]
    [InlineData("not json", ProviderOutcomeStatus.InvalidResponse)]
    public async Task EmptySuccessAndMalformedPayloadAreDistinct(string payload, ProviderOutcomeStatus status)
    {
        using var http = new HttpClient(new Handler(_ => Json(payload)));
        var result = await new TmdbMetadataClient(Options, new(http)).GetPageAsync(new(AudiovisualMediaKind.Movie));
        Assert.Empty(result.Items);
        Assert.Equal(status, result.Outcomes[0].Status);
    }

    [Fact]
    public async Task FailedLaterPageLeavesEarlierPageIntactAndReportsCredentialsFailure()
    {
        using var http = new HttpClient(new Handler(uri => uri.Query.Contains("page=2") ? new(HttpStatusCode.Unauthorized) : Json(Mixed)));
        var client = new TmdbMetadataClient(Options, new(http));
        var request = new AudiovisualCatalogRequest(AudiovisualMediaKind.Cartoon);
        var first = await client.GetPageAsync(request);
        var failed = await client.GetPageAsync(request with { ContinuationToken = first.NextToken });
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(ProviderOutcomeStatus.InvalidCredentials, failed.Outcomes[0].Status);
    }

    [Fact]
    public async Task PublicCatalogRoutesPagesAndRejectsContinuationAfterProviderChange()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new DomainHotSwapper(Path.Combine(root, "config.json"));
            config.SetSetting("TmdbApiKey", "fixture-secret");
            config.SetSetting("TmdbApiUrl", "https://tmdb.example/3/");
            var handler = new Handler(_ => Json(Mixed));
            using var http = new HttpClient(handler);
            IPagedAudiovisualCatalogService service = new CartoonService(config, http);
            var request = new AudiovisualCatalogRequest(AudiovisualMediaKind.Cartoon);
            var first = await service.GetPageAsync(request);
            Assert.True(service.Capabilities.HasFlag(AudiovisualCatalogCapabilities.Continuation));
            await Assert.ThrowsAsync<ArgumentException>(() => service.GetPageAsync(request with { Kind = AudiovisualMediaKind.Movie }));
            config.SetSetting("TmdbApiKey", "");
            config.SetSetting("OtherMediaEnableInternetArchive", "true");
            await Assert.ThrowsAsync<ArgumentException>(() => service.GetPageAsync(request with { ContinuationToken = first.NextToken }));
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            string target = Path.GetFullPath(root);
            if (target.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(target, true);
        }
    }

    [Fact]
    public async Task UnsupportedAndMissingProvidersAreNotSuccessfulEmptyPages()
    {
        var request = new AudiovisualCatalogRequest(AudiovisualMediaKind.Movie);
        Assert.Equal(ProviderOutcomeStatus.NotConfigured, (await EmptyAudiovisualMetadataClient.Instance.GetPageAsync(request)).Outcomes[0].Status);
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException()));
        Assert.Equal(ProviderOutcomeStatus.Unsupported, (await new ImdbMetadataClient(http).GetPageAsync(request)).Outcomes[0].Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TmdbMetadataClient(Options, new(http)).GetPageAsync(request, cancellation.Token));
    }

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK) { Content = new StringContent(payload) };
    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
}
