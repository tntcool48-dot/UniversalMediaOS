using System.IO;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class AnimePlaybackProgressTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "anime-progress-" + Guid.NewGuid().ToString("N"));
    private readonly PlaybackProgressService _progress;
    private string LibraryPath => Path.Combine(_root, "library.json");
    public AnimePlaybackProgressTests()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _root);
        _progress = new(new(LibraryPath));
    }

    [Fact]
    public async Task SameTitleWithoutMalIdsRetainsIndependentCatalogPositionsAfterRestart()
    {
        using (var first = Player())
        {
            first.LoadEmbed("https://first.invalid/1", "Same title - Ep 1", "1", episodeContext: Context(100));
            first.ReportWebPlaybackProgress(90, 600, false);
        }
        using (var second = Player())
        {
            second.LoadEmbed("https://first.invalid/1", "Same title - Ep 1", "1", episodeContext: Context(200));
            ResumeDispatcherContentionTests.WaitForResumeLoad(second);
            Assert.False(second.TryConsumePendingWebResumePosition(out _));
            second.ReportWebPlaybackProgress(180, 600, false);
        }
        var restarted = new PlaybackProgressService(new(LibraryPath));
        using var reopened = Player(restarted);
        reopened.LoadEmbed("https://different.invalid/new-token", "Different display title", "1", episodeContext: Context(100, audio: "dub"));
        ResumeDispatcherContentionTests.WaitForResumeLoad(reopened);
        Assert.True(reopened.TryConsumePendingWebResumePosition(out double position));
        Assert.Equal(90, position);
        Assert.Equal(180, Read("anime:anilist:200", "episode:1"));
        Assert.Empty(await new AudiovisualLibraryService(LibraryPath).GetAllAsync());
    }

    [Fact]
    public void ExactCatalogMalAliasImportsItsEpisodeAndPreservesAllLegacyRows()
    {
        Seed("52991", "1", 123);
        Seed("52991", "2", 240);
        Seed("title:same title", "1", 999);
        using var player = Player();
        player.LoadEmbed("https://changed.invalid/embed", "Same title", "1", malId: 52991,
            episodeContext: Context(154587, 52991));
        ResumeDispatcherContentionTests.WaitForResumeLoad(player);
        Assert.True(player.TryConsumePendingWebResumePosition(out double position));
        Assert.Equal(123, position);
        Assert.Equal(123, Read("anime:anilist:154587", "episode:1"));
        Assert.Equal(123, Read("52991", "1"));
        Assert.Equal(240, Read("52991", "2"));
        Assert.Equal(999, Read("title:same title", "1"));
    }

    [Fact]
    public void CanonicalCompletionNeverReimportsAnOlderMalPosition()
    {
        Seed("52991", "1", 90);
        Seed("anime:anilist:154587", "episode:1", 0);
        using var player = Player();
        player.LoadEmbed("https://changed.invalid/embed", "Film", "1", episodeContext: Context(154587, 52991));
        ResumeDispatcherContentionTests.WaitForResumeLoad(player);
        Assert.False(player.TryConsumePendingWebResumePosition(out _));
        Assert.Equal(0, Read("anime:anilist:154587", "episode:1"));
        Assert.Equal(90, Read("52991", "1"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("13")]
    [InlineData("Special")]
    [InlineData("")]
    public void UnestablishedUnitDoesNotGuessAnEpisodeFromTheTitle(string episode)
    {
        Seed("title:same title", "1", 90);
        using var player = Player();
        player.LoadEmbed("https://first.invalid/1", "Same title - Ep 1", episode, episodeContext: Context(100));
        player.ReportWebPlaybackProgress(123, 600, false);
        player.Dispose();
        ResumeDispatcherContentionTests.WaitForResumeLoad(player);
        Assert.False(player.TryConsumePendingWebResumePosition(out _));
        using var database = new DatabaseContext();
        Assert.Single(database.ResumeStates.ToArray());
        Assert.Equal(90, Read("title:same title", "1"));
    }

    [Fact]
    public void OlderProviderCloseCannotOverwriteNewAnimeProgress()
    {
        using var older = Player();
        older.LoadEmbed("https://old.invalid/1", "Anime", "1", episodeContext: Context(100));
        older.ReportWebPlaybackProgress(90, 600, false);
        older.SetTabActive(false);
        using var current = Player();
        current.LoadEmbed("https://new.invalid/1", "Alias", "1", episodeContext: Context(100, audio: "dub"));
        ResumeDispatcherContentionTests.WaitForResumeLoad(current);
        Assert.True(current.TryConsumePendingWebResumePosition(out double position));
        Assert.Equal(90, position);
        current.ReportWebPlaybackProgress(240, 600, false);
        current.Dispose();
        older.Dispose();
        Assert.Equal(240, Read("anime:anilist:100", "episode:1"));
    }

    [Fact]
    public async Task AdjacentNavigationRetainsIdentityAudioAndIndependentPositions()
    {
        var context = new EpisodePlaybackContext(1, 12, (episode, _) => Task.FromResult<ResolvedEpisodePlayback?>(
            new("https://next.invalid/" + episode, "New alias - Ep " + episode, true)), "dub", new(100));
        using var player = Player();
        player.LoadEmbed("https://first.invalid/1", "First title", "1", episodeContext: context);
        player.ReportWebPlaybackProgress(90, 600, false);
        await player.NextEpisodeCommand.ExecuteAsync(null);
        Assert.Equal("dub", player.BrowserAudioPreference);
        ResumeDispatcherContentionTests.WaitForResumeLoad(player);
        Assert.False(player.TryConsumePendingWebResumePosition(out _));
        player.ReportWebPlaybackProgress(180, 600, false);
        player.Dispose();
        Assert.Equal(90, Read("anime:anilist:100", "episode:1"));
        Assert.Equal(180, Read("anime:anilist:100", "episode:2"));
    }

    [Fact]
    public void StreamAndExactTemporaryWatchUseTheSameUnitKey()
    {
        var stream = Context(100, 999, "sub").CreateProgressContext("2");
        var downloaded = new EpisodePlaybackContext(2, 2, (_, _) => Task.FromResult<ResolvedEpisodePlayback?>(null),
            "dub", new(100, 999)).CreateProgressContext("2");
        Assert.Equal(stream, downloaded);
        Assert.Equal("anime:anilist:100", stream!.WorkKey);
        Assert.Equal("episode:2", stream.UnitKey);
    }

    private PlaybackViewModel Player(PlaybackProgressService? progress = null) =>
        new(new DatabaseContext(), null, null, playbackProgress: progress ?? _progress);
    private static EpisodePlaybackContext Context(int id, int mal = 0, string audio = "sub") =>
        new(1, 12, (_, _) => Task.FromResult<ResolvedEpisodePlayback?>(null), audio, new(id, mal));
    private static void Seed(string work, string unit, double position)
    {
        using var database = new DatabaseContext();
        database.Database.EnsureCreated();
        database.SaveResumeState(work, unit, position);
    }
    private static double Read(string work, string unit)
    {
        using var database = new DatabaseContext();
        return database.GetResumeState(work, unit);
    }
    public void Dispose()
    {
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_root, "Roaming", "UniversalMediaOS", "media_os.db")};Cache=Shared;"))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _previousRoot);
        string target = Path.GetFullPath(_root);
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target)) Directory.Delete(target, true);
    }
}
