using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class TvmazeProductionRoutingTests
{
    private const string Show = """{"id":526,"name":"The Office","type":"Scripted","premiered":"2005-03-24","summary":"<p>An <b>office</b> &amp; its people.</p>","rating":{"average":8.5},"image":{"medium":"https://static.tvmaze.com/poster.jpg"},"externals":{"imdb":"tt0386676"}}""";
    private const string Episodes = """[{"id":1,"name":"Pilot","type":"regular","season":1,"number":1}]""";

    [Theory]
    [InlineData("", false)]
    [InlineData("old-saved-key", false)]
    [InlineData("", true)]
    [InlineData("old-saved-key", true)]
    public async Task TelevisionUsesKeylessProviderRegardlessOfLegacySettings(string key, bool archive)
    {
        using var config = new Config(key, archive);
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var service = new TvService(config.Value, http);
        var search = await service.GetPageAsync(new(AudiovisualMediaKind.Television, AudiovisualCatalogMode.Search, "Office"));
        var browse = await service.GetPageAsync(new(AudiovisualMediaKind.Television));
        var item = Assert.Single(search.Items);
        Assert.Equal(item.Identity, Assert.Single(browse.Items).Identity);
        Assert.Equal(new("tvmaze", "show", "526"), item.Identity.PrimaryId);
        Assert.Null(item.Identity.TmdbId);
        Assert.Equal("An office & its people.", item.Overview);
        Assert.Equal(8.5, item.Rating);
        Assert.Equal("https://www.tvmaze.com/shows/526", new AudiovisualCardViewModel(item).MetadataAttributionUrl);
        var units = await service.GetUnitsAsync(item.Identity);
        Assert.Equal(1, Assert.Single(units.Items).Unit!.EpisodeNumber);
        Assert.Equal(3, handler.Calls);
        Assert.False(service.Capabilities.HasFlag(AudiovisualCatalogCapabilities.Locales));
        Assert.True(service.Capabilities.HasFlag(AudiovisualCatalogCapabilities.Episodes));
    }

    [Fact]
    public async Task DefaultConstructorAlsoUsesTvmazeAndReusesIndexPageForContinuation()
    {
        var handler = new Handler { Index = "[" + Show + "," + Show.Replace("526", "1292") + "]" };
        using var http = new HttpClient(handler);
        var service = new TvService(httpClient: http);
        var request = new AudiovisualCatalogRequest(AudiovisualMediaKind.Television, PageSize: 1);
        var first = await service.GetPageAsync(request);
        var next = await service.GetPageAsync(request with { ContinuationToken = first.NextToken });
        Assert.Equal("526", Assert.Single(first.Items).Identity.PrimaryId!.Value);
        Assert.Equal("1292", Assert.Single(next.Items).Identity.PrimaryId!.Value);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SavedTvmazeCartoonIdentityOwnsEpisodeLookupAfterSettingsChange()
    {
        using var config = new Config("old-key", true);
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var service = new CartoonService(config.Value, http);
        var units = await service.GetUnitsAsync(new() { Kind = AudiovisualMediaKind.Cartoon,
            ContentForm = AudiovisualContentForm.Series, PrimaryId = new("tvmaze", "show", "526"), Title = "Saved series" });
        Assert.Single(units.Items);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SavedLegacySeriesNeedsExplicitMappingInsteadOfTitleGuessing()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var service = new TvService(httpClient: http);
        var result = await service.GetUnitsAsync(new() { Kind = AudiovisualMediaKind.Television,
            ContentForm = AudiovisualContentForm.Series, TmdbId = 526, Title = "The Office" });
        Assert.Equal(ProviderOutcomeStatus.Unsupported, Assert.Single(result.Outcomes).Status);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderOutcomeStatus.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderOutcomeStatus.Unavailable)]
    public async Task KeylessFailureDoesNotFallThroughToSavedTmdbKey(HttpStatusCode status, ProviderOutcomeStatus expected)
    {
        using var config = new Config("old-key", true);
        var handler = new Handler { Status = status };
        using var http = new HttpClient(handler);
        var page = await new TvService(config.Value, http).GetPageAsync(new(AudiovisualMediaKind.Television));
        Assert.Empty(page.Items);
        Assert.Equal(expected, Assert.Single(page.Outcomes).Status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MissingPresentationFieldsRemainUsableWithoutInventedValues()
    {
        var handler = new Handler { Index = """[{"id":1,"name":"Unknown show","rating":{"average":null}}]""" };
        using var http = new HttpClient(handler);
        var item = Assert.Single(await new TvService(httpClient: http).GetPopularAsync());
        Assert.Empty(item.Overview);
        Assert.Empty(item.PosterUrl);
        Assert.Equal(0, item.Rating);
        Assert.Empty(item.OriginalLanguage);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Index { get; init; } = "[" + Show + "]";
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("api.tvmaze.com", request.RequestUri.Host);
            Assert.Null(request.Headers.Authorization);
            Assert.DoesNotContain("api_key", request.RequestUri.Query);
            string body = request.RequestUri.AbsolutePath.EndsWith("/episodes", StringComparison.Ordinal) ? Episodes
                : request.RequestUri.AbsolutePath == "/search/shows" ? "[{\"show\":" + Show + "}]" : Index;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body) });
        }
    }

    private sealed class Config : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.TvmazeRouting", Guid.NewGuid().ToString("N"));
        public DomainHotSwapper Value { get; }
        public Config(string key, bool archive)
        {
            Directory.CreateDirectory(_root);
            string path = Path.Combine(_root, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string>
            { [AudiovisualOptions.TmdbApiKeyConfigKey] = key, [AudiovisualOptions.InternetArchiveEnabledConfigKey] = archive.ToString() }));
            Value = new(path);
        }
        public void Dispose()
        {
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.TvmazeRouting")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true);
        }
    }
}
