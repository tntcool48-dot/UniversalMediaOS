using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace UniversalMediaOS.Tests.E2E;

public sealed class ResumeDispatcherContentionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PausingAnInactiveTabDoesNotWaitForASqliteWriter(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = CreatePlayer(profile, catalog);
            var (work, unit) = SaveInitial(player, profile);
            player.PlaybackTime = 120_000;
            var elapsed = Stopwatch.StartNew();
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 1500))
            {
                elapsed.Restart();
                player.SetTabActive(false);
                elapsed.Stop();
            }
            output.WriteLine($"{(catalog ? "Catalog" : "Legacy")} inactive pause returned in {elapsed.Elapsed.TotalMilliseconds:0.000} ms.");
            Assert.False(player.IsPlaying);
            AssertPersisted(work, unit, 120);
            Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 0, 500);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CloseIsBoundedAndCapturedProgressSurvivesDeferredDatabaseDisposal(bool catalog, bool completed)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = CreatePlayer(profile, catalog, out var ownedDatabase);
            var (work, unit) = SaveInitial(player, profile);
            using (var seed = new DatabaseContext()) seed.SaveResumeState(work, "adjacent-unit", 57);
            player.PlaybackTime = 120_000;
            var elapsed = Stopwatch.StartNew();
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
            {
                // Hold the writer beyond the two-second final flush deadline.
                elapsed.Restart();
                if (completed) player.ReportWebPlaybackProgress(600, 600, true, paused: true);
                player.Dispose();
                elapsed.Stop();
                Assert.True(player.IsDisposed);
                Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 1500, 3000);
                Assert.False(ownedDatabase.Disposed, "Accepted saves must retain the database while the writer remains locked.");
            }
            output.WriteLine($"{(catalog ? "Catalog" : "Legacy")} {(completed ? "completion/close" : "close")} returned in {elapsed.Elapsed.TotalMilliseconds:0.000} ms.");
            AssertPersisted(work, unit, completed ? 0 : 120);
            Assert.True(SpinWait.SpinUntil(() => ownedDatabase.Disposed, TimeSpan.FromSeconds(5)),
                "The retained database must be disposed after every accepted save ends.");
            using var verify = new DatabaseContext();
            Assert.Equal(57, verify.GetResumeState(work, "adjacent-unit"));
        });
    }

    [Fact]
    public async Task QueuedCatalogSaveKeepsTheProfileCapturedBeforeTheDataRootChanges()
    {
        using var profile = new IsolatedProfile();
        var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
        var session = progress.Open(Context()).Session;
        using (var database = new DatabaseContext())
        {
            profile.ConnectionString = database.Database.GetConnectionString()!;
            database.SaveResumeState(session.Context.WorkKey, session.Context.UnitKey!, 90);
        }
        Task first;
        Task queued;
        using (var writer = new WriterLock(profile.ConnectionString!, session.Context.WorkKey, session.Context.UnitKey!, 5000))
        {
            first = progress.SaveAsync(progress.Capture(session, 120, 600, false)!);
            queued = progress.SaveAsync(progress.Capture(session, 180, 600, false)!);
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, Path.Combine(profile.Root, "other-profile"));
        }
        await Task.WhenAll(first, queued);
        Assert.False(Directory.Exists(Path.Combine(profile.Root, "other-profile")), "Workers must use the captured connection, not create another profile.");
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, profile.Root);
        AssertPersisted(session.Context.WorkKey, session.Context.UnitKey!, 180);
    }

    private static PlaybackViewModel CreatePlayer(IsolatedProfile profile, bool catalog)
        => CreatePlayer(profile, catalog, out _);

    private static PlaybackViewModel CreatePlayer(IsolatedProfile profile, bool catalog, out TrackingDatabaseContext database)
    {
        var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
        database = new TrackingDatabaseContext();
        var player = new PlaybackViewModel(database, null, null, playbackProgress: progress);
        player.LoadEmbed("https://fixture.invalid/embed", "Contention fixture", audiovisualContext: catalog ? Context() : null);
        return player;
    }

    private static AudiovisualPlaybackContext Context() => new("av:fixture:film:contention",
        new AudiovisualIdentity { PrimaryId = new("fixture", "film", "contention"),
            Kind = AudiovisualMediaKind.Movie, ContentForm = AudiovisualContentForm.Feature,
            Title = "Contention fixture" }, new(), "Contention fixture", "", new());

    private static (string Work, string Unit) SaveInitial(PlaybackViewModel player, IsolatedProfile profile)
    {
        player.ReportWebPlaybackProgress(90, 600, false, paused: true);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            using var database = new DatabaseContext();
            return database.ResumeStates.AsNoTracking().Any(row => row.PositionSeconds == 90);
        }, TimeSpan.FromSeconds(5)), "Arrange waits for the asynchronous initial save.");
        using var initialDatabase = new DatabaseContext();
        profile.ConnectionString = initialDatabase.Database.GetConnectionString()!;
        var initial = Assert.Single(initialDatabase.ResumeStates.AsNoTracking().ToArray());
        Assert.Equal(90, initial.PositionSeconds);
        return (initial.MediaId, initial.EpisodeId);
    }

    private static void AssertPersisted(string work, string unit, double expected) =>
        Assert.True(SpinWait.SpinUntil(() =>
        {
            using var database = new DatabaseContext();
            return database.GetResumeState(work, unit) == expected;
        }, TimeSpan.FromSeconds(5)), $"Queued exact-unit position {expected} must persist after the writer releases.");

    private sealed class WriterLock : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Task _writer;

        public WriterLock(string connectionString, string work, string unit, int maximumHoldMilliseconds)
        {
            using var locked = new ManualResetEventSlim();
            _writer = Task.Run(() =>
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE ResumeStates SET PositionSeconds = PositionSeconds WHERE MediaId = $work AND EpisodeId = $unit";
                command.Parameters.AddWithValue("$work", work);
                command.Parameters.AddWithValue("$unit", unit);
                Assert.Equal(1, command.ExecuteNonQuery());
                locked.Set();
                _release.Wait(TimeSpan.FromMilliseconds(maximumHoldMilliseconds));
                transaction.Commit();
            });
            Assert.True(locked.Wait(TimeSpan.FromSeconds(3)), "Fixture writer must acquire the SQLite lock before the timed UI call.");
        }

        public void Dispose()
        {
            _release.Set();
            try { _writer.GetAwaiter().GetResult(); }
            finally { _release.Dispose(); }
        }
    }

    private sealed class IsolatedProfile : IDisposable
    {
        private readonly string? _previousRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        private readonly string _tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests"));
        public string Root { get; }
        public string? ConnectionString { get; set; }

        public IsolatedProfile()
        {
            Root = Path.Combine(_tempRoot, "resume-contention-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, Root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _previousRoot);
            if (ConnectionString != null)
            {
                using var ownPool = new SqliteConnection(ConnectionString);
                SqliteConnection.ClearPool(ownPool);
            }
            var ownedDirectory = new DirectoryInfo(Root);
            Assert.StartsWith(_tempRoot + Path.DirectorySeparatorChar, ownedDirectory.FullName, StringComparison.OrdinalIgnoreCase);
            Assert.Null(ownedDirectory.LinkTarget);
            if (ownedDirectory.Exists) ownedDirectory.Delete(recursive: true);
        }
    }

    private sealed class TrackingDatabaseContext : DatabaseContext
    {
        private int _disposed;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public override void Dispose()
        {
            base.Dispose();
            Volatile.Write(ref _disposed, 1);
        }
    }
}
