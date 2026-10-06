using System.IO;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class AudiovisualPlaybackProgressTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "av-progress-" + Guid.NewGuid().ToString("N"));
    private string LibraryPath => Path.Combine(_root, "library.json");
    private readonly AudiovisualLibraryService _library;
    private readonly PlaybackProgressService _progress;

    public AudiovisualPlaybackProgressTests()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _root);
        _library = new(LibraryPath);
        _progress = new(_library);
    }

    [Fact]
    public async Task PlayerProgressUpdatesLibraryWithoutTheOriginatingDetailsTab()
    {
        var context = Context("tt1160419");
        using (var player = Player())
        {
            player.LoadEmbed("https://first.invalid/embed", "Display title", audiovisualContext: context);
            player.ReportWebPlaybackProgress(123, 600, false);
        }
        var summary = Assert.Single(await new AudiovisualLibraryService(LibraryPath).GetAllAsync());
        Assert.Equal(context.WorkKey, summary.Key.WorkKey);
        Assert.Equal(123, summary.PositionSeconds);
        Assert.Equal(600, summary.DurationSeconds);
        using var restarted = Player(new(new(LibraryPath)));
        restarted.LoadEmbed("https://other-provider.invalid/new?token=changed", "Changed display title",
            audiovisualContext: Context("tt1160419", provider: "other"));
        ResumeDispatcherContentionTests.WaitForResumeLoad(restarted);
        Assert.True(restarted.TryConsumePendingWebResumePosition(out double position));
        Assert.Equal(123, position);
    }

    [Fact]
    public async Task OlderPausedPlayerCloseCannotOverwriteTheNewProviderPosition()
    {
        var context = Context("tt1160419");
        using var old = Player();
        old.LoadEmbed("https://old.invalid/embed", "Film", audiovisualContext: context);
        old.ReportWebPlaybackProgress(90, 600, false);
        old.SetTabActive(false);
        using (var newer = Player())
        {
            newer.LoadEmbed("https://new.invalid/embed", "Film", audiovisualContext: Context("tt1160419", provider: "new"));
            ResumeDispatcherContentionTests.WaitForResumeLoad(newer);
            Assert.True(newer.TryConsumePendingWebResumePosition(out double restored));
            Assert.Equal(90, restored);
            newer.ReportWebPlaybackProgress(240, 600, false);
        }
        old.Dispose();
        Assert.Equal(240, Read(context));
        Assert.Equal(240, (await _library.GetAsync(Key(context)))!.PositionSeconds);
    }

    [Fact]
    public async Task ExplicitResumeOfAnOlderTabCanBecomeTheCurrentProgressOwner()
    {
        var context = Context("tt1160419");
        var old = _progress.Open(context).Session;
        var newer = _progress.Open(context).Session;
        await Save(newer, 240);
        Assert.Null(_progress.Capture(old, 90, 600, false));
        old = _progress.TakeOwnership(old);
        await Save(old, 95);
        Assert.Equal(95, Read(context));
    }

    [Fact]
    public async Task FilmAdjacentEpisodesSeasonsAndEstablishedSpecialsSurviveStoreRestartSeparately()
    {
        var contexts = new[] { Context("tt1160419"), Context("tt0903747", 1, 1), Context("tt0903747", 1, 2),
            Context("tt0903747", 2, 1), Context("tt0903747", 0, 1) };
        for (int index = 0; index < contexts.Length; index++)
            await Save(_progress.Open(contexts[index]).Session, 90 + index * 40);
        var restarted = new PlaybackProgressService(new(LibraryPath));
        for (int index = 0; index < contexts.Length; index++)
            Assert.Equal(90 + index * 40, restarted.Open(contexts[index]).Position);
        using var database = new DatabaseContext();
        Assert.Equal(5, database.ResumeStates.Count());
    }

    [Fact]
    public async Task CapturedProgressCannotBeAppliedToAnotherUnitAndDelayedSummaryCannotReplaceIt()
    {
        var first = Context("tt0903747", 1, 1);
        var second = Context("tt0903747", 2, 1);
        var delayed = _progress.Capture(_progress.Open(first).Session, 90, 600, false)!;
        await Save(_progress.Open(second).Session, 180);
        await _progress.SaveAsync(delayed);
        Assert.Equal(90, Read(first));
        Assert.Equal(180, Read(second));
        var summary = (await _library.GetAsync(Key(second)))!;
        Assert.Equal(2, summary.LastSeasonNumber);
        Assert.Equal(180, summary.PositionSeconds);
    }

    [Fact]
    public async Task CompletionRejectsAnEarlierCapturedWriteAndOlderSession()
    {
        var context = Context("tt1160419");
        var old = _progress.Open(context).Session;
        var session = _progress.Open(context).Session;
        var pending = _progress.Capture(session, 90, 600, false)!;
        var complete = _progress.Capture(session, 600, 600, true)!;
        await _progress.SaveAsync(complete);
        await _progress.SaveAsync(pending);
        Assert.Null(_progress.Capture(old, 123, 600, false));
        Assert.Equal(0, Read(context));
        Assert.Equal(0, (await _library.GetAsync(Key(context)))!.PositionSeconds);
    }

    [Fact]
    public async Task SummaryFailurePreservesAuthoritativeResumeAndCanBeRepaired()
    {
        var context = Context("tt1160419");
        var failingLibrary = new AudiovisualLibraryService(LibraryPath, (_, _) => throw new IOException("controlled failure"));
        var progress = new PlaybackProgressService(failingLibrary);
        var session = progress.Open(context).Session;
        await progress.SaveAsync(progress.Capture(session, 123, 600, false)!);
        Assert.Equal(123, Read(context));
        Assert.False(File.Exists(LibraryPath));
        var repaired = _progress.Open(context);
        Assert.Equal(123, repaired.Position);
        await Save(repaired.Session, 124);
        Assert.Equal(124, (await _library.GetAsync(Key(context)))!.PositionSeconds);
    }

    [Fact]
    public async Task OpeningAnotherEpisodeNeverRelabelsThePriorEpisodesPosition()
    {
        var first = Context("tt0903747", 1, 1);
        var second = Context("tt0903747", 2, 1);
        await Save(_progress.Open(first).Session, 90);
        await _library.RecordOpenedAsync(Key(second), second.Title, "", 2, 1);
        var summary = (await _library.GetAsync(Key(second)))!;
        Assert.Equal(2, summary.LastSeasonNumber);
        Assert.Equal(0, summary.PositionSeconds);
        Assert.Equal(0, summary.DurationSeconds);
        Assert.Equal(90, Read(first));
    }

    [Fact]
    public void UnestablishedSpecialAndInvalidPositionsCannotCreateResumeRows()
    {
        var identity = new AudiovisualIdentity { ContentForm = AudiovisualContentForm.Series };
        var special = new AudiovisualPlaybackContext("av:local:unknown", identity,
            new() { SeasonNumber = 0, EpisodeNumber = 1 }, "Special", "", new());
        Assert.Throws<ArgumentException>(() => _progress.Open(special));
        var session = _progress.Open(Context("tt1160419")).Session;
        Assert.Null(_progress.Capture(session, double.NaN, 600, false));
        Assert.Null(_progress.Capture(session, 100, double.PositiveInfinity, false));
        using var database = new DatabaseContext();
        Assert.Empty(database.ResumeStates.ToArray());
    }

    [Fact]
    public void PlaybackReloadDropsQueuedCallbacksFromThePriorUnit()
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = Player();
            player.LoadEmbed("https://first.invalid/embed", "Pilot", audiovisualContext: Context("tt0903747", 1, 1));
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            bool staleCallbackRan = false;
            player.RunOnDispatcher(() => staleCallbackRan = true, dispatcher);
            player.LoadEmbed("https://second.invalid/embed", "Seven Thirty-Seven", audiovisualContext: Context("tt0903747", 2, 1));
            dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.False(staleCallbackRan);
            Assert.Equal(2, player.AudiovisualContext!.Unit.SeasonNumber);
            ResumeDispatcherContentionTests.WaitForResumeLoad(player);
        });
    }

    [Fact]
    public async Task ExplicitPagePlayLetsAnOlderProviderTabSaveAgain()
    {
        var context = Context("tt1160419");
        using var old = Player();
        old.LoadEmbed("https://old.invalid/embed", "Film", audiovisualContext: context);
        old.ReportWebPlaybackProgress(90, 600, false, paused: true);
        using var newer = Player();
        newer.LoadEmbed("https://new.invalid/embed", "Film", audiovisualContext: context);
        newer.ReportWebPlaybackProgress(240, 600, false, paused: true);
        newer.SetTabActive(false);
        old.ReportWebPlaybackAction("play", 95, 600);
        old.ReportWebPlaybackProgress(95, 600, false, paused: false);
        old.Dispose();
        newer.Dispose();
        Assert.Equal(95, Read(context));
        Assert.Equal(95, (await _library.GetAsync(Key(context)))!.PositionSeconds);
    }

    [Theory]
    [InlineData(null, null, 123, "Resume movie at 2:03")]
    [InlineData(1, 1, 123, "Resume S01E01 at 2:03")]
    [InlineData(2, 1, 240, "Resume S02E01 at 4:00")]
    [InlineData(0, 1, 123, "Resume S00E01 at 2:03")]
    [InlineData(null, 1, 123, "Resume Episode 1 at 2:03")]
    public void ResumeSummaryUsesTheEstablishedUnit(int? season, int? episode, double position, string expected)
    {
        var context = Context("tt0903747", season, episode);
        var entry = new AudiovisualLibraryEntry { Key = Key(context), Title = "Film or series",
            LastOpenedUtc = DateTimeOffset.UtcNow, LastSeasonNumber = season, LastEpisodeNumber = episode,
            PositionSeconds = position };
        Assert.StartsWith(expected, AudiovisualCatalogViewModel.FormatResumeText(entry));
    }

    [Fact]
    public async Task ClosingAPausedPriorEpisodeDoesNotReplaceTheLastWatchedUnit()
    {
        var first = Context("tt0903747", 1, 1);
        var second = Context("tt0903747", 2, 1);
        using var olderEpisode = Player();
        olderEpisode.LoadEmbed("https://first.invalid/embed", "Pilot", audiovisualContext: first);
        olderEpisode.ReportWebPlaybackProgress(90, 600, false);
        olderEpisode.SetTabActive(false);
        using var currentEpisode = Player();
        currentEpisode.LoadEmbed("https://second.invalid/embed", "Season two", audiovisualContext: second);
        currentEpisode.ReportWebPlaybackProgress(180, 600, false);
        currentEpisode.Dispose();
        olderEpisode.Dispose();
        Assert.Equal(90, Read(first));
        Assert.Equal(180, Read(second));
        var summary = (await _library.GetAsync(Key(second)))!;
        Assert.Equal(2, summary.LastSeasonNumber);
        Assert.Equal(180, summary.PositionSeconds);
    }

    private PlaybackViewModel Player(PlaybackProgressService? progress = null) =>
        new(new DatabaseContext(), null, null, playbackProgress: progress ?? _progress);
    private Task Save(PlaybackProgressSession session, double position) =>
        _progress.SaveAsync(_progress.Capture(session, position, 600, false)!);
    private static AudiovisualLibraryKey Key(AudiovisualPlaybackContext context) =>
        AudiovisualLibraryKey.Create(context.Identity, context.WorkKey);
    private static double Read(AudiovisualPlaybackContext context)
    {
        using var database = new DatabaseContext();
        return database.GetResumeState(context.WorkKey, context.UnitKey!);
    }
    private static AudiovisualPlaybackContext Context(string id, int? season = null, int? episode = null, string provider = "first")
    {
        var identity = new AudiovisualIdentity { Kind = season == null ? AudiovisualMediaKind.Movie : AudiovisualMediaKind.Television,
            ContentForm = season == null ? AudiovisualContentForm.Feature : AudiovisualContentForm.Series,
            PrimaryId = new("imdb", "title", id), Title = "Same title" };
        var unit = new AudiovisualUnit { SeasonNumber = season, EpisodeNumber = episode };
        return new(AudiovisualIdentityKeys.CreateWorkKey(identity), identity, unit, "Same title", "", new()
        { ProviderId = provider, Evidence = new() { Origin = SourceEvidenceOrigin.ProviderItem,
            Unit = unit, SpecialUnitEstablished = season == 0 } });
    }
    public void Dispose()
    {
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_root, "Roaming", "UniversalMediaOS", "media_os.db")};Cache=Shared;"))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _previousRoot);
        string target = Path.GetFullPath(_root);
        string parent = Path.GetFullPath(Path.GetTempPath()) + (Path.EndsInDirectorySeparator(Path.GetTempPath()) ? "" : Path.DirectorySeparatorChar);
        if (target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
            Directory.Delete(target, true);
    }
}
