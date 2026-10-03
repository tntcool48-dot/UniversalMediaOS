using System.IO;
using System.Linq;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Routing;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class TemporaryEpisodeWatchTests
{
    [Theory]
    [InlineData("[SubsPlease] Frieren - 01 (1080p).mkv", 1, true)]
    [InlineData("Frieren S01E02.mkv", 2, true)]
    [InlineData("Frieren Episode 11.mp4", 11, true)]
    [InlineData("Frieren - 01 (1080p).mkv", 2, false)]
    [InlineData("Frieren S02E01.mkv", 2, false)]
    [InlineData("Frieren - 01-02.mkv", 1, false)]
    [InlineData("Frieren - 01 - 02.mkv", 1, false)]
    [InlineData("Frieren Season 1.mkv", 1, false)]
    public void EpisodeFileRequiresOneExplicitMatchingEpisode(string name, int episode, bool expected)
    {
        Assert.Equal(expected, TemporaryEpisodeWatchService.EpisodeMatches(name, episode));
    }

    [Fact]
    public void TemporaryLeaseReleasesOnlyOnce()
    {
        int releases = 0;
        var lease = new TemporaryEpisodeLease(() => releases++);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, releases);
    }

    [Theory]
    [InlineData("[Group] Frieren S2 - 01.mkv", "Frieren Season 2", true)]
    [InlineData("[Group] Frieren 2nd Season - 01.mkv", "Frieren S2", true)]
    [InlineData("[Group] Frieren S1 - 01.mkv", "Frieren Season 2", false)]
    [InlineData("[Group] Frieren S2 - 01.mkv", "Frieren", false)]
    [InlineData("[Group] Frieren S2 - 01.mkv", "Other Anime Season 2", false)]
    public void TorrentSeriesRequiresTitleAndSeason(string torrent, string requested, bool expected)
    {
        Assert.Equal(expected, TemporaryEpisodeWatchService.TorrentMatchesSeries(torrent, requested));
    }

    [Fact]
    public void RestartDeletesInterruptedOwnedJobButPreservesActiveJob()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        string orphan = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string active = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(active);
        File.WriteAllText(Path.Combine(orphan, "partial.mkv"), "partial");
        File.WriteAllText(Path.Combine(active, "partial.mkv"), "partial");
        using (var handle = new FileStream(Path.Combine(active, ".active"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            _ = new TemporaryEpisodeWatchService(new DualTrackerRssParser(), root);
            Assert.False(Directory.Exists(orphan));
            Assert.True(Directory.Exists(active));
        }
        _ = new TemporaryEpisodeWatchService(new DualTrackerRssParser(), root);
        Assert.False(Directory.Exists(active));
        Directory.Delete(root);
    }

    [Fact]
    public void DownloadedSidecarAppearsInNativeCaptionChoicesAndSurvivesReload()
    {
        string directory = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string video = Path.Combine(directory, "episode-01.mp4");
        string caption = Path.Combine(directory, "episode-01.en.srt");
        File.WriteAllBytes(video, []);
        File.WriteAllText(caption, "1\n00:00:00,000 --> 00:00:01,000\nHello\n");
        try
        {
            using var player = new PlaybackViewModel(new DatabaseContext());
            player.LoadMedia(video, "Episode 1", localCaptionPaths: [caption]);
            Assert.True(player.CaptionsAvailable);
            Assert.Contains(player.CaptionOptions, choice => choice.Label.Contains("English"));
            player.LoadMedia(video, "Episode 1");
            Assert.True(player.CaptionsAvailable);
            Assert.Contains(player.CaptionOptions, choice => choice.Label.Contains("English"));
        }
        finally
        {
            File.Delete(caption);
            File.Delete(video);
            Directory.Delete(directory);
        }
    }
}
