using System.Text;
using System.IO;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Routing;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class TemporaryWatchDiscoveryTests
{
    private static void DeleteOwnedTestDirectory(string directory)
    {
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests")) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(directory);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its temporary root.");
        Directory.Delete(target, recursive: true);
    }

    [Fact]
    public void CompletedLongReleasePathsBecomeShortWithoutLosingBytesOrCaptionLanguage()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        string batch = Path.Combine(root, new string('b', 120));
        Directory.CreateDirectory(batch);
        string stem = new string('a', 120) + " - S01E01";
        string video = Path.Combine(batch, stem + ".mkv");
        string captions = Path.Combine(batch, stem + ".ar.srt");
        string otherEpisode = Path.Combine(batch, "episode-02.mkv");
        File.WriteAllBytes(video, [1, 2, 3, 4]);
        File.WriteAllText(captions, "1\n00:00:00,000 --> 00:00:01,000\nArabic cue\n");
        File.WriteAllBytes(otherEpisode, [9]);
        try
        {
            var result = TemporaryEpisodeWatchService.PublishEpisodeFiles(root, video, 1, [captions]);
            Assert.True(video.Length > 260);
            Assert.True(result.Path.Length < 260);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(result.Path));
            Assert.EndsWith("episode-0001.ar.srt", Assert.Single(result.Captions));
            Assert.Contains("Arabic cue", File.ReadAllText(result.Captions[0]));
            Assert.False(File.Exists(video));
            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(otherEpisode));
        }
        finally { DeleteOwnedTestDirectory(root); }
    }

    [Fact]
    public void PublishingRejectsOutsideCaptionBeforeMovingAnyFile()
    {
        string parent = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(parent, "owned");
        Directory.CreateDirectory(root);
        string video = Path.Combine(root, "original.mkv");
        string permanentCaption = Path.Combine(parent, "permanent.en.srt");
        File.WriteAllBytes(video, [1]);
        File.WriteAllText(permanentCaption, "preserve");
        try
        {
            Assert.Throws<InvalidDataException>(() => TemporaryEpisodeWatchService.PublishEpisodeFiles(root, video, 1, [permanentCaption]));
            Assert.True(File.Exists(video));
            Assert.Equal("preserve", File.ReadAllText(permanentCaption));
        }
        finally { DeleteOwnedTestDirectory(parent); }
    }

    [Fact]
    public void AnimeToshoDescriptionMagnetIsDecodedWithoutChoosingItsWebsiteOrTorrentEnclosure()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        string xml = $$"""
            <rss version="2.0"><channel><title>AnimeTosho</title><item>
            <title>Frieren S01E01 [English Dub]</title>
            <link>https://example.test/view/release</link>
            <enclosure url="https://example.test/release.torrent" type="application/x-bittorrent" length="0"/>
            <description><![CDATA[<a href="https://example.test/release.torrent">Torrent</a>/<a href="magnet:?xt=urn:btih:{{hash}}&amp;dn=Frieren&amp;tr=https%3A%2F%2Fexample.test%2Fannounce">Magnet</a>]]></description>
            </item></channel></rss>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = Assert.Single(DualTrackerRssParser.ParseFeed(stream, "AnimeTosho"));
        Assert.Equal($"magnet:?xt=urn:btih:{hash}&dn=Frieren&tr=https%3A%2F%2Fexample.test%2Fannounce", result.MagnetLink);
    }

    [Fact]
    public void NyaaInfoHashFallbackKeepsSeedersAndMagnet()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        string xml = $$"""
            <rss version="2.0" xmlns:nyaa="https://nyaa.si/xmlns/nyaa"><channel><title>Nyaa</title><item>
            <title>Frieren - 01</title><link>https://example.test/release</link>
            <nyaa:infoHash>{{hash}}</nyaa:infoHash><nyaa:seeders>12</nyaa:seeders>
            </item></channel></rss>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = Assert.Single(DualTrackerRssParser.ParseFeed(stream, "Nyaa"));
        Assert.Equal(12, result.Seeders);
        Assert.Equal(hash, result.InfoHash);
        Assert.StartsWith($"magnet:?xt=urn:btih:{hash}&", result.MagnetLink);
    }

    [Theory]
    [InlineData("Sub")]
    [InlineData("Dub")]
    public async Task RejectedFeedResultsDoNotPreventTrustedAliasSearch(string audio)
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        var queries = new List<string>();
        Task<List<TorrentResult>> Search(string query, Action<string>? _, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            queries.Add(query);
            if (query.StartsWith("Sousou no Frieren", StringComparison.Ordinal))
                return Task.FromResult(new List<TorrentResult>
                {
                    new() { Title = "[Release] Sousou no Frieren - 01 [Dual Audio]", MagnetLink = "magnet:?xt=urn:btih:match", Seeders = 5 }
                });
            return Task.FromResult(Enumerable.Range(0, 75).Select(i => new TorrentResult
            {
                Title = i % 2 == 0 ? "Unrelated Show - 01 [English Dub]" : "Frieren S02E01 [English Dub]",
                MagnetLink = $"magnet:?xt=urn:btih:rejected{i}", Seeders = 100
            }).ToList());
        }
        try
        {
            var service = new TemporaryEpisodeWatchService(Search, root);
            var results = await service.FindCandidatesAsync("Frieren: Beyond Journey’s End",
                ["Sousou no Frieren"], 1, audio, _ => { }, CancellationToken.None);
            Assert.Contains(queries, q => q.StartsWith("Sousou no Frieren", StringComparison.Ordinal));
            Assert.Equal("[Release] Sousou no Frieren - 01 [Dual Audio]", Assert.Single(results).Title);
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public async Task SeededDualAudioSearchRunsEvenWhenDubFeedHasEnoughUnknownSeedCounts()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        Task<List<TorrentResult>> Search(string query, Action<string>? _, CancellationToken token) => Task.FromResult(
            query.EndsWith("Dual Audio", StringComparison.Ordinal)
                ? new List<TorrentResult> { new() { Title = "Frieren Season 1 [Dual Audio]", MagnetLink = "magnet:?xt=urn:btih:seeded", Seeders = 34 } }
                : Enumerable.Range(0, 25).Select(i => new TorrentResult
                {
                    Title = $"[Group{i}] Frieren Season 1 [English Dub]", MagnetLink = $"magnet:?xt=urn:btih:unknown{i}"
                }).ToList());
        try
        {
            var service = new TemporaryEpisodeWatchService(Search, root);
            var results = await service.FindCandidatesAsync("Frieren", [], 1, "Dub", _ => { }, CancellationToken.None);
            Assert.Equal("magnet:?xt=urn:btih:seeded", results[0].MagnetLink);
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public async Task LaterEpisodesDoNotSuppressSeededFirstEpisodeOrSeasonFallback()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        var queries = new List<string>();
        Task<List<TorrentResult>> Search(string query, Action<string>? _, CancellationToken token)
        {
            queries.Add(query);
            if (query.EndsWith("Dual Audio", StringComparison.Ordinal))
                return Task.FromResult(new List<TorrentResult>
                {
                    new() { Title = "Frieren Season 1 [Dual Audio]", MagnetLink = "magnet:?xt=urn:btih:batch", Seeders = 34 }
                });
            return Task.FromResult(Enumerable.Range(2, 27).Select(i => new TorrentResult
            {
                Title = $"Frieren S01E{i:00} [English Dub]", MagnetLink = $"magnet:?xt=urn:btih:ep{i}", Seeders = 100
            }).ToList());
        }
        try
        {
            var service = new TemporaryEpisodeWatchService(Search, root);
            var results = await service.FindCandidatesAsync("Frieren", [], 1, "Dub", _ => { }, CancellationToken.None);
            Assert.Contains("Frieren Dual Audio", queries);
            Assert.Equal("Frieren Season 1 [Dual Audio]", Assert.Single(results).Title);
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public async Task DuplicateOrMagnetlessDubResultsDoNotSuppressDualAudioFallback()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        var queries = new List<string>();
        Task<List<TorrentResult>> Search(string query, Action<string>? _, CancellationToken token)
        {
            queries.Add(query);
            string magnet = query.EndsWith("Dual Audio", StringComparison.Ordinal) ? "magnet:?xt=urn:btih:dual" : "";
            return Task.FromResult(Enumerable.Range(0, 75).Select(i => new TorrentResult
            {
                Title = "Frieren - 01 [Dual Audio]", MagnetLink = magnet, InfoHash = "same", Seeders = i
            }).ToList());
        }
        try
        {
            var service = new TemporaryEpisodeWatchService(Search, root);
            var results = await service.FindCandidatesAsync("Frieren", [], 1, "Dub", _ => { }, CancellationToken.None);
            Assert.Contains("Frieren Dual Audio", queries);
            Assert.Contains("Frieren English Audio", queries);
            Assert.Equal("magnet:?xt=urn:btih:dual", Assert.Single(results).MagnetLink);
        }
        finally { Directory.Delete(root); }
    }
}
