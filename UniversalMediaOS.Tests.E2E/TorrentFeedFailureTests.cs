using System.Net;
using System.Net.Http;
using UniversalMediaOS.Core.Routing;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class TorrentFeedFailureTests
{
    private const string Empty = "<rss version='2.0'><channel><title>Empty</title></channel></rss>";
    private const string Match = "<rss version='2.0'><channel><title>Match</title><item><title>Catalog episode</title><link>magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567</link></item></channel></rss>";

    [Theory]
    [InlineData(200, 503)]
    [InlineData(503, 503)]
    public async Task EmptyResultsWithAFailedSourceAreIncomplete(int first, int second)
    {
        using var client = new HttpClient(new FeedHandler(first, second));
        var rss = new DualTrackerRssParser(client, "https://first.test/?q=", "https://second.test/?q=");
        await Assert.ThrowsAsync<TorrentSearchIncompleteException>(() => rss.SearchAsync("catalog"));
    }

    [Fact]
    public async Task BothSuccessfulEmptyFeedsCanReportNoResults()
    {
        using var client = new HttpClient(new FeedHandler(200, 200));
        var rss = new DualTrackerRssParser(client, "https://first.test/?q=", "https://second.test/?q=");
        Assert.Empty(await rss.SearchAsync("catalog"));
    }

    [Fact]
    public async Task AFallbackMatchSurvivesTheFirstFeedFailure()
    {
        using var client = new HttpClient(new FeedHandler(503, 200, Match));
        var rss = new DualTrackerRssParser(client, "https://first.test/?q=", "https://second.test/?q=");
        Assert.Equal("Catalog episode", Assert.Single(await rss.SearchAsync("catalog")).Title);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        using var client = new HttpClient(new FeedHandler(200, 200));
        var rss = new DualTrackerRssParser(client, "https://first.test/?q=", "https://second.test/?q=");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rss.SearchAsync("catalog", token: cancellation.Token));
    }

    private sealed class FeedHandler(int first, int second, string secondBody = Empty) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isFirst = request.RequestUri!.Host == "first.test";
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)(isFirst ? first : second))
            { Content = new StringContent(isFirst ? Empty : secondBody) });
        }
    }
}
