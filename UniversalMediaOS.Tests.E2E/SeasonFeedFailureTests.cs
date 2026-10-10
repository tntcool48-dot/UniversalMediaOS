using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Routing;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class SeasonFeedFailureTests
{
    private const string Empty = "<rss version='2.0'><channel><title>Empty</title></channel></rss>";
    private const string Match = "<rss version='2.0'><channel><item><title>Catalog batch</title><link>magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567</link></item></channel></rss>";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeasonFailureDistinguishesIncompleteDiscoveryFromSuccessfulEmptyFeeds(bool unavailable)
    {
        using var client = new HttpClient(new FeedHandler(unavailable));
        using var fixture = new SeasonFixture(client);
        var messages = new ConcurrentBag<string>();
        Assert.False(await fixture.Downloader.DownloadSeasonAsync("Catalog title", messages.Add));
        if (unavailable)
        {
            Assert.Contains(messages, message => message.Contains("discovery is incomplete", StringComparison.Ordinal));
            Assert.DoesNotContain(messages, message => message.Contains("No torrents found", StringComparison.Ordinal));
        }
        else Assert.Contains(messages, message => message.Contains("No torrents found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASeasonMatchFromALaterQuerySurvivesOtherIncompleteQueries()
    {
        using var client = new HttpClient(new FeedHandler(unavailable: true, laterMatch: true));
        using var fixture = new SeasonFixture(client);
        var result = await fixture.Downloader.SearchForBatchTorrentsAsync("Catalog title", _ => { });
        Assert.Equal("Catalog batch", Assert.Single(result).Title);
    }

    private sealed class FeedHandler(bool unavailable, bool laterMatch = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool found = laterMatch && request.RequestUri!.Host == "first.test" &&
                Uri.UnescapeDataString(request.RequestUri.Query).Contains(" Complete", StringComparison.Ordinal);
            bool failed = unavailable && !found && (laterMatch || request.RequestUri!.Host == "second.test");
            return Task.FromResult(new HttpResponseMessage(failed ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(found ? Match : Empty) });
        }
    }

    private sealed class SeasonFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", "SeasonFeeds-" + Guid.NewGuid().ToString("N"));
        public SeasonDownloader Downloader { get; }
        public SeasonFixture(HttpClient client)
        {
            Directory.CreateDirectory(_root);
            string config = Path.Combine(_root, "config.json");
            File.WriteAllText(config, JsonSerializer.Serialize(new Dictionary<string, string>
                { ["DownloadDirectory"] = Path.Combine(_root, "downloads") }));
            Downloader = new SeasonDownloader(new DomainHotSwapper(config), null,
                new DualTrackerRssParser(client, "https://first.test/?q=", "https://second.test/?q="));
        }
        public void Dispose()
        {
            Downloader.Dispose();
            Assert.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests")) + Path.DirectorySeparatorChar,
                Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
            Assert.Null(new DirectoryInfo(_root).LinkTarget);
            Directory.Delete(_root, true);
        }
    }
}
