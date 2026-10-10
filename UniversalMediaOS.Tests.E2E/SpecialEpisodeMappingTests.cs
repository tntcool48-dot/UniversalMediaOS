using System.IO;
using System.Reflection;
using MonoTorrent;
using MonoTorrent.Client;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Routing;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class SpecialEpisodeMappingTests
{
    private const string Catalog = "Mushoku Tensei: Jobless Reincarnation Cour 2 - Eris the Goblin Slayer";
    private const string Release = "Mushoku Tensei: Jobless Reincarnation - OVA Eris the Goblin Slayer [English Dub][1080p] | Mushoku Tensei Jobless Reincarnation Season 1 Episode 24 [English Dub]";

    [Theory]
    [InlineData(Release, true, 24)]
    [InlineData("Mushoku Tensei: Jobless Reincarnation - OVA Eris the Goblin Slayer S01E17.5", true, 17.5)]
    [InlineData("Mushoku Tensei: Jobless Reincarnation - S01E17.5 (OVA)", false, 0)]
    [InlineData("Goblin Slayer OVA Episode 24", false, 0)]
    [InlineData(Catalog + " Episode 13.5", false, 0)]
    [InlineData(Catalog + " OVA Episode 13-14", false, 0)]
    [InlineData(Catalog + " OVA Episode 13a", false, 0)]
    [InlineData(Catalog + " OVA Batch Episode 24", false, 0)]
    public void MappingNeedsTheExactDistinctSpecialAndOneExplicitProviderUnit(string release, bool expected, double number)
    {
        var mapping = TemporaryEpisodeWatchService.TryMapSingleSpecialRelease(release, [Catalog]);
        Assert.Equal(expected, mapping != null);
        if (mapping != null)
        {
            Assert.Equal((decimal)number, mapping.ProviderNumber);
            Assert.Equal(Catalog, mapping.CatalogTitle);
        }
    }

    [Theory]
    [InlineData("Mushoku Tensei Jobless Reincarnation Season 1 Episode 24.mkv", true)]
    [InlineData("Mushoku Tensei S01E24.mkv", true)]
    [InlineData("Goblin Slayer S01E24.mkv", false)]
    [InlineData("Mushoku Tensei S02E24.mkv", false)]
    [InlineData("Mushoku Tensei S01E23.mkv", false)]
    [InlineData("Mushoku Tensei S01E24.5.mkv", false)]
    [InlineData("Mushoku Tensei Episode 24-25.mkv", false)]
    [InlineData("Mushoku Tensei Complete Episode 24.mkv", false)]
    public void MappedMetadataAndFileRetainProviderUnitAndFranchise(string name, bool expected)
    {
        var mapping = Assert.IsType<TemporaryEpisodeWatchService.SpecialEpisodeMapping>(
            TemporaryEpisodeWatchService.TryMapSingleSpecialRelease(Release, [Catalog]));
        Assert.Equal(expected, TemporaryEpisodeWatchService.FileMatchesSpecialMapping(name, mapping));
        Assert.False(TemporaryEpisodeWatchService.EpisodeMatches(name, 1));
    }

    [Fact]
    public void AProviderMappingCannotSelectOneMemberOfAMultiVideoBatch()
    {
        var mapping = Assert.IsType<TemporaryEpisodeWatchService.SpecialEpisodeMapping>(
            TemporaryEpisodeWatchService.TryMapSingleSpecialRelease(Release, [Catalog]));
        ITorrentManagerFile File(string name)
        {
            var file = DispatchProxy.Create<ITorrentManagerFile, FileProjection>();
            ((FileProjection)(object)file).Path = name;
            return file;
        }
        var selected = File("Mushoku Tensei Season 1 Episode 24.mkv");
        var other = File("Mushoku Tensei Season 1 Episode 23.mkv");
        Assert.Same(selected, TemporaryEpisodeWatchService.SelectEpisodeFile([selected], 1, [Catalog], mapping));
        Assert.Null(TemporaryEpisodeWatchService.SelectEpisodeFile([selected, other], 1, [Catalog], mapping));
    }

    [Fact]
    public async Task CompactCatalogSearchCanFindAQualifiedProviderUnitAfterIncompleteLongQueries()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        var queries = new List<string>();
        Task<List<TorrentResult>> Search(string query, Action<string>? _, CancellationToken token)
        {
            queries.Add(query);
            if (query == "Mushoku Tensei OVA")
                return Task.FromResult(new List<TorrentResult> { new() { Title = Release, MagnetLink = "magnet:?xt=urn:btih:match" } });
            throw new TorrentSearchIncompleteException("Fixture feed unavailable.");
        }
        try
        {
            var service = new TemporaryEpisodeWatchService(Search, root);
            var results = await service.FindCandidatesAsync(Catalog, [], 1, "Dub", _ => { }, CancellationToken.None, "SPECIAL", 1);
            Assert.Equal("Mushoku Tensei OVA", queries[0]);
            Assert.Equal(Release, Assert.Single(results).Title);
            Assert.True(queries.Count <= 4);
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public async Task ExhaustedPartialDiscoveryIsNotReportedAsCompleteAbsence()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        Task<List<TorrentResult>> Search(string _, Action<string>? __, CancellationToken token) =>
            throw new TorrentSearchIncompleteException("Fixture source failed.");
        try
        {
            var service = new TemporaryEpisodeWatchService(Search, root);
            var error = await Assert.ThrowsAsync<TorrentSearchIncompleteException>(() =>
                service.FindCandidatesAsync(Catalog, [], 1, "Sub", _ => { }, CancellationToken.None, "SPECIAL", 1));
            Assert.Contains("incomplete", error.Message);
        }
        finally { Directory.Delete(root); }
    }

    public class FileProjection : DispatchProxy
    {
        public string Path { get; set; } = "";
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_FullPath" ? Path : throw new NotSupportedException(targetMethod?.Name);
    }
}
