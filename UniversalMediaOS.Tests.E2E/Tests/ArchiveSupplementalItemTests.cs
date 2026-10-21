using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

/// <summary>The trailer and featurette records were observed on archive.org on October 21, 2026, trimmed to
/// the fields the provider reads. Their titles and years match the requested films exactly; the curated
/// movie_trailers collection disqualifies them. (A free-form "trailer" subject alone only withholds
/// verification, as ArchiveMovieFileSelectionTests records.)</summary>
public sealed class ArchiveSupplementalItemTests
{
    [Fact]
    public async Task TrailerItemWithTheFilmsTitleAndYearIsNotOfferedForTheFilm()
    {
        string search = """
            {"response":{"docs":[{"date":"1990-01-01T00:00:00Z","identifier":"turner_video_47924",
              "subject":["trailer","Movie","Horror"],"title":"Night of the Living Dead","year":1990}]}}
            """;
        string item = """
            {"metadata":{"identifier":"turner_video_47924","mediatype":"movies",
              "collection":["movie_trailers_unsorted","movie_trailers","moviesandfilms"],
              "title":"Night of the Living Dead","subject":["trailer","Movie","Horror"]},
             "files":[{"name":"47924.ia.mp4","format":"h.264 IA","source":"derivative","size":"62154813","length":"66.07"},
                      {"name":"47924.m4v","format":"MPEG4","source":"original","size":"39353743","length":"66.09"},
                      {"name":"47924.mp4","format":"MPEG4","source":"original","size":"62154813","length":"66.07"}]}
            """;

        Assert.Empty(await FindSourcesAsync(Film("Night of the Living Dead", 1990), search, ("turner_video_47924", item)));
    }

    [Fact]
    public async Task FeaturetteItemWithTheFilmsTitleAndYearIsNotOfferedForTheFilm()
    {
        string search = """
            {"response":{"docs":[{"date":"1959-01-01T00:00:00Z","identifier":"turner_video_384146",
              "subject":["featurette","Movie","Horror"],"title":"House on Haunted Hill","year":1959}]}}
            """;
        string item = """
            {"metadata":{"identifier":"turner_video_384146","mediatype":"movies",
              "collection":["movie_trailers_unsorted","movie_trailers","moviesandfilms"],
              "title":"House on Haunted Hill","subject":["featurette","Movie","Horror"]},
             "files":[{"name":"384146.ia.mp4","format":"h.264 IA","source":"derivative","size":"49446625","length":"172.22"},
                      {"name":"384146.mp4","format":"MPEG4","source":"original","size":"49446625","length":"172.22"}]}
            """;

        Assert.Empty(await FindSourcesAsync(Film("House on Haunted Hill", 1959), search, ("turner_video_384146", item)));
    }

    [Fact]
    public async Task FilmWhoseOwnTitleNamesASupplementalFormIsStillOffered()
    {
        string search = """{"response":{"docs":[{"identifier":"tpb-movie","title":"Trailer Park Boys","year":2006}]}}""";
        string item = """
            {"metadata":{"identifier":"tpb-movie","title":"Trailer Park Boys","year":"2006",
              "subject":["Trailer Park Boys","Comedy"],"collection":["Comedy_Films","feature_films"]},
             "files":[{"name":"Trailer_Park_Boys.mp4","format":"MPEG4","source":"original","size":"900000000"}]}
            """;

        AudiovisualSource source = Assert.Single(await FindSourcesAsync(Film("Trailer Park Boys", 2006), search, ("tpb-movie", item)));
        Assert.Equal("https://archive.example/download/tpb-movie/Trailer_Park_Boys.mp4", source.Location.AbsoluteUri);
    }

    [Fact]
    public async Task SupersededHistoryRevisionIsNeverTheMediaFile()
    {
        // Archive keeps replaced files as history/files/<name>.~N~; this one is the item's only original.
        AudiovisualSource source = Assert.Single(await FindFeatureAsync(
            """{"name":"history/files/Example_Film.mp4.~1~","format":"MPEG4","source":"original","size":"900000000"}""",
            """{"name":"Example_Film.webm","format":"WebM","source":"derivative","size":"400000000"}"""));

        Assert.Equal("https://archive.example/download/film-item/Example_Film.webm", source.Location.AbsoluteUri);
    }

    [Fact]
    public async Task ItemWithOnlySupersededRevisionsOffersNoSource()
    {
        Assert.Empty(await FindFeatureAsync(
            """{"name":"history/files/Example_Film.mp4.~1~","format":"MPEG4","source":"original","size":"900000000"}""",
            """{"name":"history/files/Example_Film.ia.mp4.~1~","format":"h.264 IA","source":"derivative","size":"800000000"}"""));
    }

    private static AudiovisualIdentity Film(string title, int year) => new()
    {
        Kind = AudiovisualMediaKind.Movie,
        ContentForm = AudiovisualContentForm.Feature,
        Title = title,
        Year = year
    };

    private static Task<IReadOnlyList<AudiovisualSource>> FindFeatureAsync(params string[] files) => FindSourcesAsync(
        Film("Example Film", 2000),
        """{"response":{"docs":[{"identifier":"film-item","title":"Example Film","year":2000}]}}""",
        ("film-item", $$"""{"metadata":{"identifier":"film-item","title":"Example Film","year":"2000","subject":["Feature films"]},"files":[{{string.Join(",", files)}}]}"""));

    private static async Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(AudiovisualIdentity film, string search,
        params (string Identifier, string Item)[] items)
    {
        var responses = items.ToDictionary(item => "/metadata/" + item.Identifier, item => item.Item);
        responses["/advancedsearch.php"] = search;
        using var http = new HttpClient(new Responses(responses));
        var provider = new InternetArchiveSourceProvider(new AudiovisualOptions
        {
            InternetArchiveBaseUri = new Uri("https://archive.example/")
        }, new ProviderRequestCoordinator(http));
        return await provider.FindSourcesAsync(film, AudiovisualUnit.Feature);
    }

    private sealed class Responses(Dictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.True(responses.TryGetValue(request.RequestUri!.AbsolutePath, out string? body),
                $"Unexpected request {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body!, Encoding.UTF8, "application/json")
            });
        }
    }
}
