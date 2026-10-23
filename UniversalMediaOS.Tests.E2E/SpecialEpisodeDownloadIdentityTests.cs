using System.IO;
using System.Reflection;
using MonoTorrent;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Routing;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class SpecialEpisodeDownloadIdentityTests
{
    private const string Title = "Mushoku Tensei: Jobless Reincarnation Cour 2 - Eris the Goblin Slayer";
    private const string Alias = "Mushoku Tensei: Jobless Reincarnation Cour 2 Special";
    private const string NumberedTitle = "Fixture Chronicle: Northern Lights";

    [Theory]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " OVA Episode 02 [1080p]", true)]
    [InlineData("SPECIAL", 3, 3, NumberedTitle + " Special Ep 03", true)]
    [InlineData("TV", 2, 2, NumberedTitle + " OVA Episode 02", false)]
    [InlineData("ONA", 2, 2, NumberedTitle + " OVA Episode 02", false)]
    [InlineData("ONA", 2, 2, NumberedTitle + " Episode 02", true)]
    [InlineData("OVA", 0, 2, NumberedTitle + " OVA Episode 02", false)]
    [InlineData("OVA", 1, 2, NumberedTitle + " OVA Episode 02", false)]
    [InlineData("OVA", 2, 3, NumberedTitle + " OVA Episode 03", false)]
    [InlineData("OVA", 2, 2, NumberedTitle + " OVA", false)]
    [InlineData("OVA", 2, 2, NumberedTitle + " OVA 2", false)]
    [InlineData("OVA", 2, 2, NumberedTitle + " OVA Episode 02.5", false)]
    [InlineData("OVA", 2, 2, NumberedTitle + " OVA Episode 02-03", false)]
    [InlineData("OVA", 2, 2, NumberedTitle + " OVA Episode 02a", false)]
    [InlineData("OVA", 2, 2, NumberedTitle + " OVA Episode II", false)]
    [InlineData("OVA", 2, 2, NumberedTitle + " OVA Episode 02 Episode 01", false)]
    [InlineData("OVA", 2, 2, NumberedTitle + " OVA S01E02", false)]
    [InlineData("OVA", 2, 2, NumberedTitle + " OVA Season 2 Episode 02", false)]
    [InlineData("OVA", 2, 2, NumberedTitle + " OVA Batch Episode 02", false)]
    [InlineData("OVA", 2, 2, NumberedTitle + " ONA Episode 02", false)]
    [InlineData("OVA", 2, 2, NumberedTitle + " Movie OVA Episode 02", false)]
    [InlineData("OVA", 2, 2, NumberedTitle + " Extra Story OVA Episode 02", false)]
    [InlineData("OVA", 2, 2, "Fixture Chronicle: Southern Lights OVA Episode 02", false)]
    // Live October 23 release conventions: dash/E units, plural labels and technical tags.
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " OVA - 02 [DVD x264 AC3][7E15928E].mkv", true)]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " - E02 (OVA)[1080p][HEVC x265 10bit][Multi-Subs]", true)]
    [InlineData("SPECIAL", 2, 2, "[Group] " + NumberedTitle + " Specials - 02 [A9E28304][720p, AAC].mkv", true)]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " - 02 - OVA [DVD][480p][AAC].mp4", true)]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " OVA - 02 (DVD 1024x576 AAC 10bit) [489D792C].mkv", true)]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " OVA - 02 [576p][AAC].mp4", true)]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " OVA - 02 (DVD 1024x576) Southern", false)]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " - 02 [1080p]", false)]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " OVA #02 [1080p]", false)]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " OVA - 02.5 [1080p]", false)]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " OVA - 01 ~ 02 [1080p]", false)]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " OVA - 02 - 03 [1080p]", false)]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " Specials 2 - 02 [1080p]", false)]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " OVA - 02 [Multi-Subs] (Southern Lights)", false)]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " Season 2 OVA - 02 [1080p]", false)]
    [InlineData("OVA", 2, 2, "[Group] " + NumberedTitle + " OVA - 02 [BD Batch]", false)]
    [InlineData("TV", 2, 2, "[Group] " + NumberedTitle + " OVA - 02 [1080p]", false)]
    public async Task NumberedMultiSpecialDiscoveryRequiresWholeWorkAndExplicitInRangeEpisode(
        string format, int count, int episode, string release, bool expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        Task<List<TorrentResult>> Search(string _, Action<string>? __, CancellationToken token) =>
            Task.FromResult(new List<TorrentResult>
            {
                new() { Title = release, MagnetLink = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567" }
            });
        try
        {
            var service = new TemporaryEpisodeWatchService(Search, root);
            var candidates = await service.FindCandidatesAsync(NumberedTitle, [], episode, "Sub", _ => { },
                CancellationToken.None, format, count);
            Assert.Equal(expected ? 1 : 0, candidates.Count);
        }
        finally { Directory.Delete(root); }
    }

    [Theory]
    [InlineData(NumberedTitle + " OVA Episode 02", NumberedTitle + " OVA Episode 02.mkv", true, true)]
    [InlineData(NumberedTitle + " OVA Episode 01", NumberedTitle + " OVA Episode 02.mkv", false, true)]
    [InlineData(NumberedTitle + " OVA Episode 02", NumberedTitle + " OVA Episode 01.mkv", true, false)]
    [InlineData(NumberedTitle + " Extra Story OVA Episode 02", NumberedTitle + " OVA Episode 02.mkv", false, true)]
    [InlineData(NumberedTitle + " OVA S01E02", NumberedTitle + " OVA S01E02.mkv", false, false)]
    [InlineData(NumberedTitle + " OVA Episode 02a", NumberedTitle + " OVA Episode 02a.mkv", false, false)]
    [InlineData(NumberedTitle + " OVA Episode 02", "Episode 02.mkv", true, false)]
    [InlineData("[Group] " + NumberedTitle + " - E02 (OVA)[1080p][HEVC x265 10bit][Multi-Subs]",
        "[Group] " + NumberedTitle + " - E02 (OVA)[1080p][HEVC x265 10bit][Multi-Subs].mkv", true, true)]
    [InlineData("[Group] " + NumberedTitle + " OVA - 02 [1080p]", "[Group] " + NumberedTitle + " - 02 [1080p].mkv", true, false)]
    [InlineData("[Group] " + NumberedTitle + " OVA - 02 [1080p]", "[Group] " + NumberedTitle + " OVA - 01 [1080p].mkv", true, false)]
    public void NumberedMultiSpecialChecksMetadataAndTheActualSelectedVideo(
        string metadata, string video, bool metadataExpected, bool videoExpected)
    {
        Assert.Equal(metadataExpected, TemporaryEpisodeWatchService.FileMatchesNumberedSpecial(metadata, 2, [NumberedTitle]));
        var file = Video(video);
        var selected = TemporaryEpisodeWatchService.SelectEpisodeFile([file], 2, numberedSpecialTitles: [NumberedTitle]);
        if (videoExpected) Assert.Same(file, selected);
        else Assert.Null(selected);
    }

    [Fact]
    public void NumberedMultiSpecialCannotSelectFromAMultiVideoRelease()
    {
        var selected = Video(NumberedTitle + " OVA Episode 02.mkv");
        var other = Video(NumberedTitle + " OVA Episode 01.mkv");
        Assert.Null(TemporaryEpisodeWatchService.SelectEpisodeFile([selected, other], 2,
            numberedSpecialTitles: [NumberedTitle]));
    }

    [Theory]
    [InlineData("光・影", "光・影 OVA Episode 02.mkv", true)]
    [InlineData("光・影", "光 影 OVA Episode 02.mkv", false)]
    [InlineData("カ\u3099ラスの光", "ガラスの光 OVA Episode 02.mkv", true)]
    public void NumberedMultiSpecialPreservesTheWholeNativeTitle(string title, string file, bool expected)
    {
        var selected = TemporaryEpisodeWatchService.SelectEpisodeFile([Video(file)], 2, numberedSpecialTitles: [title]);
        Assert.Equal(expected, selected != null);
    }

    private static ITorrentManagerFile Video(string name)
    {
        var file = DispatchProxy.Create<ITorrentManagerFile, SpecialEpisodeMappingTests.FileProjection>();
        ((SpecialEpisodeMappingTests.FileProjection)(object)file).Path = name;
        return file;
    }

    [Theory]
    [InlineData("SPECIAL", 1, 1, "[Group] " + Title + " OVA", "Sub", true)]
    [InlineData("OVA", 1, 1, "[Group] " + Title + " OVA [Dual Audio]", "Dub", true)]
    [InlineData("SPECIAL", 1, 1, "[Group] " + Alias + " [1080p]", "Sub", true)]
    [InlineData("TV", 1, 1, "[Group] " + Title + " OVA", "Sub", false)]
    [InlineData("", 1, 1, "[Group] " + Title + " OVA", "Sub", false)]
    [InlineData("SPECIAL", 2, 1, "[Group] " + Title + " OVA", "Sub", false)]
    [InlineData("SPECIAL", 1, 2, "[Group] " + Title + " OVA", "Sub", false)]
    [InlineData("SPECIAL", 1, 1, "[Group] Goblin Slayer OVA", "Sub", false)]
    [InlineData("SPECIAL", 1, 1, "[Group] Mushoku Tensei OVA", "Sub", false)]
    [InlineData("SPECIAL", 1, 1, "[Group] " + Title + " - 13.5", "Sub", false)]
    [InlineData("SPECIAL", 1, 1, "[Group] " + Title + " OVA 2", "Sub", false)]
    [InlineData("SPECIAL", 1, 1, "[Group] " + Title + " OVA", "Dub", false)]
    [InlineData("SPECIAL", 1, 1, "[Group] " + Title + " OVA [English Dub]", "Sub", false)]
    public async Task DiscoveryRequiresCatalogFormCountExactWorkAndRequestedAudio(
        string format, int count, int episode, string release, string audio, bool expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        Task<List<TorrentResult>> Search(string _, Action<string>? __, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new List<TorrentResult>
            {
                new() { Title = release, MagnetLink = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", Seeders = 5 }
            });
        }
        try
        {
            var service = new TemporaryEpisodeWatchService(Search, root);
            var results = await service.FindCandidatesAsync(Title, [Alias], episode, audio, _ => { },
                CancellationToken.None, format, count);
            Assert.Equal(expected ? 1 : 0, results.Count);
        }
        finally { Directory.Delete(root); }
    }

    [Theory]
    [InlineData("[Group] " + Title + " OVA.mkv", true)]
    [InlineData("[Group] " + Alias + ".mkv", true)]
    [InlineData("[Group] " + Title + " - 01.mkv", true)]
    [InlineData("[Group] Goblin Slayer OVA.mkv", false)]
    [InlineData("[Group] Mushoku Tensei - 01.mkv", false)]
    [InlineData("[Group] " + Title + " - 13.5.mkv", false)]
    [InlineData("[Group] " + Title + " OVA 1.mkv", false)]
    [InlineData("[Group] " + Title + " Complete.mkv", false)]
    public void SelectedFileMustIdentifyTheSpecialWithoutGuessingANonstandardUnit(string file, bool expected)
    {
        Assert.Equal(expected, TemporaryEpisodeWatchService.FileMatchesCatalogEpisode(file, 1, [Title, Alias]));
    }
}
