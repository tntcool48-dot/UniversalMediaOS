using System.Net;
using System.Net.Http;
using System.Text;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

/// <summary>Wikidata records House on Haunted Hill (Q263583) as released 1958-12-17, 1959-01-14, 1959-02-17 and
/// 1959-10-27 (observed October 23, 2026). The catalog shows the earliest year, 1958, while Archive dates its
/// uploads 1959, so every upload was rejected. A year the same catalog entity records is accepted; nothing is
/// guessed with plus or minus one, and the 1999 remake stays apart.</summary>
public sealed class CatalogReleaseYearTests
{
    private static readonly AudiovisualIdentity HouseOnHauntedHill = new()
    {
        Kind = AudiovisualMediaKind.Movie,
        ContentForm = AudiovisualContentForm.Feature,
        PrimaryId = new("wikidata", "item", "Q263583"),
        Title = "House on Haunted Hill",
        Year = 1958,
        ReleaseYears = [1958, 1959],
        ImdbId = "tt0051744"
    };

    [Fact]
    public async Task CatalogRecordsEveryReleaseYearOfTheEntityAndShowsTheEarliest()
    {
        string search = """{"query":{"search":[{"title":"Q263583"}]}}""";
        string entities = """
            {"entities":{"Q263583":{"id":"Q263583","labels":{"en":{"language":"en","value":"House on Haunted Hill"}},
              "claims":{
                "P31":[{"rank":"normal","mainsnak":{"snaktype":"value","datavalue":{"value":{"id":"Q11424"}}}}],
                "P345":[{"rank":"normal","mainsnak":{"snaktype":"value","datavalue":{"value":"tt0051744"}}}],
                "P577":[
                  {"rank":"normal","mainsnak":{"snaktype":"value","datavalue":{"value":{"time":"+1958-12-17T00:00:00Z","precision":11}}}},
                  {"rank":"normal","mainsnak":{"snaktype":"value","datavalue":{"value":{"time":"+1959-01-14T00:00:00Z","precision":11}}}},
                  {"rank":"normal","mainsnak":{"snaktype":"value","datavalue":{"value":{"time":"+1959-02-17T00:00:00Z","precision":11}}}},
                  {"rank":"normal","mainsnak":{"snaktype":"value","datavalue":{"value":{"time":"+1959-10-27T00:00:00Z","precision":11}}}},
                  {"rank":"deprecated","mainsnak":{"snaktype":"value","datavalue":{"value":{"time":"+1957-01-01T00:00:00Z","precision":9}}}}]}}}}
            """;
        using var http = new HttpClient(new Responses(request =>
            request.RequestUri!.Query.Contains("wbgetentities", StringComparison.Ordinal) ? entities : search));
        var page = await new WikidataMetadataClient(new(http)).GetPageAsync(
            new(AudiovisualMediaKind.Movie, AudiovisualCatalogMode.Search, "House on Haunted Hill"));

        var film = Assert.Single(page.Items).Identity;
        Assert.Equal(1958, film.Year);
        Assert.Equal(new[] { 1958, 1959 }, film.ReleaseYears);
    }

    [Fact]
    public async Task ArchiveUploadDatedByAnotherRecordedReleaseYearIsOffered()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Responses(request =>
        {
            requests.Add(Uri.UnescapeDataString(request.RequestUri!.Query));
            return request.RequestUri.AbsolutePath == "/advancedsearch.php"
                ? """{"response":{"docs":[{"identifier":"house_on_haunted_hill_ipod","title":"House on Haunted Hill","year":1959}]}}"""
                : """
                  {"metadata":{"identifier":"house_on_haunted_hill_ipod","title":"House on Haunted Hill","year":"1959"},
                   "files":[{"name":"house_on_haunted_hill.mp4","format":"MPEG4","source":"original","size":"700000000","length":"4483.2"}]}
                  """;
        }));
        var provider = new InternetArchiveSourceProvider(new AudiovisualOptions { InternetArchiveBaseUri = new Uri("https://archive.example/") },
            new ProviderRequestCoordinator(http));

        AudiovisualSource source = Assert.Single(await provider.FindSourcesAsync(HouseOnHauntedHill, AudiovisualUnit.Feature));

        Assert.Equal(1959, source.Identity.Year);
        Assert.Contains(requests, query => query.Contains("year:1958", StringComparison.Ordinal) &&
            query.Contains("year:1959", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemakeFromAnUnrecordedYearStaysApart()
    {
        using var http = new HttpClient(new Responses(request => request.RequestUri!.AbsolutePath == "/advancedsearch.php"
            ? """{"response":{"docs":[{"identifier":"hohh-1999","title":"House on Haunted Hill","year":1999}]}}"""
            : """{"metadata":{"identifier":"hohh-1999","title":"House on Haunted Hill","year":"1999"},"files":[{"name":"hohh.mp4","format":"MPEG4","source":"original"}]}"""));
        var provider = new InternetArchiveSourceProvider(new AudiovisualOptions { InternetArchiveBaseUri = new Uri("https://archive.example/") },
            new ProviderRequestCoordinator(http));

        Assert.Empty(await provider.FindSourcesAsync(HouseOnHauntedHill, AudiovisualUnit.Feature));
        Assert.False(ExactAudiovisualMatcher.IsIdentityMatch(HouseOnHauntedHill,
            HouseOnHauntedHill with { Year = 1960, ReleaseYears = [] }));
    }

    [Theory]
    [InlineData(1959, SourceVerificationStatus.Verified)]
    [InlineData(1958, SourceVerificationStatus.Verified)]
    [InlineData(1960, SourceVerificationStatus.Rejected)]
    [InlineData(1999, SourceVerificationStatus.Rejected)]
    public void ProviderItemYearIsCheckedAgainstEveryRecordedReleaseYear(int providerYear, SourceVerificationStatus expected)
    {
        // A provider item for the same IMDb title, dated by its own release year.
        var evidence = new AudiovisualSourceEvidence
        {
            Origin = SourceEvidenceOrigin.ProviderItem,
            Identity = new AudiovisualIdentity
            {
                Kind = AudiovisualMediaKind.Movie, ContentForm = AudiovisualContentForm.Feature,
                Title = "House on Haunted Hill", Year = providerYear, ImdbId = "tt0051744"
            },
            Unit = AudiovisualUnit.Feature
        };

        Assert.Equal(expected, ExactAudiovisualMatcher.VerifyEvidence(
            new SourceSearchRequest { Identity = HouseOnHauntedHill, Unit = AudiovisualUnit.Feature }, evidence).Status);
    }

    private sealed class Responses(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json")
            });
        }
    }
}
