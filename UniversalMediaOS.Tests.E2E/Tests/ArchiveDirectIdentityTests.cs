using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class ArchiveDirectIdentityTests
{
    private const string ItemId = "Retained-Film";
    private const string OtherId = "Other-Film";
    private static readonly AudiovisualExternalId ArchiveId = new("internetarchive", "item", ItemId);

    [Theory]
    [InlineData(false, null, "Observed Film")]
    [InlineData(true, null, "Observed Film")]
    [InlineData(false, 2000, "Observed Film")]
    [InlineData(false, null, "")]
    public async Task PreservedArchiveIdResolvesItsOwnObservedMetadataWithoutTitleSearch(bool externalId, int? year, string savedTitle)
    {
        var handler = new ItemResponses(Metadata(ItemId, year));
        using var http = new HttpClient(handler);
        var identity = Identity(year) with
        {
            Title = savedTitle,
            PrimaryId = externalId ? null : ArchiveId,
            ExternalIds = externalId ? [ArchiveId] : []
        };
        var source = Assert.Single(await Provider(http).FindSourcesAsync(identity, AudiovisualUnit.Feature));

        Assert.Equal("https://archive.example/download/Retained-Film/Observed_Film.mp4", source.Location.AbsoluteUri);
        Assert.Equal(ArchiveId, source.Identity.PrimaryId);
        Assert.Equal("Observed Film", source.Identity.Title);
        Assert.Equal(year, source.Identity.Year);
        Assert.Equal(SourceEvidenceOrigin.ProviderItem, source.Evidence?.Origin);
        Assert.Equal(ArchiveId, source.Evidence?.Identity?.PrimaryId);
        Assert.Equal(year, source.Evidence?.Identity?.Year);
        Assert.Equal(SourceVerificationStatus.Verified, ExactAudiovisualMatcher.VerifyEvidence(
            new SourceSearchRequest { Identity = identity, Unit = AudiovisualUnit.Feature }, source.Evidence).Status);
        Assert.Empty(source.Evidence?.Audio?.Languages ?? []);
        Assert.Equal("/metadata/Retained-Film", Assert.Single(handler.Uris).AbsolutePath);
    }

    [Theory]
    [InlineData(OtherId)]
    [InlineData("retained-film")]
    [InlineData(null)]
    public async Task DirectItemRequiresItsIndependentlyReturnedCaseSensitiveIdentifier(string? returnedId)
    {
        var handler = new ItemResponses(Metadata(returnedId));
        using var http = new HttpClient(handler);

        Assert.Empty(await Provider(http).FindSourcesAsync(Identity(), AudiovisualUnit.Feature));
        Assert.Equal("/metadata/Retained-Film", Assert.Single(handler.Uris).AbsolutePath);
    }

    [Fact]
    public async Task ConflictingPreservedArchiveIdsCannotStartARequest()
    {
        var handler = new ItemResponses(Metadata(ItemId));
        using var http = new HttpClient(handler);
        var identity = Identity() with { ExternalIds = [new("internetarchive", "item", OtherId)] };

        Assert.Empty(await Provider(http).FindSourcesAsync(identity, AudiovisualUnit.Feature));
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public async Task MissingDirectItemMetadataCannotFallBackToTitleSearchOrRequestEvidence()
    {
        var handler = new ItemResponses("{\"files\":[{\"name\":\"Observed_Film.mp4\",\"format\":\"MPEG4\"}]}");
        using var http = new HttpClient(handler);

        Assert.Empty(await Provider(http).FindSourcesAsync(Identity(), AudiovisualUnit.Feature));
        Assert.Equal("/metadata/Retained-Film", Assert.Single(handler.Uris).AbsolutePath);
    }

    [Fact]
    public async Task CallerCancellationStopsTheDirectItemRequestWithoutSearchingAlternatives()
    {
        var started = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ItemResponses(Metadata(ItemId), async (uri, token) =>
        {
            started.TrySetResult(uri);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        using var caller = new CancellationTokenSource();
        Task<IReadOnlyList<AudiovisualSource>> pending = Provider(http).FindSourcesAsync(
            Identity(), AudiovisualUnit.Feature, token: caller.Token);
        Uri observed;
        try
        {
            observed = await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            caller.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
        });
        Assert.Equal("/metadata/Retained-Film", observed.AbsolutePath);
        Assert.Single(handler.Uris);
    }

    [Theory]
    [InlineData("Observed_Film_trailer.mp4")]
    [InlineData("Observed_Film_S01E01.mp4")]
    public async Task ASharedDirectItemIdCannotBypassFeatureFileValidation(string file)
    {
        var handler = new ItemResponses(Metadata(ItemId, file: file));
        using var http = new HttpClient(handler);

        Assert.Empty(await Provider(http).FindSourcesAsync(Identity(), AudiovisualUnit.Feature));
        Assert.Equal("/metadata/Retained-Film", Assert.Single(handler.Uris).AbsolutePath);
    }

    [Fact]
    public async Task ASharedDirectItemIdCannotOverrideTheObservedSeasonEpisode()
    {
        var handler = new ItemResponses(Metadata(ItemId, season: 2, episode: 4,
            file: "Observed_Film_S02E04.mp4"));
        using var http = new HttpClient(handler);
        var identity = Identity() with
        {
            Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series
        };

        Assert.Empty(await Provider(http).FindSourcesAsync(identity,
            new AudiovisualUnit { SeasonNumber = 2, EpisodeNumber = 3 }));
        Assert.Equal("/metadata/Retained-Film", Assert.Single(handler.Uris).AbsolutePath);
    }

    private static AudiovisualIdentity Identity(int? year = 2000) => new()
    {
        PrimaryId = ArchiveId, Kind = AudiovisualMediaKind.Movie,
        ContentForm = AudiovisualContentForm.Feature, Title = "Observed Film", Year = year
    };

    private static InternetArchiveSourceProvider Provider(HttpClient http) => new(new AudiovisualOptions
    {
        EnableInternetArchive = true, InternetArchiveBaseUri = new("https://archive.example/")
    }, new ProviderRequestCoordinator(http));

    private static string Metadata(string? identifier, int? year = 2000,
        int? season = null, int? episode = null, string file = "Observed_Film.mp4") =>
        JsonSerializer.Serialize(new
        {
            metadata = new { identifier, title = "Observed Film", year, season, episode, subject = new[] { "Feature films" } },
            files = new[] { new { name = file, format = "MPEG4", source = "original", size = "1000000" } }
        });

    private sealed class ItemResponses(string metadata,
        Func<Uri, CancellationToken, Task<HttpResponseMessage>>? pending = null) : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Uris.Add(uri);
            Assert.Equal("archive.example", uri.Host);
            if (pending != null) return pending(uri, token);
            string payload = uri.AbsolutePath switch
            {
                "/metadata/Retained-Film" => metadata,
                "/metadata/Other-Film" => Metadata(OtherId),
                "/advancedsearch.php" => "{\"response\":{\"docs\":[{\"identifier\":\"Other-Film\",\"title\":\"Observed Film\",\"year\":2000}]}}",
                _ => throw new InvalidOperationException("Unexpected Archive fixture request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
