using System.IO;
using Microsoft.Data.Sqlite;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class ResumeJournalInvariantTests : IDisposable
{
    private readonly string? _previousRoot =
        Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "resume-journal-" + Guid.NewGuid().ToString("N"));
    private string DatabaseA => Path.Combine(_root, "profile-a", "media_os.db");
    private string DatabaseB => Path.Combine(_root, "profile-b", "media_os.db");
    private string LibraryPath => Path.Combine(_root, "library.json");
    private static readonly PlaybackProgressContext Film = new("av:wikidata:item:Q925587", "film");

    public ResumeJournalInvariantTests()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _root);
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task CommittedPendingJournalCannotRollBackANewerOwnerOnRestart()
    {
        var neighbor = new PlaybackProgressContext("av:tvmaze:show:169", "season:1:episode:1");
        Seed(DatabaseA, (Film, 10), (neighbor, 57));
        var progress = Service(DatabaseA);
        var older = progress.BeginDurableSession(Film);
        var oldReceipt = Assert.IsType<PlaybackProgressReceipt>(
            await progress.StageDurableObservationAsync(older, Observe(90)));
        await progress.FlushDurableObservationsAsync();

        // A successful database commit without acknowledgement reproduces the
        // process-ending gap while leaving the accepted journal entry intact.
        using (var database = new DatabaseContext(DatabaseA))
            Assert.True(await database.ApplyJournaledResumeAsync(oldReceipt.WorkKey, oldReceipt.UnitKey,
                oldReceipt.Position, oldReceipt.JournalId, oldReceipt.Revision));

        var newer = progress.BeginDurableSession(Film);
        var newReceipt = Assert.IsType<PlaybackProgressReceipt>(
            await progress.StageDurableObservationAsync(newer, Observe(240)));
        await progress.FlushDurableObservationsAsync();
        using (var database = new DatabaseContext(DatabaseA))
            Assert.True(await database.ApplyJournaledResumeAsync(newReceipt.WorkKey, newReceipt.UnitKey,
                newReceipt.Position, newReceipt.JournalId, newReceipt.Revision));

        var restarted = Service(DatabaseA);
        Assert.Equal(240, restarted.Open(Film).Position);
        await Replay(restarted, DatabaseA);
        await progress.CommitDurableObservationAsync(oldReceipt);
        AssertPositions(DatabaseA, (Film, 240), (neighbor, 57));
    }

    [Fact]
    public async Task NewOwnerRejectsLateOldCallbacksButRetainsPreviouslyAcceptedLegacyProgress()
    {
        var legacy = new PlaybackProgressContext("52991", "1");
        var nextEpisode = new PlaybackProgressContext("52991", "2");
        Seed(DatabaseA, (legacy, 10), (nextEpisode, 57));
        var progress = Service(DatabaseA);
        var older = progress.BeginDurableSession(legacy);
        Assert.IsType<PlaybackProgressReceipt>(
            await progress.StageDurableObservationAsync(older, Observe(90)));
        await progress.FlushDurableObservationsAsync();

        var newer = progress.BeginDurableSession(legacy);
        Assert.Null(await progress.StageDurableObservationAsync(older, Observe(999)));
        await progress.FlushDurableObservationsAsync();
        await Replay(Service(DatabaseA), DatabaseA);
        AssertPositions(DatabaseA, (legacy, 90), (nextEpisode, 57));

        var currentReceipt = Assert.IsType<PlaybackProgressReceipt>(
            await progress.StageDurableObservationAsync(newer, Observe(240)));
        await progress.FlushDurableObservationsAsync();
        await progress.CommitDurableObservationAsync(currentReceipt);
        AssertPositions(DatabaseA, (legacy, 240), (nextEpisode, 57));
    }

    [Fact]
    public async Task ReplayedCompletionKeepsZeroAndProtectsFilmSeasonEpisodeAndLegacyNeighbors()
    {
        var episode = new PlaybackProgressContext("av:tvmaze:show:169", "season:1:episode:1");
        var nextEpisode = new PlaybackProgressContext("av:tvmaze:show:169", "season:1:episode:2");
        var nextSeason = new PlaybackProgressContext("av:tvmaze:show:169", "season:2:episode:1");
        var legacy = new PlaybackProgressContext("52991", "1");
        Seed(DatabaseA, (episode, 123), (nextEpisode, 57), (nextSeason, 89), (Film, 347), (legacy, 73));
        var progress = Service(DatabaseA);
        var claim = progress.BeginDurableSession(episode);
        Assert.IsType<PlaybackProgressReceipt>(
            await progress.StageDurableObservationAsync(claim, Observe(600, completed: true)));
        await progress.FlushDurableObservationsAsync();

        await Replay(Service(DatabaseA), DatabaseA);
        await Replay(Service(DatabaseA), DatabaseA);
        AssertPositions(DatabaseA, (episode, 0), (nextEpisode, 57), (nextSeason, 89), (Film, 347), (legacy, 73));
    }

    [Fact]
    public async Task BoundDestinationSurvivesFactoryChangeAndDatabaseContextDisposal()
    {
        Seed(DatabaseA, (Film, 10));
        Seed(DatabaseB, (Film, 20));
        string currentDestination = DatabaseA;
        var progress = new PlaybackProgressService(new AudiovisualLibraryService(LibraryPath),
            () => new DatabaseContext(currentDestination));
        PlaybackProgressClaim claim;
        using (var bound = new DatabaseContext(DatabaseA))
            claim = progress.BeginDurableSession(Film, bound);

        currentDestination = DatabaseB;
        var receipt = Assert.IsType<PlaybackProgressReceipt>(
            await progress.StageDurableObservationAsync(claim, Observe(180)));
        await progress.FlushDurableObservationsAsync();
        await progress.CommitDurableObservationAsync(receipt);
        await Replay(Service(DatabaseB), DatabaseB);
        AssertPositions(DatabaseA, (Film, 180));
        AssertPositions(DatabaseB, (Film, 20));
    }

    [Fact]
    public async Task DamagedJournalIsRetainedAsideWithoutBlockingReadsOrLaterProgress()
    {
        var neighbor = new PlaybackProgressContext("av:tvmaze:show:169", "season:1:episode:1");
        Seed(DatabaseA, (Film, 90), (neighbor, 57));
        string journal = DatabaseA + ".resume-pending.json";
        byte[] damaged = System.Text.Encoding.UTF8.GetBytes("{\"unknown recovery document\":");
        await File.WriteAllBytesAsync(journal, damaged);

        // Unreadable records are never applied, and they do not hide the saved row.
        var restarted = Service(DatabaseA);
        Assert.Equal(90, restarted.Open(Film).Position);
        string retained = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(DatabaseA)!,
            "media_os.db.resume-pending.json.damaged-*"));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(retained));
        AssertPositions(DatabaseA, (Film, 90), (neighbor, 57));

        var claim = restarted.BeginDurableSession(Film);
        var receipt = Assert.IsType<PlaybackProgressReceipt>(
            await restarted.StageDurableObservationAsync(claim, Observe(240)));
        await restarted.FlushDurableObservationsAsync();
        await restarted.CommitDurableObservationAsync(receipt);
        AssertPositions(DatabaseA, (Film, 240), (neighbor, 57));
    }

    [Fact]
    public async Task PendingJournalReplaysAfterItsProfileFolderMoves()
    {
        var neighbor = new PlaybackProgressContext("av:tvmaze:show:169", "season:1:episode:1");
        Seed(DatabaseA, (Film, 90), (neighbor, 57));
        var progress = Service(DatabaseA);
        var claim = progress.BeginDurableSession(Film);
        Assert.IsType<PlaybackProgressReceipt>(await progress.StageDurableObservationAsync(claim, Observe(240)));
        await progress.FlushDurableObservationsAsync();
        SqliteConnection.ClearAllPools();

        // A moved or aliased profile names the same journal beside its database.
        string moved = Path.Combine(_root, "profile-moved", "media_os.db");
        Directory.Move(Path.GetDirectoryName(DatabaseA)!, Path.GetDirectoryName(moved)!);
        Assert.Equal(240, Service(moved).Open(Film).Position);
        AssertPositions(moved, (Film, 240), (neighbor, 57));
    }

    [Fact]
    public async Task RecreatedJournalStillSavesAfterAnEarlierJournalWasLost()
    {
        Seed(DatabaseA, (Film, 10));
        var progress = Service(DatabaseA);
        var first = Assert.IsType<PlaybackProgressReceipt>(
            await progress.StageDurableObservationAsync(progress.BeginDurableSession(Film), Observe(90)));
        await progress.CommitDurableObservationAsync(first);
        AssertPositions(DatabaseA, (Film, 90));

        File.Delete(DatabaseA + ".resume-pending.json");
        var restarted = Service(DatabaseA);
        var second = Assert.IsType<PlaybackProgressReceipt>(
            await restarted.StageDurableObservationAsync(restarted.BeginDurableSession(Film), Observe(240)));
        Assert.NotEqual(first.JournalId, second.JournalId);
        await restarted.CommitDurableObservationAsync(second);
        AssertPositions(DatabaseA, (Film, 240));
    }

    [Fact]
    public async Task ReplayFromAnotherLibraryAppliesThePositionWithoutWritingThatSummary()
    {
        var episode = new PlaybackProgressContext("av:tvmaze:show:169", "season:1:episode:2",
            new AudiovisualPlaybackContext("av:tvmaze:show:169", new AudiovisualIdentity
            {
                PrimaryId = new("tvmaze", "show", "169"), Kind = AudiovisualMediaKind.Television,
                ContentForm = AudiovisualContentForm.Series, Title = "Breaking Bad"
            }, new() { SeasonNumber = 1, EpisodeNumber = 2 }, "Breaking Bad", "", new()));
        Seed(DatabaseA, (episode, 30));
        string otherLibrary = Path.Combine(_root, "other-library.json");
        var other = new PlaybackProgressService(new AudiovisualLibraryService(otherLibrary),
            () => new DatabaseContext(DatabaseA));
        Assert.IsType<PlaybackProgressReceipt>(
            await other.StageDurableObservationAsync(other.BeginDurableSession(episode), Observe(300)));
        await other.FlushDurableObservationsAsync();

        await Replay(Service(DatabaseA), DatabaseA);
        AssertPositions(DatabaseA, (episode, 300));
        Assert.Empty(await new AudiovisualLibraryService(LibraryPath).GetAllAsync());
        Assert.False(File.Exists(otherLibrary));
    }

    [Fact]
    public async Task JournaledPositionsApplyToAnOlderRollbackJournalDatabaseWithoutTheUnitIndex()
    {
        // Created outside the app: no WAL header and no unique (MediaId, EpisodeId) index.
        var episode = new PlaybackProgressContext("av:tvmaze:show:169", "season:1:episode:1");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabaseA)!);
        using (var connection = new SqliteConnection($"Data Source={DatabaseA};Pooling=False"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE ResumeStates (Id INTEGER NOT NULL CONSTRAINT PK_ResumeStates PRIMARY KEY AUTOINCREMENT,
                    MediaId TEXT NOT NULL, EpisodeId TEXT NOT NULL, PositionSeconds REAL NOT NULL);
                INSERT INTO ResumeStates (MediaId, EpisodeId, PositionSeconds) VALUES ('av:wikidata:item:Q925587', 'film', 10);
                """;
            create.ExecuteNonQuery();
        }
        var progress = Service(DatabaseA);
        foreach (var (context, position) in new[] { (Film, 240.0), (episode, 57.0) })
        {
            var receipt = Assert.IsType<PlaybackProgressReceipt>(
                await progress.StageDurableObservationAsync(progress.BeginDurableSession(context), Observe(position)));
            await progress.CommitDurableObservationAsync(receipt);
        }
        AssertPositions(DatabaseA, (Film, 240), (episode, 57));
    }

    private PlaybackProgressService Service(string databasePath) =>
        new(new AudiovisualLibraryService(LibraryPath), () => new DatabaseContext(databasePath));

    private static PlaybackProgressObservation Observe(double position, bool completed = false) =>
        new(position, 600, completed, DateTimeOffset.UtcNow);

    private static async Task Replay(PlaybackProgressService progress, string databasePath)
    {
        using var database = new DatabaseContext(databasePath);
        await progress.RunSerializedPersistenceAsync(() => progress.ReplayDurableObservationsAsync(database));
    }

    private static void Seed(string databasePath,
        params (PlaybackProgressContext Context, double Position)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using var database = new DatabaseContext(databasePath);
        database.Database.EnsureCreated();
        foreach (var entry in entries)
            database.SaveResumeState(entry.Context.WorkKey, entry.Context.UnitKey, entry.Position);
    }

    private static void AssertPositions(string databasePath,
        params (PlaybackProgressContext Context, double Position)[] expected)
    {
        using var database = new DatabaseContext(databasePath);
        var rows = database.ResumeStates.ToArray();
        Assert.Equal(expected.Length, rows.Length);
        foreach (var entry in expected)
        {
            var row = Assert.Single(rows, candidate =>
                candidate.MediaId == entry.Context.WorkKey && candidate.EpisodeId == entry.Context.UnitKey);
            Assert.Equal(entry.Position, row.PositionSeconds);
        }
    }

    public void Dispose()
    {
        // Tests run serially; release every pooled handle before removing the profile.
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _previousRoot);
        string target = Path.GetFullPath(_root);
        string temporaryDirectory = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (target.StartsWith(temporaryDirectory, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(target))
            Directory.Delete(target, true);
    }
}

