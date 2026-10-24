using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using MonoTorrent;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Routing;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

// Live October 23: "[SubsPlease] Kaguya-sama wa Kokurasetai - Otona e no Kaidan - 01" names the whole
// SPECIAL but carries no OVA/Special label. It may match only when AniList shows that title belongs to
// exactly one catalog entry, the requested one.
public sealed class SpecialEpisodeTitleUniquenessTests
{
    private const int CatalogId = 194884;
    private const string Title = "Fixture Chronicle: Northern Lights";
    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("[Group] " + Title + " - 02 (1080p) [32DA56DA].mkv", true, true)]
    [InlineData("[Group] " + Title + " - 02 (1080p) [32DA56DA].mkv", false, false)]
    [InlineData("[Group] " + Title + " - 01 (1080p).mkv", true, false)]
    [InlineData("[Group] " + Title + " - 02.5 (1080p).mkv", true, false)]
    [InlineData("[Group] " + Title + " (01-02) (1080p) [Batch]", true, false)]
    [InlineData("[Group] " + Title + " Season 2 - 02 (1080p).mkv", true, false)]
    [InlineData("[Group] " + Title + " - 02 (Southern Lights) (1080p).mkv", true, false)]
    [InlineData("[Group] Fixture Chronicle - 02 (1080p).mkv", true, false)]
    public async Task UnlabelledReleaseNeedsAniListUniquenessAndEveryOtherGuard(string release, bool unique, bool expected)
    {
        var checkedTitles = new List<string>();
        var (service, root) = Service(release, (id, title, _) =>
        {
            checkedTitles.Add(title);
            return Task.FromResult(unique && id == CatalogId && title == Title);
        });
        try
        {
            var proven = new HashSet<string>(StringComparer.Ordinal);
            var candidates = await service.FindCandidatesAsync(Title, [], 2, "Sub", _ => { }, CancellationToken.None,
                "SPECIAL", 2, CatalogId, proven);
            Assert.Equal(expected ? 1 : 0, candidates.Count);
            Assert.Equal(expected, proven.Contains(Title));
            // A parent-series or otherwise non-matching name never reaches the catalog check.
            if (release.Contains("Fixture Chronicle - 02")) Assert.Empty(checkedTitles);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    // An ordinary TV entry keeps its existing unlabelled episode matching and never asks AniList.
    [InlineData("TV", 2, CatalogId, 1)]
    [InlineData("SPECIAL", 2, 0, 0)]
    public async Task UnlabelledMatchingNeedsANumberedSpecialAndItsCatalogId(string format, int count, int catalogId, int expected)
    {
        bool asked = false;
        var (service, root) = Service("[Group] " + Title + " - 02 (1080p).mkv", (_, _, _) => { asked = true; return Task.FromResult(true); });
        try
        {
            var proven = new HashSet<string>();
            var candidates = await service.FindCandidatesAsync(Title, [], 2, "Sub", _ => { }, CancellationToken.None,
                format, count, catalogId, proven);
            Assert.Equal(expected, candidates.Count);
            Assert.False(asked);
            Assert.Empty(proven);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MetadataAndSelectedVideoUseOnlyTheProvenUniqueTitle()
    {
        string name = "[Group] " + Title + " - 02 (1080p) [32DA56DA].mkv";
        Assert.False(TemporaryEpisodeWatchService.FileMatchesNumberedSpecial(name, 2, [Title]));
        Assert.True(TemporaryEpisodeWatchService.FileMatchesNumberedSpecial(name, 2, [Title], [Title]));
        Assert.False(TemporaryEpisodeWatchService.FileMatchesNumberedSpecial(name, 2, [Title], ["Fixture Chronicle"]));
        var file = DispatchProxy.Create<ITorrentManagerFile, SpecialEpisodeMappingTests.FileProjection>();
        ((SpecialEpisodeMappingTests.FileProjection)(object)file).Path = name;
        Assert.Null(TemporaryEpisodeWatchService.SelectEpisodeFile([file], 2, numberedSpecialTitles: [Title]));
        Assert.Same(file, TemporaryEpisodeWatchService.SelectEpisodeFile([file], 2, numberedSpecialTitles: [Title],
            unlabelledNumberedTitles: [Title]));
    }

    private const string Page = """{"data":{"Page":{"pageInfo":{"hasNextPage":%NEXT%},"media":[%MEDIA%]}}}""";

    private static string Media(int id, string english, string romaji = "", params string[] synonyms) =>
        $$"""{"id":{{id}},"title":{"romaji":"{{romaji}}","english":"{{english}}","native":null},"synonyms":[{{string.Join(",", synonyms.Select(s => $"\"{s}\""))}}]}""";

    private static string Json(bool next, params string[] media) =>
        Page.Replace("%NEXT%", next ? "true" : "false").Replace("%MEDIA%", string.Join(",", media));

    [Theory]
    [MemberData(nameof(AniListPages))]
    public void AniListPageMustShowExactlyTheRequestedEntry(string json, string title, bool expected)
    {
        Assert.Equal(expected, TemporaryEpisodeWatchService.AniListTitleBelongsOnlyTo(json, CatalogId, title));
    }

    public static IEnumerable<object[]> AniListPages()
    {
        const string romaji = "Kaguya-sama wa Kokurasetai: Otona e no Kaidan";
        string special = Media(CatalogId, "Kaguya-sama: Love Is War -Stairway to Adulthood-", romaji);
        string parent = Media(101921, "Kaguya-sama: Love is War", "Kaguya-sama wa Kokurasetai: Tensai-tachi no Renai Zunousen");
        yield return [Json(false, special, parent), romaji, true];
        yield return [Json(false, special, parent), "kaguya sama wa kokurasetai otona e no kaidan", true];
        // The 1993 JoJo OVA and the 2012 TV series share an English title: not unique.
        yield return [Json(false, Media(CatalogId, "JoJo's Bizarre Adventure", "JoJo no Kimyou na Bouken"),
            Media(14719, "JoJo's Bizarre Adventure", "JoJo no Kimyou na Bouken (TV)")), "JoJo's Bizarre Adventure", false];
        // Live October 23: AniList's romaji for the 2012 TV series carries a "(TV)" disambiguator that
        // releases drop, so "[DeadFish] JoJo no Kimyou na Bouken - 01" must not prove the 1993 OVA.
        yield return [Json(false, Media(CatalogId, "JoJo's Bizarre Adventure OVA", "JoJo no Kimyou na Bouken"),
            Media(14719, "JoJo's Bizarre Adventure (2012)", "JoJo no Kimyou na Bouken (TV)")), "JoJo no Kimyou na Bouken", false];
        yield return [Json(false, Media(CatalogId, "Fixture Chronicle"), Media(9, "Fixture Chronicle (2019)")), "Fixture Chronicle", false];
        yield return [Json(false, special, Media(5, "Other", "Other", romaji)), romaji, false];
        yield return [Json(false, Media(77, "Elsewhere", romaji), parent), romaji, false];
        yield return [Json(false, parent), romaji, false];
        yield return [Json(true, special, parent), romaji, false];
        yield return ["""{"errors":[{"message":"Too Many Requests."}],"data":null}""", romaji, false];
        yield return ["not json", romaji, false];
    }

    [Fact]
    public async Task AniListFailureIsNotUniquenessButCallerCancellationRemainsCancellation()
    {
        using var limited = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        Assert.False(await TemporaryEpisodeWatchService.AniListTitleIsUniqueAsync(limited, "https://anilist.invalid/graphql",
            CatalogId, Title, CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TemporaryEpisodeWatchService.AniListTitleIsUniqueAsync(
            limited, "https://anilist.invalid/graphql", CatalogId, Title, canceled.Token));
    }

    private static (TemporaryEpisodeWatchService Service, string Root) Service(string release,
        Func<int, string, CancellationToken, Task<bool>> unique)
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        Task<List<TorrentResult>> Search(string _, Action<string>? __, CancellationToken token) =>
            Task.FromResult(new List<TorrentResult> { new() { Title = release, MagnetLink = Magnet, Seeders = 5 } });
        return (new TemporaryEpisodeWatchService(Search, root, catalogTitleIsUnique: unique), root);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
