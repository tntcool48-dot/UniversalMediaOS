using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class ArchiveMovieFileSelectionTests
{
    [Fact]
    public async Task OriginalMp4TrailerCannotReplaceTheFullFilm()
    {
        AudiovisualSource source = Assert.Single(await FindSourcesAsync("Example Film",
            new VideoFile("Example_Film_Official_Trailer.mp4", "MPEG4", "original", "10000"),
            new VideoFile("Example_Film.webm", "WebM", "derivative", "1000000")));

        Assert.Equal("https://archive.example/download/film-item/Example_Film.webm", source.Location.AbsoluteUri);
        Assert.Equal(SourceEvidenceOrigin.ProviderItem, source.Evidence?.Origin);
        Assert.Equal(SourceVerificationStatus.Verified, ExactAudiovisualMatcher.VerifyEvidence(
            new SourceSearchRequest { Identity = Identity("Example Film") }, source.Evidence).Status);
        Assert.Empty(source.Evidence!.Audio?.Languages ?? []);
    }

    [Fact]
    public async Task TrailerOnlyItemCannotOfferAFullFilmSource()
    {
        Assert.Empty(await FindSourcesAsync("Example Film",
            new VideoFile("Example_Film_trailer.mp4", "MPEG4", "original", "10000")));
    }

    [Fact]
    public async Task MarkerInsideTheObservedFilmTitleDoesNotRejectItsFullFile()
    {
        AudiovisualSource source = Assert.Single(await FindSourcesAsync("Trailer Park Boys",
            new VideoFile("Trailer_Park_Boys_Official_Trailer.mp4", "MPEG4", "original", "20000"),
            new VideoFile("Trailer_Park_Boys.mp4", "MPEG4", "original", "10000")));

        Assert.Equal("https://archive.example/download/film-item/Trailer_Park_Boys.mp4", source.Location.AbsoluteUri);
    }

    [Fact]
    public async Task KnownSupplementalFilesCannotSupplyAFeatureSource()
    {
        Assert.Empty(await FindSourcesAsync("Example Film",
            new VideoFile("Example_Film_teaser.mp4", "MPEG4", "original", "10000"),
            new VideoFile("preview/Example_Film.webm", "WebM", "original", "20000"),
            new VideoFile("Example_Film_clip.mkv", "Matroska", "original", "30000"),
            new VideoFile("Example_Film_behind_the_scenes.mp4", "MPEG4", "original", "40000")));
    }

    [Theory]
    [InlineData("Example_Show_S01E02.mp4")]
    [InlineData("Example_Show_S02E01.mp4")]
    public async Task EpisodeFileCannotContradictTheObservedItemUnit(string wrongFile)
    {
        AudiovisualIdentity identity = Identity("Example Show") with
        {
            Kind = AudiovisualMediaKind.Television,
            ContentForm = AudiovisualContentForm.Series
        };
        var unit = new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 1 };
        AudiovisualSource source = Assert.Single(await FindSourcesAsync(identity, unit,
            new VideoFile(wrongFile, "MPEG4", "original", "20000"),
            new VideoFile("Example_Show_S01E01.webm", "WebM", "derivative", "10000")));

        Assert.EndsWith("Example_Show_S01E01.webm", source.Location.AbsoluteUri);
        Assert.Equal(unit.SeasonNumber, source.Evidence?.Unit?.SeasonNumber);
        Assert.Equal(unit.EpisodeNumber, source.Evidence?.Unit?.EpisodeNumber);
    }

    [Fact]
    public async Task EpisodeOnlyWrongUnitCannotSupplyTheRequestedEpisode()
    {
        AudiovisualIdentity identity = Identity("Example Show") with
        {
            Kind = AudiovisualMediaKind.Television,
            ContentForm = AudiovisualContentForm.Series
        };
        Assert.Empty(await FindSourcesAsync(identity, new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 1 },
            new VideoFile("Example_Show_episode_2.mp4", "MPEG4", "original", "10000")));
    }

    [Fact]
    public async Task FeatureFileCannotBeAnExplicitSeriesEpisode()
    {
        Assert.Empty(await FindSourcesAsync("Example Film",
            new VideoFile("Example_Film_S01E01.mp4", "MPEG4", "original", "10000")));
    }

    [Theory]
    [InlineData("Example_Show_1x01.mp4", true)]
    [InlineData("Example_Show_1x02.mp4", false)]
    [InlineData("Example_Show_S01E01.5.mp4", false)]
    [InlineData("Example_Show_S01E01-02.mp4", false)]
    [InlineData("Example_Show_S01E01_720x480.mp4", true)]
    public async Task AdditionalFilenameUnitClaimsKeepIntegerIdentity(string file, bool available)
    {
        AudiovisualIdentity identity = Identity("Example Show") with
        {
            Kind = AudiovisualMediaKind.Television,
            ContentForm = AudiovisualContentForm.Series
        };
        IReadOnlyList<AudiovisualSource> sources = await FindSourcesAsync(identity,
            new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 1 },
            new VideoFile(file, "MPEG4", "original", "10000"));
        if (available) Assert.EndsWith(file, Assert.Single(sources).Location.AbsoluteUri);
        else Assert.Empty(sources);
    }

    [Theory]
    [InlineData("Blender|Sintel|Movie|Film|Short|Fantasy", true)]
    [InlineData("Animated short film", true)]
    [InlineData("Short animated film", true)]
    [InlineData("Short|Fantasy", false)]
    [InlineData("Film|Short|Trailer", false)]
    [InlineData("Film|Short|Clip", false)]
    [InlineData("movies", false)]
    public async Task IndependentlyDeclaredShortFilmHasStandaloneUnitEvidence(string subjects, bool verified)
    {
        AudiovisualIdentity identity = Identity("Sintel") with { Year = 2010 };
        AudiovisualSource source = Assert.Single(await FindSourcesWithSubjectsAsync(identity,
            AudiovisualUnit.Feature, subjects.Split('|'),
            new VideoFile("data/sintel.mp4", "h.264", "original", "88506270")));

        SourceVerification result = ExactAudiovisualMatcher.VerifyEvidence(
            new SourceSearchRequest { Identity = identity, Unit = AudiovisualUnit.Feature }, source.Evidence);
        Assert.Equal(verified ? SourceVerificationStatus.Verified : SourceVerificationStatus.Unverified, result.Status);
        Assert.Empty(source.Evidence?.Audio?.Languages ?? []);
        if (verified)
        {
            Assert.Equal(SourceEvidenceOrigin.ProviderItem, source.Evidence?.Origin);
            Assert.True(source.Evidence?.Unit?.IsFeature);
            Assert.Equal(2010, source.Evidence?.Identity?.Year);
        }
    }

    [Theory]
    [InlineData("Short films")]
    [InlineData("Feature films")]
    public async Task SearchFilmTagsCannotOverrideSupplementalItemDeclaration(string searchSubject)
    {
        AudiovisualIdentity identity = Identity("Sintel") with { Year = 2010 };
        AudiovisualSource source = Assert.Single(await FindSourcesWithSubjectsAsync(identity,
            AudiovisualUnit.Feature, ["Film", "Short", "Trailer"], [searchSubject],
            new VideoFile("data/sintel.mp4", "h.264", "original", "88506270")));
        Assert.Equal(SourceVerificationStatus.Unverified, ExactAudiovisualMatcher.VerifyEvidence(
            new SourceSearchRequest { Identity = identity }, source.Evidence).Status);
    }

    private static AudiovisualIdentity Identity(string title) => new()
    {
        Kind = AudiovisualMediaKind.Movie,
        ContentForm = AudiovisualContentForm.Feature,
        Title = title,
        Year = 2000
    };

    private static Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(string title, params VideoFile[] files) =>
        FindSourcesAsync(Identity(title), AudiovisualUnit.Feature, files);

    private static Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(
        AudiovisualIdentity identity, AudiovisualUnit unit, params VideoFile[] files) =>
        FindSourcesWithSubjectsAsync(identity, unit,
            [identity.Kind == AudiovisualMediaKind.Movie ? "Feature films" : "Television"], files);

    private static Task<IReadOnlyList<AudiovisualSource>> FindSourcesWithSubjectsAsync(
        AudiovisualIdentity identity, AudiovisualUnit unit, string[] subjects, params VideoFile[] files) =>
        FindSourcesWithSubjectsAsync(identity, unit, subjects, [], files);

    private static async Task<IReadOnlyList<AudiovisualSource>> FindSourcesWithSubjectsAsync(
        AudiovisualIdentity identity, AudiovisualUnit unit, string[] subjects, string[] searchSubjects, params VideoFile[] files)
    {
        string title = identity.Title;
        string search = JsonSerializer.Serialize(new
        {
            response = new { docs = new[] { new
            {
                identifier = "film-item", title, year = identity.Year,
                season = unit.SeasonNumber, episode = unit.EpisodeNumber, subject = searchSubjects
            } } }
        });
        string item = JsonSerializer.Serialize(new
        {
            metadata = new
            {
                title, year = identity.Year, season = unit.SeasonNumber, episode = unit.EpisodeNumber,
                subject = subjects
            }, files
        });
        using var http = new HttpClient(new ItemResponses(search, item));
        var provider = new InternetArchiveSourceProvider(new AudiovisualOptions
        {
            InternetArchiveBaseUri = new Uri("https://archive.example/")
        }, new ProviderRequestCoordinator(http));
        return await provider.FindSourcesAsync(identity, unit);
    }

    private sealed record VideoFile(string name, string format, string source, string size);

    private sealed class ItemResponses(string search, string item) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Contains(request.RequestUri!.AbsolutePath, new[] { "/advancedsearch.php", "/metadata/film-item" });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri.AbsolutePath == "/advancedsearch.php" ? search : item,
                    Encoding.UTF8, "application/json")
            });
        }
    }
}
